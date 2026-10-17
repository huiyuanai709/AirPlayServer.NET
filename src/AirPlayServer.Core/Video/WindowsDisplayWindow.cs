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
    private const int GwlpUserData = -21;
    private const uint SrcCopy = 0x00CC0020;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;

    public static IAsyncDisposable Start(FrameSlot frames, string title, IAirPlayLog log, CancellationToken cancellationToken)
    {
        var window = new Window(frames, title, log, cancellationToken);
        window.Start();
        return window;
    }

    private sealed class Window : IAsyncDisposable
    {
        private readonly FrameSlot _frames;
        private readonly string _title;
        private readonly IAirPlayLog _log;
        private readonly CancellationToken _cancellation;
        private readonly byte[] _header = new byte[40];
        private GCHandle _self;
        private Task _loop = Task.CompletedTask;
        private nint _hwnd;
        private int _disposed;

        public Window(FrameSlot frames, string title, IAirPlayLog log, CancellationToken cancellationToken)
        {
            _frames = frames;
            _title = title;
            _log = log;
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
                    "AirPlayServer.NET — " + _title,
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
                case WmDestroy:
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
                return;
            }

            if (GetClientRect(hwnd, out var rect) == 0)
            {
                return;
            }

            var clientWidth = Math.Max(1, rect.Right - rect.Left);
            var clientHeight = Math.Max(1, rect.Bottom - rect.Top);
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

                SetStretchBltMode(dc, 3);
                StretchDIBits(
                    dc,
                    0,
                    0,
                    clientWidth,
                    clientHeight,
                    0,
                    0,
                    width,
                    height,
                    pixelsPin.AddrOfPinnedObject(),
                    headerPin.AddrOfPinnedObject(),
                    0,
                    SrcCopy);
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

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "SetStretchBltMode")]
    private static partial int SetStretchBltMode(nint dc, int mode);

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
