using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPlayServer.Host.Update;

/// <summary>
/// Replaces this process with the new binary on Linux so a systemd
/// Type=simple unit keeps the same PID. libc is loaded at runtime:
/// a direct LibraryImport would make the Windows Native AOT link require it.
/// execve receives the environment block because Native AOT does not link
/// setenv, so a managed Environment.SetEnvironmentVariable is invisible to execv.
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

        if (!NativeLibrary.TryGetExport(libc, "execve", out var export))
        {
            throw new IOException("无法加载 execve，不能在原地切换进程。");
        }

        ExecAt(export, executable, arguments, CreateEnvironmentBlock());
    }

    internal static string[] CreateEnvironmentBlock()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            ReadProcEnviron(map);
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || key.Length == 0 || key.Contains('='))
            {
                continue;
            }

            map[key] = entry.Value as string ?? "";
        }

        map[HostUpdate.JustUpdatedVariable] = "1";
        var block = new string[map.Count];
        var index = 0;
        foreach (var pair in map)
        {
            block[index++] = pair.Key + "=" + pair.Value;
        }

        return block;
    }

    private static void ReadProcEnviron(Dictionary<string, string> map)
    {
        try
        {
            var bytes = File.ReadAllBytes("/proc/self/environ");
            var start = 0;
            for (var i = 0; i <= bytes.Length; i++)
            {
                if (i != bytes.Length && bytes[i] != 0)
                {
                    continue;
                }

                if (i > start)
                {
                    var segment = Encoding.UTF8.GetString(bytes, start, i - start);
                    var split = segment.IndexOf('=');
                    if (split > 0)
                    {
                        map[segment[..split]] = segment[(split + 1)..];
                    }
                }

                start = i + 1;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ExecAt(IntPtr export, string executable, IReadOnlyList<string> arguments, string[] environment)
    {
        var argvPointers = new IntPtr[arguments.Count + 2];
        var envPointers = new IntPtr[environment.Length + 1];
        var argv = Marshal.AllocCoTaskMem(IntPtr.Size * argvPointers.Length);
        var envp = Marshal.AllocCoTaskMem(IntPtr.Size * envPointers.Length);
        try
        {
            argvPointers[0] = Marshal.StringToCoTaskMemUTF8(executable);
            for (var i = 0; i < arguments.Count; i++)
            {
                argvPointers[i + 1] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            argvPointers[^1] = IntPtr.Zero;
            for (var i = 0; i < argvPointers.Length; i++)
            {
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, argvPointers[i]);
            }

            for (var i = 0; i < environment.Length; i++)
            {
                envPointers[i] = Marshal.StringToCoTaskMemUTF8(environment[i]);
            }

            envPointers[^1] = IntPtr.Zero;
            for (var i = 0; i < envPointers.Length; i++)
            {
                Marshal.WriteIntPtr(envp, i * IntPtr.Size, envPointers[i]);
            }

            unsafe
            {
                var execve = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)export;
                _ = execve(argvPointers[0], argv, envp);
            }

            throw new IOException("execve 失败。");
        }
        finally
        {
            Marshal.FreeCoTaskMem(argv);
            Marshal.FreeCoTaskMem(envp);
            for (var i = 0; i < argvPointers.Length - 1; i++)
            {
                if (argvPointers[i] != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(argvPointers[i]);
                }
            }

            for (var i = 0; i < envPointers.Length - 1; i++)
            {
                if (envPointers[i] != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(envPointers[i]);
                }
            }
        }
    }
}
