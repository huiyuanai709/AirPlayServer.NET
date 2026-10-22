using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Reads the Windows USB registry. It does not install, remove, or rebind a driver.
/// </summary>
internal static class UsbDriverProbe
{
    public static UsbDriverSnapshot Capture(bool libraryLoaded, string? libraryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UsbDriverSnapshot
            {
                LibraryLoaded = libraryLoaded,
                LibraryPath = libraryPath,
            };
        }

        try
        {
            return CaptureWindows(libraryLoaded, libraryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException or InvalidOperationException)
        {
            return new UsbDriverSnapshot
            {
                IsWindows = true,
                LibraryLoaded = libraryLoaded,
                LibraryPath = libraryPath,
                ProbeError = ex.Message,
            };
        }
    }

    [SupportedOSPlatform("windows")]
    private static UsbDriverSnapshot CaptureWindows(bool libraryLoaded, string? libraryPath)
    {
        var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service") is not null;
        var libusb0Service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\libusb0") is not null;
        var support = AppleSupportFiles();
        var driverFile = Libusb0SysExists();
        var parents = new List<UsbParentBinding>();
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is not null)
        {
            foreach (var name in usb.GetSubKeyNames())
            {
                if (!name.StartsWith("VID_05AC", StringComparison.OrdinalIgnoreCase) || name.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var product = usb.OpenSubKey(name);
                if (product is null)
                {
                    continue;
                }

                foreach (var instance in product.GetSubKeyNames())
                {
                    if (parents.Count >= 8)
                    {
                        break;
                    }

                    using var node = product.OpenSubKey(instance);
                    if (node is null)
                    {
                        continue;
                    }

                    var driver = node.GetValue("Service") as string ?? "";
                    parents.Add(new UsbParentBinding
                    {
                        InstanceId = "USB\\" + name + "\\" + instance,
                        Service = driver.Trim(),
                        Libusb0UpperFilter = HasLibusb0(node.GetValue("UpperFilters")),
                    });
                }
            }
        }

        return new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = libraryLoaded,
            LibraryPath = libraryPath,
            AppleMobileDeviceService = service,
            AppleSupportFiles = support,
            AppleStoreStack = AppleStoreStack(),
            Libusb0ServicePresent = libusb0Service,
            Libusb0DriverFilePresent = driverFile,
            Parents = parents,
        };
    }

    /// <summary>
    /// The path install-filter writes as the libusb0 service ImagePath:
    /// <c>system32\drivers\libusb0.sys</c> under the native System32, not SysWOW64.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool Libusb0SysExists()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        }

        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            var native = Path.Combine(windows, "Sysnative", "drivers", "libusb0.sys");
            if (File.Exists(native))
            {
                return true;
            }
        }

        return File.Exists(Path.Combine(windows, "System32", "drivers", "libusb0.sys"));
    }

    [SupportedOSPlatform("windows")]
    private static bool AppleStoreStack()
    {
        if (ServiceExists("AppleLowerFilter") || ServiceExists("AppleKmdfFilter"))
        {
            return true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        }

        var drivers = Path.Combine(windows, "System32", "drivers");
        if (File.Exists(Path.Combine(drivers, "AppleLowerFilter.sys")) || File.Exists(Path.Combine(drivers, "AppleKmdfFilter.sys")))
        {
            return true;
        }

        if (StorePackageRegistered("AppleInc.AppleDevices_") || StorePackageRegistered("AppleInc.iTunes_"))
        {
            return true;
        }

        return StorePackageDirectory("AppleInc.AppleDevices_*") || StorePackageDirectory("AppleInc.iTunes_*");
    }

    [SupportedOSPlatform("windows")]
    private static bool ServiceExists(string name)
        => Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name) is not null;

    [SupportedOSPlatform("windows")]
    private static bool StorePackageRegistered(string prefix)
    {
        using var apps = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");
        if (apps is null)
        {
            return false;
        }

        foreach (var name in apps.GetSubKeyNames())
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StorePackageDirectory(string pattern)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            if (!Directory.Exists(root))
            {
                return false;
            }

            foreach (var _ in Directory.EnumerateDirectories(root, pattern))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool AppleSupportFiles()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Apple", "Mobile Device Support"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Apple", "Mobile Device Support"),
        };
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLibusb0(object? value)
    {
        if (value is string[] many)
        {
            foreach (var name in many)
            {
                if (string.Equals(name, "libusb0", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return value is string one && string.Equals(one, "libusb0", StringComparison.OrdinalIgnoreCase);
    }
}
