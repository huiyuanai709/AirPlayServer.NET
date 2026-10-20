using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AirPlayServer.Core;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Host.Display;

/// <summary>
/// Owns the Avalonia desktop lifetime for mirror windows. Windows and Linux
/// run that lifetime on a background thread. macOS runs it on the main thread,
/// because AppKit will not open windows from anywhere else. The first call to
/// <see cref="Open"/> is what starts the UI, so <c>--help</c> and
/// <c>--self-check</c> do not load Skia.
/// </summary>
internal static class AvaloniaMirrorDisplay
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _ready;
    private static TaskCompletionSource<bool>? _stopped;
    private static ClassicDesktopStyleApplicationLifetime? _lifetime;
    private static Exception? _startError;
    private static int _cascade = -1;
    private static int _opened;
    private static int _failed;
    private static int _startLogged;

    public static bool HasOpenedSurface => Volatile.Read(ref _opened) > 0;

    internal static int NextCascade() => Interlocked.Increment(ref _cascade);

    public static IMirrorSurface? Open(MirrorSurfaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryEnsureUi(request.Log))
        {
            return null;
        }

        try
        {
            var window = Dispatcher.UIThread.Invoke(() =>
            {
                var created = new MirrorWindow(request);
                MirrorWindow.ShowOwned(created);
                return created;
            });
            Interlocked.Increment(ref _opened);
            return window;
        }
        catch (Exception ex)
        {
            request.Log.Log(
                AirPlayLogLevel.Warning,
                "镜像窗口没有打开。无图形会话时请加 --no-window。The mirror window was not opened. Pass --no-window when there is no display. " + Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Blocks the calling thread in the Avalonia loop and runs
    /// <paramref name="server"/> on the thread pool once the loop is pumping.
    /// Used on macOS, where the UI thread has to be the process main thread.
    /// </summary>
    public static int RunOnMainThread(AirPlayReceiverOptions options, Func<Task<int>> server)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(server);
        options.OpenMirrorSurface = Open;
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            _ready ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RunLoop(server, exit);
        return exit.Task.GetAwaiter().GetResult();
    }

    public static void Shutdown()
    {
        ClassicDesktopStyleApplicationLifetime? lifetime;
        TaskCompletionSource<bool>? stopped;
        lock (Gate)
        {
            lifetime = _lifetime;
            stopped = _stopped;
        }

        if (lifetime is null || stopped is null)
        {
            return;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                lifetime.Shutdown();
            }
            else
            {
                Dispatcher.UIThread.Post(() => lifetime.Shutdown());
            }

            stopped.Task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }

    private static bool TryEnsureUi(IAirPlayLog log)
    {
        if (Volatile.Read(ref _failed) != 0)
        {
            return false;
        }

        TaskCompletionSource<bool> ready;
        lock (Gate)
        {
            if (_failed != 0)
            {
                return false;
            }

            if (_ready is not null)
            {
                ready = _ready;
            }
            else
            {
                ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ready = ready;
                _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() => RunLoop(server: null, exit: null))
                {
                    Name = "AirPlay Avalonia",
                    IsBackground = true,
                };
                if (OperatingSystem.IsWindows())
                {
                    thread.SetApartmentState(ApartmentState.STA);
                }

                thread.Start();
            }
        }

        if (!ready.Task.Wait(TimeSpan.FromSeconds(15)))
        {
            Interlocked.Exchange(ref _failed, 1);
            LogStartupFailure(log, new TimeoutException("Avalonia did not start within 15 seconds."));
            return false;
        }

        if (!ready.Task.Result)
        {
            LogStartupFailure(log, _startError ?? new InvalidOperationException("Avalonia did not start."));
            return false;
        }

        return true;
    }

    private static void RunLoop(Func<Task<int>>? server, TaskCompletionSource<int>? exit)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        lock (Gate)
        {
            _lifetime = lifetime;
        }

        try
        {
            AppBuilder.Configure(() => new MirrorApplication())
                .UseHostPlatform()
                .SetupWithLifetime(lifetime);
            lifetime.Startup += (_, _) =>
            {
                // Startup runs before the dispatcher loop. Post so Open's Invoke
                // cannot deadlock on a loop that has not started pumping.
                Dispatcher.UIThread.Post(() =>
                {
                    SignalReady(started: true);
                    if (server is null || exit is null)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            exit.TrySetResult(await server().ConfigureAwait(false));
                        }
                        catch (Exception ex)
                        {
                            exit.TrySetException(ex);
                        }
                        finally
                        {
                            Dispatcher.UIThread.Post(() => lifetime.Shutdown());
                        }
                    });
                });
            };
            lifetime.Start([]);
        }
        catch (Exception ex)
        {
            _startError = ex;
            SignalReady(started: false);
            if (exit is not null && server is not null && !exit.Task.IsCompleted)
            {
                try
                {
                    exit.TrySetResult(server().GetAwaiter().GetResult());
                }
                catch (Exception serverEx)
                {
                    exit.TrySetException(serverEx);
                }
            }
        }
        finally
        {
            _stopped?.TrySetResult(true);
        }
    }

    private static void SignalReady(bool started)
    {
        if (!started)
        {
            Interlocked.Exchange(ref _failed, 1);
        }

        _ready?.TrySetResult(started);
    }

    private static void LogStartupFailure(IAirPlayLog log, Exception ex)
    {
        if (Interlocked.Exchange(ref _startLogged, 1) != 0)
        {
            return;
        }

        log.Log(
            AirPlayLogLevel.Warning,
            "镜像窗口没有打开。无图形会话时请加 --no-window。The mirror window was not opened. Pass --no-window when there is no display. " + Describe(ex));
    }

    private static string Describe(Exception ex)
    {
        var message = ex.Message;
        if (ex.InnerException is { Message: { Length: > 0 } inner })
        {
            message += " " + inner;
        }

        return message;
    }
}
