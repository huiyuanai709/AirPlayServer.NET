using System.Runtime.InteropServices;

namespace AirPlayServer.Host;

/// <summary>
/// Stops the host on Ctrl+C and on SIGTERM. Native AOT skips its SIGINT and
/// SIGQUIT handlers when the inherited disposition is SIG_IGN, and exec keeps
/// SIG_IGN, so a relaunch from an ignoring parent would keep running. Those
/// two are put back to SIG_DFL before the runtime subscribes. A real handler
/// is left alone.
/// </summary>
internal static class HostSignals
{
    private const int SigInt = 2;

    private const int SigQuit = 3;

    private static readonly IntPtr SigIgn = new(1);

    private static readonly IntPtr SigErr = new(-1);

    private static readonly TimeSpan ForceExitAfter = TimeSpan.FromSeconds(10);

    private static int _stopCount;

    private static int _watchdogArmed;

    private static PosixSignalRegistration? _termination;

    public static void Install(CancellationTokenSource shutdown)
    {
        UnignoreTerminationSignals();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestStop(shutdown);
        };

        try
        {
            _termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                RequestStop(shutdown);
            });
            GC.KeepAlive(_termination);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    internal static void UnignoreTerminationSignals()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!TryLoadLibc(out var libc) || !NativeLibrary.TryGetExport(libc, "signal", out var export))
        {
            return;
        }

        ResetIfIgnored(export, SigInt);
        ResetIfIgnored(export, SigQuit);
    }

    private static void RequestStop(CancellationTokenSource shutdown)
    {
        // Exit 0 matches a normal stop. The systemd unit uses Restart=on-failure,
        // so 130 would start the service again after Ctrl+C or a second signal.
        if (Interlocked.Increment(ref _stopCount) > 1)
        {
            Environment.Exit(0);
        }

        ArmWatchdog();
        try
        {
            // Do not wait: CancelAsync finishes registered callbacks, and waiting
            // on the signal thread can deadlock with the console lock.
            _ = shutdown.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            Environment.Exit(0);
        }
    }

    private static void ArmWatchdog()
    {
        if (Interlocked.Exchange(ref _watchdogArmed, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(ForceExitAfter).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }

    private static bool TryLoadLibc(out IntPtr libc)
    {
        if (OperatingSystem.IsMacOS() && NativeLibrary.TryLoad("libSystem.dylib", out libc))
        {
            return true;
        }

        return NativeLibrary.TryLoad("libc.so.6", out libc)
            || NativeLibrary.TryLoad("libc", out libc);
    }

    private static unsafe void ResetIfIgnored(IntPtr export, int signo)
    {
        var signal = (delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr>)export;
        var previous = signal(signo, IntPtr.Zero);
        if (previous != IntPtr.Zero && previous != SigIgn && previous != SigErr)
        {
            signal(signo, previous);
        }
    }
}
