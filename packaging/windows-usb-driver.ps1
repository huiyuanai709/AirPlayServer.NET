# Windows wired-USB helper for AirPlayServer.NET.
# English first. A short Chinese summary is printed at the end of -Check.
#
# This script does not replace the Apple composite parent. It does not run Zadig.
# It does not install a class filter. It does not download Apple software.
# It does not ship libusb0.sys. -FetchLibusb copies only the user-mode libusb-1.0.dll.
# -InstallFilter runs an operator-supplied libusb-win32 install-filter.exe as a
# per-device UpperFilter on an Apple usbccgp (or usbaapl) instance.
#
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -Check
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -FetchLibusb -HostDir .
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -InstallFilter -FilterZip C:\path\libusb-win32-bin.zip

[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$FetchLibusb,
    [string]$HostDir = "",
    [switch]$InstallFilter,
    [string]$FilterZip = "",
    [string]$InstanceId = ""
)

$ErrorActionPreference = "Stop"
$LibusbVersion = "1.0.30"
$LibusbUrl = "https://github.com/libusb/libusb/releases/download/v$LibusbVersion/libusb-$LibusbVersion.7z"

function Write-Step([string]$English, [string]$Chinese) {
    Write-Host $English
    if ($Chinese) {
        Write-Host $Chinese
    }
}

if (-not $IsWindows -and -not ($env:OS -eq "Windows_NT")) {
    Write-Step "windows-usb-driver.ps1 is for Windows. Linux and macOS only need the system libusb-1.0 library." "这个脚本只给 Windows 用。Linux 和 macOS 只要系统里的 libusb-1.0。"
    exit 2
}

if (-not $Check -and -not $FetchLibusb -and -not $InstallFilter) {
    $Check = $true
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AppleParents {
    $usb = "HKLM:\SYSTEM\CurrentControlSet\Enum\USB"
    if (-not (Test-Path $usb)) {
        return @()
    }

    $found = @()
    foreach ($product in Get-ChildItem $usb) {
        $name = $product.PSChildName
        if ($name -notlike "VID_05AC*") { continue }
        if ($name -like "*&MI_*") { continue }
        foreach ($instance in Get-ChildItem $product.PSPath) {
            $props = Get-ItemProperty $instance.PSPath
            $service = [string]$props.Service
            $filters = @()
            if ($props.UpperFilters) {
                $filters = @($props.UpperFilters)
            }
            $found += [pscustomobject]@{
                InstanceId = "USB\$name\$($instance.PSChildName)"
                Service = $service
                Libusb0 = [bool]($filters | Where-Object { $_ -eq "libusb0" })
            }
        }
    }
    return $found
}

function Test-KeepsApple([string]$Service) {
    return @("usbccgp", "usbaapl64", "usbaapl", "appleusb") -contains $Service.ToLowerInvariant()
}

function Test-Replaced([string]$Service) {
    return @("winusb", "libusbk", "libusb0") -contains $Service.ToLowerInvariant()
}

function Show-Check {
    $dll = Join-Path $PSScriptRoot "libusb-1.0.dll"
    $beside = Test-Path $dll
    $service = Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service"
    $support = Test-Path "${env:CommonProgramFiles}\Apple\Mobile Device Support"
    Write-Step "Check only. This does not change drivers." "只检查，不改驱动。"
    Write-Step ("libusb-1.0.dll beside this script: " + $(if ($beside) { $dll } else { "missing" })) ""
    Write-Step ("Apple Mobile Device Service: " + $(if ($service) { "registered" } else { "missing" })) ""
    Write-Step ("Apple Mobile Device Support files: " + $(if ($support) { "present" } else { "missing" })) ""
    if (-not $service -and -not $support) {
        Write-Step "Install Apple Devices from the Microsoft Store, or Apple Mobile Device Support. This script does not download Apple software." "安装 Apple Devices 或 Apple Mobile Device Support。这个脚本不下载 Apple 的安装包。"
    }

    $parents = @(Get-AppleParents)
    if ($parents.Count -eq 0) {
        Write-Step "No Apple USB parent is plugged in. After you connect a phone, its service must stay usbccgp. Do not run Zadig on the Apple composite parent." "当前没有 Apple USB 父设备。插上之后服务应保持 usbccgp，不要用 Zadig 改复合父设备。"
    }

    foreach ($parent in $parents) {
        if (Test-Replaced $parent.Service) {
            Write-Step ("REFUSE " + $parent.InstanceId + " service=" + $parent.Service + ". Restore the Apple usbccgp parent before a filter install. Do not use Zadig on this parent.") ("拒绝 " + $parent.InstanceId + "。先恢复 usbccgp，不要用 Zadig。")
            continue
        }

        $filter = $(if ($parent.Libusb0) { "libusb0 UpperFilter present" } else { "no libusb0 UpperFilter" })
        $keep = $(if (Test-KeepsApple $parent.Service) { "Apple parent kept" } else { "unexpected service" })
        Write-Step ($parent.InstanceId + " service=" + $parent.Service + " (" + $keep + "), " + $filter) ""
    }

    Write-Step "Next: put libusb-1.0.dll next to AirPlayServer.Host.exe (-FetchLibusb). On each phone, install a device UpperFilter only (-InstallFilter -FilterZip <official libusb-win32 zip>). Then unplug and replug, unlock, and tap Trust." "下一步：把 libusb-1.0.dll 放到程序旁边。每台手机只装设备级 UpperFilter。然后重新插拔并信任电脑。"
}

function Get-LibusbDll([string]$Root) {
    $candidates = Get-ChildItem -Path $Root -Filter "libusb-1.0.dll" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "\\(x86|MS32|Win32|ARM)\\" }
    $preferred = $candidates | Where-Object { $_.FullName -match "MS64|x64|arm64" } | Select-Object -First 1
    if ($preferred) { return $preferred.FullName }
    $any = $candidates | Select-Object -First 1
    if ($any) { return $any.FullName }
    return $null
}

function Invoke-FetchLibusb {
    if (-not $HostDir) {
        $HostDir = $PSScriptRoot
    }
    if (-not (Test-Path $HostDir)) {
        throw "HostDir does not exist: $HostDir"
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("airplay-libusb-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $archive = Join-Path $temp "libusb.7z"
        Write-Step "Downloading user-mode libusb $LibusbVersion from $LibusbUrl" "正在下载用户态 libusb $LibusbVersion。这不是内核驱动。"
        Invoke-WebRequest -Uri $LibusbUrl -OutFile $archive
        $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
        Write-Step "SHA256 $hash" "对照 GitHub release 上的文件。这个脚本不附带内核驱动。"
        $extract = Join-Path $temp "extract"
        New-Item -ItemType Directory -Path $extract | Out-Null
        $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
        if (-not $tar) {
            throw "tar.exe is required to unpack the official .7z. Copy VS2022\MS64\dll\libusb-1.0.dll next to the host by hand from $LibusbUrl"
        }
        & tar.exe -xf $archive -C $extract
        $dll = Get-LibusbDll $extract
        if (-not $dll) {
            throw "libusb-1.0.dll was not inside the archive."
        }
        $dest = Join-Path (Resolve-Path $HostDir) "libusb-1.0.dll"
        Copy-Item $dll $dest -Force
        Write-Step "Copied libusb-1.0.dll to $dest" "已复制 libusb-1.0.dll。"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-InstallFilter {
    if (-not (Test-Admin)) {
        throw "-InstallFilter must run in an elevated PowerShell. It still refuses to replace the Apple parent."
    }
    if (-not $FilterZip) {
        throw "-FilterZip is required. Point it at the official libusb-win32 binary zip you downloaded. This script does not download libusb0.sys."
    }
    if (-not (Test-Path $FilterZip)) {
        throw "FilterZip was not found: $FilterZip"
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("airplay-libusb0-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        Expand-Archive -Path $FilterZip -DestinationPath $temp -Force
        $tools = Get-ChildItem -Path $temp -Filter "install-filter.exe" -Recurse -File |
            Where-Object { $_.FullName -match "amd64|x64" -and $_.FullName -notmatch "\\x86\\" }
        if (-not $tools) {
            $tools = Get-ChildItem -Path $temp -Filter "install-filter.exe" -Recurse -File | Select-Object -First 1
        }
        $tool = $tools | Select-Object -First 1
        if (-not $tool) {
            throw "install-filter.exe was not in $FilterZip"
        }

        $parents = @(Get-AppleParents)
        if ($InstanceId) {
            $parents = @($parents | Where-Object { $_.InstanceId -eq $InstanceId })
            if ($parents.Count -eq 0) {
                throw "InstanceId is not an Apple USB parent: $InstanceId"
            }
        }
        if ($parents.Count -eq 0) {
            throw "No Apple USB parent is plugged in."
        }

        $installed = 0
        foreach ($parent in $parents) {
            if (Test-Replaced $parent.Service) {
                Write-Step ("Skipped " + $parent.InstanceId + " because service " + $parent.Service + " replaced the Apple parent. Do not run Zadig on it.") ("已跳过 " + $parent.InstanceId + "。不要用 Zadig。")
                continue
            }
            if (-not (Test-KeepsApple $parent.Service)) {
                Write-Step ("Skipped " + $parent.InstanceId + " service " + $parent.Service + ". Only usbccgp or Apple's own service is accepted.") ("已跳过 " + $parent.InstanceId + "。只接受 usbccgp 或 Apple 自己的服务。")
                continue
            }

            Write-Step ("Installing a device UpperFilter on " + $parent.InstanceId + " (service " + $parent.Service + "). Class filters are not used.") ("只给这个设备实例装 UpperFilter，不装 class filter。")
            & $tool.FullName install "--device=$($parent.InstanceId)"
            if ($LASTEXITCODE -ne 0) {
                throw "install-filter.exe exited $LASTEXITCODE for $($parent.InstanceId)"
            }
            $installed++
        }

        if ($installed -eq 0) {
            throw "No device filter was installed."
        }
        Write-Step "Unplug the phone and plug it back in, then unlock it and tap Trust This Computer. Start the host with --usb." "重新插拔手机，解锁并信任这台电脑，然后用 --usb 启动。"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Show-Check
if ($FetchLibusb) {
    Invoke-FetchLibusb
}
if ($InstallFilter) {
    Invoke-InstallFilter
}
