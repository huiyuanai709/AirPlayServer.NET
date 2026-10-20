using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AirPlayServer.Core;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Host.Display;

/// <summary>
/// One mirror session. The picture is the latest BGRA frame, letterboxed into
/// the client. Hover text is TapClock UIKit points when <c>--model</c> is set.
/// Pointer presses are not handled and are not sent to the phone.
/// </summary>
internal sealed class MirrorWindow : Window, IMirrorSurface
{
    private readonly FrameSlot _frames;
    private readonly IAirPlayLog _log;
    private readonly int _tapClockScreenWidth;
    private readonly int _tapClockScreenHeight;
    private readonly Panel _root;
    private readonly Image _image;
    private readonly Border _chip;
    private readonly TextBlock _chipText;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenRegistration _cancel;
    private string _baseTitle;
    private string? _readout;
    private WriteableBitmap? _bitmap;
    private long _presentedGeneration = -1;
    private int _loggedPresent;
    private int _disposed;
    private bool _pointerInside;
    private int _pointerX;
    private int _pointerY;

    public MirrorWindow(MirrorSurfaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _frames = request.Frames;
        _log = request.Log;
        _tapClockScreenWidth = request.TapClockScreenWidth;
        _tapClockScreenHeight = request.TapClockScreenHeight;
        _baseTitle = MirrorViewMath.DeviceTitle(request.Title);
        Title = _baseTitle;
        Background = Brushes.Black;
        TransparencyLevelHint = [WindowTransparencyLevel.None];
        Width = 480;
        Height = 1040;
        MinWidth = 120;
        MinHeight = 160;
        WindowStartupLocation = WindowStartupLocation.Manual;
        CanResize = true;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        _chipText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(8, 5),
            // Inter is not embedded: it has no CJK glyphs, and the numbers
            // render with a system face. The title bar is drawn by the OS.
            FontFamily = new FontFamily("Microsoft YaHei, PingFang SC, Noto Sans CJK SC, sans-serif"),
        };
        _chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            Child = _chipText,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            IsVisible = false,
            IsHitTestVisible = false,
        };
        _root = new Panel
        {
            Background = Brushes.Black,
        };
        _root.Children.Add(_image);
        _root.Children.Add(_chip);
        Content = _root;
        // Presses stay in this window. Nothing in this type talks to the phone.
        _root.PointerMoved += OnPointerMoved;
        _root.PointerExited += OnPointerExited;

        var index = AvaloniaMirrorDisplay.NextCascade();
        var offset = index * 28;
        Position = new PixelPoint(80 + offset, 48 + offset);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0) };
        _timer.Tick += (_, _) => Present();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        _cancel = request.CancellationToken.Register(static state =>
        {
            if (state is MirrorWindow window)
            {
                Dispatcher.UIThread.Post(window.Close);
            }
        }, this);
    }

    public void SetDeviceName(string? deviceName)
    {
        var title = MirrorViewMath.DeviceTitle(deviceName);
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyTitle(title);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyTitle(title));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancel.DisposeAsync().ConfigureAwait(false);
        if (Dispatcher.UIThread.CheckAccess())
        {
            CloseFromOwner();
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                CloseFromOwner();
            }
            finally
            {
                done.TrySetResult();
            }
        });
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private void ApplyTitle(string title)
    {
        _baseTitle = title;
        Title = _readout is null ? _baseTitle : MirrorViewMath.HoverTitle(_baseTitle, _readout);
    }

    private void CloseFromOwner()
    {
        _timer.Stop();
        _bitmap?.Dispose();
        _bitmap = null;
        if (IsVisible)
        {
            Close();
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(_root);
        var scale = RenderScale();
        _pointerInside = true;
        _pointerX = MirrorViewMath.DevicePixels(point.X, scale);
        _pointerY = MirrorViewMath.DevicePixels(point.Y, scale);
        UpdateHover();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _pointerInside = false;
        UpdateHover();
    }

    private void Present()
    {
        if (_disposed != 0)
        {
            return;
        }

        var client = ClientPixels();
        if (client.Width < 1 || client.Height < 1)
        {
            return;
        }

        if (!_frames.TryLease(out var frameWidth, out var frameHeight, out var stride, out var pixels, out var generation, out var lease))
        {
            UpdateHover();
            return;
        }

        using (lease)
        {
            if (generation != _presentedGeneration || _bitmap is null || _bitmap.PixelSize.Width != client.Width || _bitmap.PixelSize.Height != client.Height)
            {
                if (TryPaint(pixels, frameWidth, frameHeight, stride, client.Width, client.Height))
                {
                    _presentedGeneration = generation;
                }
            }
        }

        UpdateHover();
    }

    private bool TryPaint(byte[] pixels, int frameWidth, int frameHeight, int stride, int clientWidth, int clientHeight)
    {
        try
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != clientWidth || _bitmap.PixelSize.Height != clientHeight)
            {
                var created = new WriteableBitmap(
                    new PixelSize(clientWidth, clientHeight),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                _image.Source = created;
                _bitmap?.Dispose();
                _bitmap = created;
            }

            using var framebuffer = _bitmap.Lock();
            var length = checked(framebuffer.RowBytes * framebuffer.Size.Height);
            unsafe
            {
                var destination = new Span<byte>((void*)framebuffer.Address, length);
                if (!MirrorViewMath.TryComposeLetterbox(
                        pixels,
                        frameWidth,
                        frameHeight,
                        stride,
                        destination,
                        clientWidth,
                        clientHeight,
                        framebuffer.RowBytes))
                {
                    NotePresentFailure(frameWidth, frameHeight);
                    return false;
                }
            }

            _image.InvalidateVisual();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
        {
            NotePresentFailure(frameWidth, frameHeight, ex.Message);
            return false;
        }
    }

    private void UpdateHover()
    {
        var client = ClientPixels();
        if (!_pointerInside
            || client.Width < 1
            || client.Height < 1
            || !_frames.TryReadSize(out var frameWidth, out var frameHeight)
            || !MirrorViewMath.TryHoverReadout(
                _pointerX,
                _pointerY,
                client.Width,
                client.Height,
                frameWidth,
                frameHeight,
                _tapClockScreenWidth,
                _tapClockScreenHeight,
                out var readout))
        {
            _readout = null;
            _chip.IsVisible = false;
            Title = _baseTitle;
            return;
        }

        _readout = readout;
        _chipText.Text = readout;
        _chip.IsVisible = true;
        Title = MirrorViewMath.HoverTitle(_baseTitle, readout);
    }

    private (int Width, int Height) ClientPixels()
    {
        var scale = RenderScale();
        return (MirrorViewMath.DevicePixels(_root.Bounds.Width, scale), MirrorViewMath.DevicePixels(_root.Bounds.Height, scale));
    }

    private double RenderScale()
    {
        var scaling = RenderScaling;
        return scaling > 0 && !double.IsNaN(scaling) && !double.IsInfinity(scaling) ? scaling : 1;
    }

    private void NotePresentFailure(int width, int height, string? detail = null)
    {
        if (Interlocked.Exchange(ref _loggedPresent, 1) != 0)
        {
            return;
        }

        var extra = string.IsNullOrEmpty(detail) ? "" : " " + detail;
        _log.Log(
            AirPlayLogLevel.Warning,
            "窗口没能画上 " + width + "x" + height + " 的镜像帧，客户区保持空白。The mirror window could not paint a " + width + "x" + height + " frame, so the client stays blank." + extra);
    }

    public static void ShowOwned(MirrorWindow window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is null)
        {
            desktop.MainWindow = window;
        }

        window.Show();
    }
}
