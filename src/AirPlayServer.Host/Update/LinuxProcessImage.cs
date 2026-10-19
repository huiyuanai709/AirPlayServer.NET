using System.Runtime.InteropServices;

namespace AirPlayServer.Host.Update;

/// <summary>
/// Replaces this process with the new binary on Linux so a systemd
/// Type=simple unit keeps the same PID. libc is loaded at runtime:
/// a direct LibraryImport would make the Windows Native AOT link require it.
/// </summary>
internal static class LinuxProcessImage
{
    public static void Exec(string executable, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        if (!NativeLibrary.TryLoad("libc.so.6", out var libc)
            && !NativeLibrary.TryLoad("libc", out libc))
        {
            throw new IOException("无法加载 libc，不能在原地切换进程。");
        }

        var export = NativeLibrary.GetExport(libc, "execv");
        ExecAt(export, executable, arguments);
    }

    private static void ExecAt(IntPtr export, string executable, IReadOnlyList<string> arguments)
    {
        var pointers = new IntPtr[arguments.Count + 2];
        var argv = Marshal.AllocCoTaskMem(IntPtr.Size * pointers.Length);
        try
        {
            pointers[0] = Marshal.StringToCoTaskMemUTF8(executable);
            for (var i = 0; i < arguments.Count; i++)
            {
                pointers[i + 1] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            pointers[^1] = IntPtr.Zero;
            for (var i = 0; i < pointers.Length; i++)
            {
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, pointers[i]);
            }

            unsafe
            {
                var execv = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)export;
                _ = execv(pointers[0], argv);
            }

            throw new IOException("execv 失败。");
        }
        finally
        {
            Marshal.FreeCoTaskMem(argv);
            for (var i = 0; i < pointers.Length - 1; i++)
            {
                if (pointers[i] != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointers[i]);
                }
            }
        }
    }
}
