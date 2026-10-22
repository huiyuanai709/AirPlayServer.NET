using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AirPlayServer.Host.Tests;

public class HostSignalsTests
{
    [Fact]
    public unsafe void Inherited_sigign_is_reset_so_ctrl_c_can_be_caught()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var signal = LoadSignal();
        var savedInt = signal(2, (IntPtr)1);
        var savedQuit = signal(3, (IntPtr)1);
        try
        {
            HostSignals.UnignoreTerminationSignals();
            Assert.Equal(IntPtr.Zero, signal(2, IntPtr.Zero));
            Assert.Equal(IntPtr.Zero, signal(3, IntPtr.Zero));
        }
        finally
        {
            signal(2, savedInt);
            signal(3, savedQuit);
        }
    }

    [Fact]
    public unsafe void Existing_termination_handler_is_left_in_place()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var signal = LoadSignal();
        var handler = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&IgnoreSignal;
        var savedInt = signal(2, handler);
        var savedQuit = signal(3, handler);
        try
        {
            HostSignals.UnignoreTerminationSignals();
            Assert.Equal(handler, signal(2, handler));
            Assert.Equal(handler, signal(3, handler));
        }
        finally
        {
            signal(2, savedInt);
            signal(3, savedQuit);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void IgnoreSignal(int signalNumber)
    {
        _ = signalNumber;
    }

    private static unsafe delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> LoadSignal()
    {
        IntPtr libc = 0;
        var loaded = OperatingSystem.IsMacOS() && NativeLibrary.TryLoad("libSystem.dylib", out libc);
        loaded = loaded || NativeLibrary.TryLoad("libc.so.6", out libc) || NativeLibrary.TryLoad("libc", out libc);
        Assert.True(loaded);
        Assert.True(NativeLibrary.TryGetExport(libc, "signal", out var export));
        return (delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr>)export;
    }
}
