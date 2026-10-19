#if WINDOWS_HOST
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Latest-frame window via user32/gdi32. Compiled only for a Windows host build
/// (or -p:ForceWindowsHost=true). Linux Native AOT does not link user32.
/// </summary>
internal static partial class WindowsDisplayWindow
{
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmTimer = 0x0113;
    private const uint WmPaint = 0x000F;
    private const uint WmCreate = 0x0001;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;
    private const int GwlpUserData = -21;
    private const uint SrcCopy = 0x00CC0020;
    private const int ColorOnColor = 3;
    private const int Halftone = 4;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const int ChipLeft = 8;
    private const int ChipTop = 8;

    public static IAsyncDisposable Start(
        FrameSlot frames,
        string title,
        IAirPlayLog log,
        int tapClockScreenWidth,
        int tapClockScreenHeight,
        CancellationToken cancellationToken)
    {
        var window = new Window(frames, title, log, tapClockScreenWidth, tapClockScreenHeight, cancellationToken);
        window.Start();
        return window;
    }

    private sealed class Window : IAsyncDisposable
    {
        private readonly FrameSlot _frames;
        private readonly string _windowTitle;
        private readonly IAirPlayLog _log;
        private readonly CancellationToken _cancellation;
        private readonly byte[] _header = new byte[40];
        private readonly int _tapClockScreenWidth;
        private readonly int _tapClockScreenHeight;
        private readonly bool _tapClockScreen;
        private GCHandle _self;
        private Task _loop = Task.CompletedTask;
        private nint _hwnd;
        private int _disposed;
        private int _pointerX;
        private int _pointerY;
        private bool _pointerInside;
        private bool _leaveArmed;
        private bool _hoverVisible;
        private int _hoverX;
        private int _hoverY;
        private string? _hoverLabel;
        private nint _hoverFont;
        private bool _hoverFontOwned;

        public Window(
            FrameSlot frames,
            string title,
            IAirPlayLog log,
            int tapClockScreenWidth,
            int tapClockScreenHeight,
            CancellationToken cancellationToken)
        {
            _frames = frames;
            _windowTitle = "AirPlayServer.NET — " + title;
            _log = log;
            _tapClockScreen = tapClockScreenWidth > 0 && tapClockScreenHeight > 0;
            _tapClockScreenWidth = _tapClockScreen ? tapClockScreenWidth : 0;
            _tapClockScreenHeight = _tapClockScreen ? tapClockScreenHeight : 0;
            _cancellation = cancellationToken;
        }

        public void Start()
        {
            _self = GCHandle.Alloc(this);
            _loop = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var hwnd = Volatile.Read(ref _hwnd);
            if (hwnd != 0)
            {
                PostMessageW(hwnd, WmClose, 0, 0);
            }

            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }

        private void Run()
        {
            try
            {
                var className = Marshal.StringToHGlobalUni("AirPlayServer.NET.Mirror");
                var wndClass = new WndClassW
                {
                    Style = 0x0003,
                    WndProc = WindowProcedurePointer(),
                    Instance = GetModuleHandleW(0),
                    Background = 6,
                    Cursor = LoadCursorW(0, 32512),
                    ClassName = className,
                };
                if (RegisterClassW(ref wndClass) == 0 && Marshal.GetLastPInvokeError() != 1410)
                {
                    _log.Log(AirPlayLogLevel.Warning, "Display window class was not registered (" + Marshal.GetLastPInvokeError() + ").");
                    return;
                }

                var hwnd = CreateWindowExW(
                    0,
                    "AirPlayServer.NET.Mirror",
                    _windowTitle,
                    WsOverlappedWindow | WsVisible,
                    unchecked((int)0x80000000),
                    unchecked((int)0x80000000),
                    480,
                    1040,
                    0,
                    0,
                    wndClass.Instance,
                    GCHandle.ToIntPtr(_self));
                if (hwnd == 0)
                {
                    _log.Log(AirPlayLogLevel.Warning, "Display window was not created (" + Marshal.GetLastPInvokeError() + ").");
                    return;
                }

                Volatile.Write(ref _hwnd, hwnd);
                ShowWindow(hwnd, 5);
                while (!_cancellation.IsCancellationRequested)
                {
                    var status = GetMessageW(out var message, 0, 0, 0);
                    if (status <= 0)
                    {
                        break;
                    }

                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception ex)
            {
                _log.Log(AirPlayLogLevel.Warning, "Display window stopped: " + ex.Message);
            }
        }

        private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case WmCreate:
                    SetTimer(hwnd, 1, 33, 0);
                    return 0;
                case WmTimer:
                case WmPaint:
                    if (message == WmPaint)
                    {
                        ValidateRect(hwnd, 0);
                    }

                    Blit(hwnd);
                    return 0;
                case WmMouseMove:
                    // Button messages stay with DefWindowProc. Clicks in this window are not sent to the phone.
                    OnPointerMove(hwnd, lParam);
                    return 0;
                case WmMouseLeave:
                    OnPointerLeave(hwnd);
                    return 0;
                case WmDestroy:
                    ReleaseHoverFont();
                    PostQuitMessage(0);
                    return 0;
                default:
                    return DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }

        private void Blit(nint hwnd)
        {
            if (!_frames.TryRead(out var width, out var height, out _, out var pixels, out _))
            {
                ClearHover(hwnd);
                return;
            }

            if (GetClientRect(hwnd, out var rect) == 0)
            {
                return;
            }

            var clientWidth = rect.Right - rect.Left;
            var clientHeight = rect.Bottom - rect.Top;
            var destination = FrameCoordinateMap.Letterbox(clientWidth, clientHeight, width, height);
            if (destination.Width <= 0 || destination.Height <= 0)
            {
                return;
            }

            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(0), 40);
            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(8), -height);
            BinaryPrimitives.WriteInt16LittleEndian(_header.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(_header.AsSpan(14), 32);
            var pixelsPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var headerPin = GCHandle.Alloc(_header, GCHandleType.Pinned);
            var dc = GetDC(hwnd);
            try
            {
                if (dc == 0)
                {
                    return;
                }

                if (destination.X != 0 || destination.Y != 0
                    || destination.Width != clientWidth || destination.Height != clientHeight)
                {
                    ClearClient(dc, clientWidth, clientHeight);
                }

                var filtering = destination.Width != width || destination.Height != height;
                SetStretchBltMode(dc, filtering ? Halftone : ColorOnColor);
                if (filtering)
                {
                    // HALFTONE is misaligned unless the brush origin is set.
                    SetBrushOrgEx(dc, 0, 0, 0);
                }

                StretchDIBits(
                    dc,
                    destination.X,
                    destination.Y,
                    destination.Width,
                    destination.Height,
                    0,
                    0,
                    width,
                    height,
                    pixelsPin.AddrOfPinnedObject(),
                    headerPin.AddrOfPinnedObject(),
                    0,
                    SrcCopy);
                // Hover uses this same rectangle, then TapClock's image→screen scale.
                // A client point outside the painted picture stays unmapped.
                ApplyHover(hwnd, width, height, destination);
                DrawHoverChip(dc);
            }
            finally
            {
                if (dc != 0)
                {
                    ReleaseDC(hwnd, dc);
                }

                headerPin.Free();
                pixelsPin.Free();
            }
        }

        private void OnPointerMove(nint hwnd, nint lParam)
        {
            _pointerInside = true;
            FrameCoordinateMap.ClientPoint(lParam, out _pointerX, out _pointerY);
            if (!_leaveArmed)
            {
                var track = new TrackMouseEventInfo
                {
                    Size = Marshal.SizeOf<TrackMouseEventInfo>(),
                    Flags = TmeLeave,
                    Hwnd = hwnd,
                };
                _leaveArmed = TrackMouseEvent(ref track) != 0;
            }

            UpdateHoverReadout(hwnd);
        }

        private void OnPointerLeave(nint hwnd)
        {
            _leaveArmed = false;
            _pointerInside = false;
            UpdateHoverReadout(hwnd);
        }

        private void UpdateHoverReadout(nint hwnd)
        {
            if (!_pointerInside || !_frames.TryRead(out var width, out var height, out _, out _, out _))
            {
                ClearHover(hwnd);
                return;
            }

            if (GetClientRect(hwnd, out var rect) == 0)
            {
                ClearHover(hwnd);
                return;
            }

            ApplyHover(hwnd, width, height, FrameCoordinateMap.Letterbox(rect.Right - rect.Left, rect.Bottom - rect.Top, width, height));
        }

        private static void ClearClient(nint dc, int width, int height)
        {
            var client = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
            var brush = CreateSolidBrush(0);
            if (brush == 0)
            {
                return;
            }

            FillRect(dc, ref client, brush);
            DeleteObject(brush);
        }

        private void ApplyHover(nint hwnd, int frameWidth, int frameHeight, FrameCoordinateMap.FrameDestination destination)
        {
            if (!_pointerInside
                || !FrameCoordinateMap.TryMapClientToScreen(
                    _pointerX,
                    _pointerY,
                    destination,
                    frameWidth,
                    frameHeight,
                    _tapClockScreenWidth,
                    _tapClockScreenHeight,
                    out var screenX,
                    out var screenY))
            {
                ClearHover(hwnd);
                return;
            }

            if (_hoverVisible && _hoverX == screenX && _hoverY == screenY)
            {
                return;
            }

            _hoverVisible = true;
            _hoverX = screenX;
            _hoverY = screenY;
            _hoverLabel = FrameCoordinateMap.FormatHoverReadout(screenX, screenY, _tapClockScreen);
            // Coordinates lead the title so a narrow mirror window does not clip them.
            SetWindowTextW(hwnd, _hoverLabel + " — " + _windowTitle);
        }

        private void ClearHover(nint hwnd)
        {
            _hoverLabel = null;
            if (!_hoverVisible)
            {
                return;
            }

            _hoverVisible = false;
            SetWindowTextW(hwnd, _windowTitle);
        }

        private void DrawHoverChip(nint dc)
        {
            var text = _hoverLabel;
            if (text is null)
            {
                return;
            }

            var font = EnsureHoverFont();
            var previous = font == 0 ? 0 : SelectObject(dc, font);
            try
            {
                if (GetTextExtentPoint32W(dc, text, text.Length, out var size) == 0)
                {
                    return;
                }

                var width = size.Cx + 16;
                var height = size.Cy + 10;
                var box = new Rect
                {
                    Left = ChipLeft,
                    Top = ChipTop,
                    Right = ChipLeft + width,
                    Bottom = ChipTop + height,
                };
                var brush = CreateSolidBrush(0x00161616);
                if (brush != 0)
                {
                    FillRect(dc, ref box, brush);
                    DeleteObject(brush);
                }

                SetBkMode(dc, 1);
                SetTextColor(dc, 0x00F2F2F2);
                TextOutW(dc, ChipLeft + 8, ChipTop + 4, text, text.Length);
            }
            finally
            {
                if (previous != 0)
                {
                    SelectObject(dc, previous);
                }
            }
        }

        private nint EnsureHoverFont()
        {
            if (_hoverFont != 0)
            {
                return _hoverFont;
            }

            var created = CreateFontW(-18, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei");
            if (created != 0)
            {
                _hoverFont = created;
                _hoverFontOwned = true;
                return created;
            }

            _hoverFont = GetStockObject(17);
            return _hoverFont;
        }

        private void ReleaseHoverFont()
        {
            if (_hoverFontOwned && _hoverFont != 0)
            {
                DeleteObject(_hoverFont);
            }

            _hoverFont = 0;
            _hoverFontOwned = false;
        }

        private static unsafe nint WindowProcedurePointer()
            => (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc;

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
        {
            if (message == WmCreate)
            {
                SetWindowLongPtrW(hwnd, GwlpUserData, Marshal.ReadIntPtr(lParam));
            }

            var data = GetWindowLongPtrW(hwnd, GwlpUserData);
            if (data == 0)
            {
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }

            var window = GCHandle.FromIntPtr(data).Target as Window;
            return window?.OnMessage(hwnd, message, wParam, lParam) ?? DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassW
    {
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public int Size;
        public uint Flags;
        public nint Hwnd;
        public uint HoverTime;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static partial ushort RegisterClassW(ref WndClassW wndClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    private static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessageW(out NativeMsg message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static partial int TranslateMessage(ref NativeMsg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessageW(ref NativeMsg message);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    private static partial nuint SetTimer(nint hwnd, nuint eventId, uint milliseconds, nint procedure);

    [LibraryImport("user32.dll", EntryPoint = "ValidateRect")]
    private static partial int ValidateRect(nint hwnd, nint rect);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    private static partial int GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static partial nint LoadCursorW(nint instance, nint cursorName);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTextW(nint hwnd, string text);

    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    private static partial int TrackMouseEvent(ref TrackMouseEventInfo track);

    [LibraryImport("user32.dll", EntryPoint = "FillRect")]
    private static partial int FillRect(nint dc, ref Rect rect, nint brush);

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "SetStretchBltMode")]
    private static partial int SetStretchBltMode(nint dc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBrushOrgEx")]
    private static partial int SetBrushOrgEx(nint dc, int x, int y, nint previous);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static partial int DeleteObject(nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    private static partial nint GetStockObject(int obj);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetTextExtentPoint32W(nint dc, string text, int length, out NativeSize size);

    [LibraryImport("gdi32.dll", EntryPoint = "TextOutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int TextOutW(nint dc, int x, int y, string text, int length);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchDIBits")]
    private static partial int StretchDIBits(
        nint dc,
        int xDest,
        int yDest,
        int destWidth,
        int destHeight,
        int xSrc,
        int ySrc,
        int srcWidth,
        int srcHeight,
        nint bits,
        nint info,
        uint usage,
        uint rop);
}
#endif
