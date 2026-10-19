#if WINDOWS_HOST
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Latest-frame window via user32/gdi32. Compiled only for a Windows host build
/// (or -p:ForceWindowsHost=true). Linux Native AOT does not link user32.
/// Letterbox bars and the scaled frame are drawn into a memory DIB, then one
/// BitBlt presents them. Filling the window itself every frame flashes black.
/// The picture is copied into that DIB in software. HALFTONE StretchDIBits of
/// 32-bit BGRA paints the client white.
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
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const int ChipLeft = 8;
    private const int ChipTop = 8;
    // ~60 Hz. SetTimer resolution is coarse (often ~15.6 ms on older stacks),
    // so 1000/60 truncates to 16 ms. Presentation stays latest-frame only.
    private const uint DisplayRefreshMilliseconds = 1000 / 60;

    public static IMirrorWindow Start(
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

    private sealed class Window : IMirrorWindow
    {
        private readonly FrameSlot _frames;
        private string _windowTitle;
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
        private nint _backDc;
        private nint _backBitmap;
        private nint _backBits;
        private nint _backPrevious;
        private int _backWidth;
        private int _backHeight;
        private int _loggedPresent;
        private bool _paintCovers;
        private int _paintX;
        private int _paintY;
        private int _paintWidth;
        private int _paintHeight;
        private bool _chipDrawn;
        private Rect _chipRect;

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

        public void SetDeviceName(string? deviceName)
        {
            var title = string.IsNullOrWhiteSpace(deviceName) ? "mirror" : deviceName.Trim();
            _windowTitle = "AirPlayServer.NET — " + title;
            var hwnd = Volatile.Read(ref _hwnd);
            if (hwnd != 0 && !_hoverVisible)
            {
                SetWindowTextW(hwnd, _windowTitle);
            }
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
                    // Stock BLACK_BRUSH. COLOR_WINDOW (6) is white, so a mirror that
                    // has not published a picture looks like a white screen.
                    Background = GetStockObject(4),
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
            finally
            {
                ReleaseBackBuffer();
            }
        }

        private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case WmCreate:
                    SetTimer(hwnd, 1, DisplayRefreshMilliseconds, 0);
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
                    ReleaseBackBuffer();
                    PostQuitMessage(0);
                    return 0;
                default:
                    return DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }

        private unsafe void Blit(nint hwnd)
        {
            if (!_frames.TryLease(out var width, out var height, out var stride, out var pixels, out _, out var lease))
            {
                ClearHover(hwnd);
                return;
            }

            using (lease)
            {
                BlitLeased(hwnd, width, height, stride, pixels);
            }
        }

        private unsafe void BlitLeased(nint hwnd, int width, int height, int stride, byte[] pixels)
        {
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

            var dc = GetDC(hwnd);
            try
            {
                if (dc == 0)
                {
                    return;
                }

                // Compose off-screen. A full-client FillRect on this window DC on
                // each timer tick flashes black before the picture lands.
                // HALFTONE StretchDIBits of 32-bit BGRA paints the client white,
                // so the phone picture is written straight into the DIB.
                if (TryCreateBackBuffer(dc, clientWidth, clientHeight) && TryPaintBackBuffer(pixels, width, height, stride, destination))
                {
                    ApplyHover(hwnd, width, height, destination);
                    DrawHoverChip(_backDc);
                    BitBlt(dc, 0, 0, clientWidth, clientHeight, _backDc, 0, 0, SrcCopy);
                    return;
                }

                PaintLetterboxBars(dc, clientWidth, clientHeight, destination);
                if (!TryStretch(dc, pixels, width, height, destination))
                {
                    NotePresentFailure(width, height);
                }

                ApplyHover(hwnd, width, height, destination);
                DrawHoverChip(dc);
            }
            finally
            {
                if (dc != 0)
                {
                    ReleaseDC(hwnd, dc);
                }
            }
        }

        private unsafe bool TryPaintBackBuffer(byte[] pixels, int width, int height, int stride, FrameCoordinateMap.FrameDestination destination)
        {
            var rowBytes = _backWidth * 4;
            var bytes = (long)rowBytes * _backHeight;
            if (_backBits == 0 || _backWidth < 1 || _backHeight < 1 || rowBytes < 4 || bytes <= 0 || bytes > int.MaxValue)
            {
                return false;
            }

            var buffer = new Span<byte>((void*)_backBits, (int)bytes);
            // The picture overwrites its rectangle. Clearing the whole DIB every
            // tick is a second full-frame write; bars stay black from the last
            // clear as long as the letterbox rectangle does not move.
            var sameView = _paintCovers
                && _paintX == destination.X
                && _paintY == destination.Y
                && _paintWidth == destination.Width
                && _paintHeight == destination.Height;
            if (!sameView)
            {
                buffer.Clear();
                _chipDrawn = false;
            }
            else if (_chipDrawn)
            {
                ClearChip(buffer, rowBytes);
            }

            if (BgraScale.PaintLetterbox(
                pixels,
                width,
                height,
                stride,
                buffer,
                _backWidth,
                _backHeight,
                rowBytes,
                destination.X,
                destination.Y,
                destination.Width,
                destination.Height,
                bottomUp: true))
            {
                _paintCovers = true;
                _paintX = destination.X;
                _paintY = destination.Y;
                _paintWidth = destination.Width;
                _paintHeight = destination.Height;
                return true;
            }

            NotePresentFailure(width, height);
            return false;
        }

        private bool TryStretch(nint dc, byte[] pixels, int width, int height, FrameCoordinateMap.FrameDestination destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(0), 40);
            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(8), -height);
            BinaryPrimitives.WriteInt16LittleEndian(_header.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(_header.AsSpan(14), 32);
            var imageBytes = (long)width * height * 4;
            if (imageBytes <= 0 || imageBytes > int.MaxValue)
            {
                return false;
            }

            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(20), (int)imageBytes);
            var pixelsPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var headerPin = GCHandle.Alloc(_header, GCHandleType.Pinned);
            try
            {
                // COLORONCOLOR. HALFTONE on a 32-bit top-down DIB returns white.
                SetStretchBltMode(dc, ColorOnColor);
                var written = StretchDIBits(
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
                return written != 0;
            }
            finally
            {
                headerPin.Free();
                pixelsPin.Free();
            }
        }

        private void NotePresentFailure(int width, int height)
        {
            if (Interlocked.Exchange(ref _loggedPresent, 1) != 0)
            {
                return;
            }

            _log.Log(
                AirPlayLogLevel.Warning,
                "窗口没能画上 " + width + "x" + height + " 的镜像帧，客户区保持空白。The mirror window could not paint a " + width + "x" + height + " frame, so the client stays blank.");
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
            if (!_pointerInside || !_frames.TryReadSize(out var width, out var height))
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

        private bool TryCreateBackBuffer(nint windowDc, int width, int height)
        {
            if (_backDc != 0 && _backWidth == width && _backHeight == height)
            {
                return true;
            }

            ReleaseBackBuffer();
            var memoryDc = CreateCompatibleDC(windowDc);
            if (memoryDc == 0)
            {
                return false;
            }

            // Bottom-up 32-bit DIB. GDI still draws from the top-left of the DC.
            // The phone picture is written into these bits; the window only BitBlts.
            var info = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0), 40);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(8), height);
            BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(14), 32);
            var infoPin = GCHandle.Alloc(info, GCHandleType.Pinned);
            nint bitmap;
            nint bits;
            try
            {
                bitmap = CreateDIBSection(windowDc, infoPin.AddrOfPinnedObject(), 0, out bits, 0, 0);
            }
            finally
            {
                infoPin.Free();
            }

            if (bitmap == 0)
            {
                DeleteDC(memoryDc);
                return false;
            }

            var previous = SelectObject(memoryDc, bitmap);
            if (previous == 0)
            {
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                return false;
            }

            _backDc = memoryDc;
            _backBitmap = bitmap;
            _backBits = bits;
            _backPrevious = previous;
            _backWidth = width;
            _backHeight = height;
            // Uninitialized DIB bits stay off-screen. The window is not cleared.
            ClearOffscreen(memoryDc, width, height);
            return true;
        }

        private void ReleaseBackBuffer()
        {
            if (_backDc != 0 && _backPrevious != 0)
            {
                SelectObject(_backDc, _backPrevious);
            }

            if (_backBitmap != 0)
            {
                DeleteObject(_backBitmap);
            }

            if (_backDc != 0)
            {
                DeleteDC(_backDc);
            }

            _backDc = 0;
            _backBitmap = 0;
            _backBits = 0;
            _backPrevious = 0;
            _backWidth = 0;
            _backHeight = 0;
            _paintCovers = false;
            _chipDrawn = false;
        }

        private void ClearChip(Span<byte> buffer, int stride)
        {
            if (!_chipDrawn || _backWidth < 1 || _backHeight < 1)
            {
                _chipDrawn = false;
                return;
            }

            var left = Math.Clamp(_chipRect.Left, 0, _backWidth);
            var right = Math.Clamp(_chipRect.Right, 0, _backWidth);
            var top = Math.Clamp(_chipRect.Top, 0, _backHeight);
            var bottom = Math.Clamp(_chipRect.Bottom, 0, _backHeight);
            var byteWidth = (right - left) * 4;
            if (byteWidth <= 0 || top >= bottom)
            {
                _chipDrawn = false;
                return;
            }

            // The DIB is bottom-up. GDI chip coordinates start at the top-left.
            for (var y = top; y < bottom; y++)
            {
                var memoryY = _backHeight - 1 - y;
                buffer.Slice((memoryY * stride) + (left * 4), byteWidth).Clear();
            }

            _chipDrawn = false;
        }

        private static void PaintLetterboxBars(
            nint dc,
            int clientWidth,
            int clientHeight,
            FrameCoordinateMap.FrameDestination picture)
        {
            Span<FrameCoordinateMap.FrameDestination> bars = stackalloc FrameCoordinateMap.FrameDestination[4];
            var count = FrameCoordinateMap.LetterboxBars(clientWidth, clientHeight, picture, bars);
            if (count == 0)
            {
                return;
            }

            var brush = CreateSolidBrush(0);
            if (brush == 0)
            {
                return;
            }

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var bar = bars[i];
                    var rect = new Rect
                    {
                        Left = bar.X,
                        Top = bar.Y,
                        Right = bar.X + bar.Width,
                        Bottom = bar.Y + bar.Height,
                    };
                    FillRect(dc, ref rect, brush);
                }
            }
            finally
            {
                DeleteObject(brush);
            }
        }

        // Off-screen only. A full-client fill on the window DC is the flicker.
        private static void ClearOffscreen(nint dc, int width, int height)
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
                _chipRect = box;
                _chipDrawn = true;
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

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    private static partial int DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static partial nint CreateDIBSection(
        nint hdc,
        nint bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt")]
    private static partial int BitBlt(
        nint hdcDest,
        int x,
        int y,
        int width,
        int height,
        nint hdcSrc,
        int xSrc,
        int ySrc,
        uint rop);

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
