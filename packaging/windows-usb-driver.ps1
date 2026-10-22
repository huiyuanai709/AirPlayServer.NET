# Windows wired-USB helper for AirPlayServer.NET.
# English first. A short Chinese summary is printed at the end of -Check.
#
# This script does not replace the Apple composite parent. It does not run Zadig.
# It does not install a class filter. It does not download Apple software.
# It does not vendor libusb0.sys. The repository zip does not contain that driver.
# -Setup downloads two official archives, checks the pinned SHA256, and then:
#   1. copies the user-mode libusb-1.0.dll next to the host
#   2. runs install-filter.exe with --device-id=<Apple parent instance>
#   3. copies that zip's bin\amd64\libusb0.sys to %SystemRoot%\System32\drivers\libusb0.sys
#      and the matching bin\amd64\libusb0.dll to %SystemRoot%\System32\libusb0.dll
#   4. refuses to succeed unless Test-Path finds libusb0.sys
# install-filter.exe creates the libusb0 service (ImagePath system32\drivers\libusb0.sys,
# demand start) and the UpperFilter. It does not copy the driver file. Without step 3,
# Device Manager stays on Code 39 (CM_PROB_DRIVER_FAILED_LOAD): the phone only charges.
# The filter is an UpperFilter on that one usbccgp (or usbaapl) parent.
# Interface functions (MI_) are not selected. The temp zip is deleted after the file check.
#
#   windows-usb-setup.cmd
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -Setup
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -Check
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -FetchLibusb -HostDir .
#   powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -InstallFilter -FilterZip C:\path\libusb-win32-bin.zip
#
# Official user-mode library (libusb 1.0.30):
#   https://github.com/libusb/libusb/releases/download/v1.0.30/libusb-1.0.30.7z
#   SHA256 7fb1dfec805b97983763d7d0ae244320da12add1003d4249c96cc4d586398c79
# Official libusb-win32 device filter (1.4.0.2, current project release):
#   https://github.com/mcuee/libusb-win32/releases/download/release_1.4.0.2/libusb-win32-bin-1.4.0.2.zip
#   SHA256 00004c92cdb99be36e17fb2377165eb97e63b48ba895bfc04a642ea9c3e26d94
#   Project page: https://github.com/mcuee/libusb-win32/releases/tag/release_1.4.0.2
#   Historical project: https://sourceforge.net/projects/libusb-win32/
# Apple Devices (Microsoft Store, not downloaded by this script):
#   https://apps.microsoft.com/detail/9np83lwlpz9k

[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$FetchLibusb,
    [string]$HostDir = "",
    [switch]$InstallFilter,
    [string]$FilterZip = "",
    [string]$InstanceId = "",
    [switch]$Setup,
    [switch]$Elevated,
    [switch]$Pause
)

$ErrorActionPreference = "Stop"
$script:ExitCode = 0
$script:DriverFileMissing = $false
$LibusbVersion = "1.0.30"
$LibusbUrl = "https://github.com/libusb/libusb/releases/download/v$LibusbVersion/libusb-$LibusbVersion.7z"
$LibusbSha256 = "7fb1dfec805b97983763d7d0ae244320da12add1003d4249c96cc4d586398c79"
$FilterVersion = "1.4.0.2"
$FilterUrl = "https://github.com/mcuee/libusb-win32/releases/download/release_$FilterVersion/libusb-win32-bin-$FilterVersion.zip"
$FilterSha256 = "00004c92cdb99be36e17fb2377165eb97e63b48ba895bfc04a642ea9c3e26d94"
$FilterPage = "https://github.com/mcuee/libusb-win32/releases/tag/release_$FilterVersion"
$AppleDevicesUrl = "https://apps.microsoft.com/detail/9np83lwlpz9k"

function Write-Step([string]$English, [string]$Chinese) {
    Write-Host $English
    if ($Chinese) {
        Write-Host $Chinese
    }
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
catch {
}

if (-not $IsWindows -and -not ($env:OS -eq "Windows_NT")) {
    Write-Step "windows-usb-driver.ps1 is for Windows. Linux and macOS only need the system libusb-1.0 library." "这个脚本只给 Windows 用。Linux 和 macOS 只要系统里的 libusb-1.0。"
    exit 2
}

if ([string]::IsNullOrWhiteSpace($HostDir)) {
    $HostDir = $PSScriptRoot
}
if (-not (Test-Path -LiteralPath $HostDir)) {
    throw "HostDir does not exist: $HostDir"
}
$HostDir = (Resolve-Path -LiteralPath $HostDir).Path

if (-not $Check -and -not $FetchLibusb -and -not $InstallFilter -and -not $Setup) {
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

function Assert-OfficialHash([string]$Path, [string]$Expected, [string]$Url) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not $actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        throw "SHA256 mismatch for $Url. Expected $Expected. Actual $actual. The file was not installed."
    }
    Write-Step "SHA256 $actual matches the pinned official hash." "SHA256 与脚本里的官方值一致。"
}

function Get-NativeSystem32 {
    $windows = $env:SystemRoot
    if ([string]::IsNullOrWhiteSpace($windows)) {
        $windows = "C:\Windows"
    }

    # 32-bit PowerShell on 64-bit Windows redirects System32 to SysWOW64.
    # The amd64 driver must land in the native System32 the service ImagePath uses.
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        $sysnative = Join-Path $windows "Sysnative"
        if (Test-Path -LiteralPath $sysnative) {
            return $sysnative
        }
    }

    return (Join-Path $windows "System32")
}

function Get-Libusb0SysPath {
    return (Join-Path (Join-Path (Get-NativeSystem32) "drivers") "libusb0.sys")
}

function Get-Libusb0DllPath {
    return (Join-Path (Get-NativeSystem32) "libusb0.dll")
}

function Test-AppleStoreStack {
    foreach ($name in @("AppleLowerFilter", "AppleKmdfFilter")) {
        if (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name") {
            return $true
        }
        $sys = Join-Path $env:SystemRoot "System32\drivers\$name.sys"
        if (Test-Path -LiteralPath $sys) {
            return $true
        }
    }

    $apps = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications"
    if (Test-Path $apps) {
        $hit = @(Get-ChildItem $apps -ErrorAction SilentlyContinue | Where-Object {
            $_.PSChildName -like "AppleInc.AppleDevices_*" -or $_.PSChildName -like "AppleInc.iTunes_*"
        })
        if ($hit.Count -gt 0) {
            return $true
        }
    }

    $windowsApps = Join-Path $env:ProgramFiles "WindowsApps"
    if (Test-Path -LiteralPath $windowsApps) {
        foreach ($filter in @("AppleInc.AppleDevices_*", "AppleInc.iTunes_*")) {
            $pkg = @(Get-ChildItem -LiteralPath $windowsApps -Directory -Filter $filter -ErrorAction SilentlyContinue)
            if ($pkg.Count -gt 0) {
                return $true
            }
        }
    }

    return $false
}

function Show-Check {
    $dll = Join-Path $PSScriptRoot "libusb-1.0.dll"
    $beside = Test-Path -LiteralPath $dll
    $service = Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service"
    $support = Test-Path "${env:CommonProgramFiles}\Apple\Mobile Device Support"
    $store = Test-AppleStoreStack
    $sysPath = Get-Libusb0SysPath
    $sysPresent = Test-Path -LiteralPath $sysPath
    $driverService = Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\libusb0"
    if ($Setup) {
        Write-Step "Setup downloads the official user-mode DLL and the official libusb-win32 zip, installs one device UpperFilter on an Apple usbccgp parent, then copies bin\amd64\libusb0.sys into System32\drivers." "正在准备一键安装：只给 Apple 的 usbccgp 父设备加 UpperFilter，并把 libusb0.sys 复制到系统驱动目录。"
    }
    else {
        Write-Step "Check only. This does not change drivers." "只检查，不改驱动。"
    }
    Write-Step ("libusb-1.0.dll beside this script: " + $(if ($beside) { $dll } else { "missing" })) ""
    Write-Step ("Apple Mobile Device Service: " + $(if ($service) { "registered" } else { "not registered" })) ""
    Write-Step ("Apple Mobile Device Support files: " + $(if ($support) { "present" } else { "missing" })) ""
    Write-Step ("Microsoft Store Apple stack (AppleInc.AppleDevices, AppleLowerFilter, or AppleKmdfFilter): " + $(if ($store) { "present" } else { "not found" })) ""
    Write-Step ("libusb0 kernel service: " + $(if ($driverService) { "registered (ImagePath system32\drivers\libusb0.sys)" } else { "not registered" })) ""
    Write-Step ("libusb0.sys: " + $(if ($sysPresent) { $sysPath } else { "missing (" + $sysPath + ")" })) ""
    if ($store -and -not $service) {
        Write-Step "Microsoft Store Apple Devices does not register Apple Mobile Device Service or Common Files\Apple\Mobile Device Support. AppleLowerFilter, AppleKmdfFilter, AppleMobileDeviceProcess.exe, or AMDS32\MobileDevice.dll still count. Do not uninstall Apple Devices." "商店版 Apple Devices 不注册传统的 Apple Mobile Device Service。AppleLowerFilter / AppleKmdfFilter 仍算已安装。不要卸载 Apple Devices。"
    }
    elseif (-not $service -and -not $support -and -not $store) {
        Write-Step "Install Apple Devices from the Microsoft Store ($AppleDevicesUrl), or Apple Mobile Device Support. This script does not download Apple software." "安装 Apple Devices 或 Apple Mobile Device Support。这个脚本不下载 Apple 的安装包。商店地址：$AppleDevicesUrl"
    }

    $parents = @(Get-AppleParents)
    if ($parents.Count -eq 0) {
        Write-Step "No Apple USB parent is plugged in. After you connect a phone, its service must stay usbccgp. Do not run Zadig on the Apple composite parent." "当前没有 Apple USB 父设备。插上之后服务应保持 usbccgp，不要用 Zadig 改复合父设备。"
    }

    $filterRegistered = $false
    foreach ($parent in $parents) {
        if ($parent.Libusb0) {
            $filterRegistered = $true
        }

        if (Test-Replaced $parent.Service) {
            Write-Step ("REFUSE " + $parent.InstanceId + " service=" + $parent.Service + ". Restore the Apple usbccgp parent before a filter install. Do not use Zadig on this parent.") ("拒绝 " + $parent.InstanceId + "。先恢复 usbccgp，不要用 Zadig。")
            continue
        }

        if ($parent.Libusb0 -and -not $sysPresent) {
            $filter = "libusb0 UpperFilter registered, but libusb0.sys is missing (Code 39)"
        }
        elseif ($parent.Libusb0) {
            $filter = "libusb0 UpperFilter present and libusb0.sys installed"
        }
        else {
            $filter = "no libusb0 UpperFilter"
        }
        $keep = $(if (Test-KeepsApple $parent.Service) { "Apple parent kept" } else { "unexpected service" })
        Write-Step ($parent.InstanceId + " service=" + $parent.Service + " (" + $keep + "), " + $filter) ""
    }

    if (($driverService -or $filterRegistered) -and -not $sysPresent) {
        $script:DriverFileMissing = $true
        $repair = "REPAIR: the libusb0 service or an UpperFilter is registered, but $sysPath is missing. Device Manager shows Code 39 (CM_PROB_DRIVER_FAILED_LOAD) and the phone only charges. install-filter.exe does not copy bin\amd64\libusb0.sys."
        if ($Setup -or $InstallFilter) {
            $repair += " This run copies that file from the official libusb-win32 zip after install-filter.exe returns, and fails if it is still missing."
        }
        else {
            $repair += " Re-run windows-usb-setup.cmd or this script -Setup. That copies bin\amd64\libusb0.sys (and the matching amd64 libusb0.dll) from the official zip. Copying bin\amd64\libusb0.sys from that zip into $sysPath by hand also repairs it."
        }
        $repair += " Do not use Zadig. This script does not vendor libusb0.sys."
        Write-Step $repair "需要修复：已注册 libusb0，但缺少 libusb0.sys。设备管理器代码 39（CM_PROB_DRIVER_FAILED_LOAD），手机只能充电。请重新运行 -Setup，或从官方 zip 的 bin\amd64 复制 libusb0.sys。不要用 Zadig。"
    }

    Write-Step "One step: double-click windows-usb-setup.cmd, or run this script -Setup from an elevated prompt. That downloads the official user-mode DLL and the official libusb-win32 zip, installs one device UpperFilter, then copies bin\amd64\libusb0.sys into System32\drivers and checks the file is there. Manual pieces: -FetchLibusb, and -InstallFilter -FilterZip <that official zip>." "一键：双击 windows-usb-setup.cmd，或用管理员运行 -Setup。它下载官方用户态 DLL 和官方 libusb-win32，给一台 Apple 父设备装 UpperFilter，再把 bin\amd64\libusb0.sys 复制到系统驱动目录并确认文件存在。"
}

function Get-LibusbDll([string]$Root) {
    $candidates = Get-ChildItem -Path $Root -Filter "libusb-1.0.dll" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "\\(x86|MS32|Win32|ARM|arm64)\\" }
    $preferred = $candidates | Where-Object { $_.FullName -match "MS64|x64|amd64" } | Select-Object -First 1
    if ($preferred) { return $preferred.FullName }
    $any = $candidates | Select-Object -First 1
    if ($any) { return $any.FullName }
    return $null
}

function Invoke-FetchLibusb {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("airplay-libusb-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $archive = Join-Path $temp "libusb.7z"
        Write-Step "Downloading user-mode libusb $LibusbVersion from $LibusbUrl" "正在下载用户态 libusb $LibusbVersion。这不是内核驱动。"
        Invoke-WebRequest -Uri $LibusbUrl -OutFile $archive -UseBasicParsing
        Assert-OfficialHash $archive $LibusbSha256 $LibusbUrl
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
        $dest = Join-Path $HostDir "libusb-1.0.dll"
        Copy-Item -LiteralPath $dll -Destination $dest -Force
        Write-Step "Copied libusb-1.0.dll to $dest" "已复制 libusb-1.0.dll。"
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Assert-DeviceInstanceSwitch([string]$ExePath) {
    $bytes = [System.IO.File]::ReadAllBytes($ExePath)
    $wide = [System.Text.Encoding]::Unicode.GetString($bytes)
    if ($wide.Contains("--device-id=")) {
        return
    }

    throw "This install-filter.exe cannot target one device instance. Point -FilterZip at the official libusb-win32 $FilterVersion zip ($FilterPage). A VID/PID filter would also match interface functions, so this script will not use one."
}

function Invoke-InstallFilter {
    if (-not (Test-Admin)) {
        throw "-InstallFilter must run in an elevated PowerShell. It still refuses to replace the Apple parent. Use -Setup or windows-usb-setup.cmd for the one-step prompt."
    }
    if (-not $FilterZip) {
        throw "-FilterZip is required for -InstallFilter. -Setup downloads the official zip itself. This script does not vendor libusb0.sys."
    }
    if (-not (Test-Path -LiteralPath $FilterZip)) {
        throw "FilterZip was not found: $FilterZip"
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("airplay-libusb0-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        Expand-Archive -Path $FilterZip -DestinationPath $temp -Force
        Install-FromExtractedTree $temp
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Copy-OfficialDriverFile([string]$Source, [string]$Destination, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Source)) {
        throw "The official libusb-win32 zip is missing $Label ($Source). This script does not vendor libusb0.sys."
    }

    $length = (Get-Item -LiteralPath $Source).Length
    if ($length -le 0) {
        throw "$Label in the official libusb-win32 zip is empty ($Source)."
    }

    $destDir = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $copyError = ""
    try {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
    catch {
        $copyError = $_.Exception.Message
    }

    if (-not (Test-Path -LiteralPath $Destination)) {
        throw "Failed to copy $Label to $Destination. $copyError install-filter.exe only registers the libusb0 service and UpperFilter. Device Manager shows Code 39 (CM_PROB_DRIVER_FAILED_LOAD) until $Destination exists. Re-run -Setup from an elevated prompt. This script does not vendor libusb0.sys."
    }

    try {
        $destHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    }
    catch {
        throw "Could not read $Destination to verify $Label. $($_.Exception.Message) $copyError Re-run -Setup from an elevated prompt."
    }
    if (-not $destHash.Equals($sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Destination does not match the official zip's $Label. Expected SHA256 $sourceHash. Actual $destHash. $copyError Unplug the phone so Windows can replace a loaded copy, then re-run -Setup."
    }

    Write-Step "$Label at $Destination matches the official zip (SHA256 $destHash)." "$Label 已在目标路径，SHA256 与官方 zip 一致。"
}

function Install-Libusb0DriverFiles([string]$Amd64Dir) {
    if ($Amd64Dir -notmatch "[\\/]amd64$") {
        throw "Refusing to copy libusb0.sys from a folder that is not bin\amd64: $Amd64Dir"
    }

    $sysSource = Join-Path $Amd64Dir "libusb0.sys"
    $sysDest = Get-Libusb0SysPath
    if (-not (Test-Path -LiteralPath $sysSource)) {
        throw "bin\amd64\libusb0.sys was not next to install-filter.exe. install-filter.exe does not copy this file, so this script will not claim success. This script does not vendor libusb0.sys."
    }

    Copy-OfficialDriverFile $sysSource $sysDest "bin\amd64\libusb0.sys"
    if (-not (Test-Path -LiteralPath $sysDest)) {
        throw "install-filter.exe registered the libusb0 service and UpperFilter, but $sysDest is still missing. Device Manager will show Code 39 (CM_PROB_DRIVER_FAILED_LOAD) and the phone will only charge. Re-run -Setup. This script does not vendor libusb0.sys."
    }

    # The kernel driver imports USBD.SYS and ntoskrnl.exe, not this DLL. Code 39 is the
    # missing .sys. The official package still installs the matching amd64 libusb0.dll
    # to %SystemRoot%\System32\libusb0.dll (INF CopyFiles libusb_files_dll, DIRID 10).
    $dllSource = Join-Path $Amd64Dir "libusb0.dll"
    $dllDest = Get-Libusb0DllPath
    if (-not (Test-Path -LiteralPath $dllSource)) {
        throw "bin\amd64\libusb0.dll was not next to bin\amd64\libusb0.sys. The official libusb-win32 zip ships that user-mode library with the driver."
    }

    Copy-OfficialDriverFile $dllSource $dllDest "bin\amd64\libusb0.dll"
    if (-not (Test-Path -LiteralPath $dllDest)) {
        throw "The matching amd64 libusb0.dll was not copied to $dllDest. $sysDest is the file Windows loads; re-run -Setup so both files from bin\amd64 are in place."
    }

    Write-Step "Verified $sysDest exists. install-filter.exe does not copy bin\amd64\libusb0.sys; this script does, after the filter is registered." "已确认 libusb0.sys 在系统驱动目录。install-filter 不会复制它，本脚本在注册过滤之后复制并检查。"
}

function Install-FromExtractedTree([string]$Root) {
    $tools = @(Get-ChildItem -Path $Root -Filter "install-filter.exe" -Recurse -File |
        Where-Object { $_.FullName -match "[\\/]amd64[\\/]" })
    $tool = $tools | Select-Object -First 1
    if (-not $tool) {
        throw "amd64 install-filter.exe was not in the official libusb-win32 zip."
    }

    Assert-DeviceInstanceSwitch $tool.FullName
    $parents = @(Get-AppleParents)
    if ($InstanceId) {
        $parents = @($parents | Where-Object { $_.InstanceId -eq $InstanceId })
        if ($parents.Count -eq 0) {
            throw "InstanceId is not an Apple USB parent: $InstanceId"
        }
    }
    if ($parents.Count -eq 0) {
        throw "No Apple USB parent is plugged in. Plug in the phone, unlock it, tap Trust This Computer, and run -Setup again."
    }

    $installed = 0
    $driverCopied = $false
    foreach ($parent in $parents) {
        if (Test-Replaced $parent.Service) {
            Write-Step ("Skipped " + $parent.InstanceId + " because service " + $parent.Service + " replaced the Apple parent. Do not run Zadig on it.") ("已跳过 " + $parent.InstanceId + "。不要用 Zadig。")
            continue
        }
        if (-not (Test-KeepsApple $parent.Service)) {
            Write-Step ("Skipped " + $parent.InstanceId + " service " + $parent.Service + ". Only usbccgp or Apple's own service is accepted.") ("已跳过 " + $parent.InstanceId + "。只接受 usbccgp 或 Apple 自己的服务。")
            continue
        }

        Write-Step ("Installing a device UpperFilter on " + $parent.InstanceId + " (service " + $parent.Service + ") with --device-id. Interface functions are not selected.") ("只给这个父设备实例装 UpperFilter。")
        & $tool.FullName install "--device-id=$($parent.InstanceId)"
        if ($LASTEXITCODE -ne 0) {
            throw "install-filter.exe exited $LASTEXITCODE for $($parent.InstanceId)"
        }

        # Copy while the extract tree still exists. The caller deletes it after this returns.
        # Do this on the first success so a later device failure cannot drop the driver file.
        if (-not $driverCopied) {
            Install-Libusb0DriverFiles $tool.DirectoryName
            $driverCopied = $true
        }
        $installed++
    }

    if ($installed -eq 0) {
        throw "No device filter was installed. Restore usbccgp on the Apple parent first. Do not run Zadig on that parent."
    }
    if (-not $driverCopied -or -not (Test-Path -LiteralPath (Get-Libusb0SysPath))) {
        throw "The libusb0 UpperFilter was registered, but $(Get-Libusb0SysPath) is still missing. Device Manager will show Code 39 (CM_PROB_DRIVER_FAILED_LOAD). Re-run -Setup. This script does not vendor libusb0.sys."
    }
    Write-Step "Unplug the phone and plug it back in so Windows loads System32\drivers\libusb0.sys, then unlock it and tap Trust This Computer. Start the host with --usb. A registered UpperFilter without that file is Code 39 and the phone only charges." "重新插拔手机，让 Windows 加载 libusb0.sys，然后解锁并信任这台电脑，再用 --usb 启动。只有过滤注册、没有这个文件时是代码 39，手机只能充电。"
}

function Get-OfficialFilterZip {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("airplay-libusb0-zip-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $archive = Join-Path $temp "libusb-win32-bin.zip"
        Write-Step "Downloading official libusb-win32 $FilterVersion from $FilterUrl" "正在下载官方 libusb-win32 $FilterVersion。校验通过后才安装，装完删除临时文件。仓库不附带内核驱动。"
        Invoke-WebRequest -Uri $FilterUrl -OutFile $archive -UseBasicParsing
        Assert-OfficialHash $archive $FilterSha256 $FilterUrl
        return $archive
    }
    catch {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Invoke-ElevateSetup {
    if ($Elevated) {
        throw "Administrator approval was not granted. Run windows-usb-setup.cmd and accept the prompt. The Apple parent stays on usbccgp."
    }

    Write-Step "Requesting administrator approval. The Apple parent stays on usbccgp." "需要管理员批准。Apple 父设备保持 usbccgp。"
    $argList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $PSCommandPath,
        "-Setup",
        "-Elevated",
        "-Pause",
        "-HostDir", $HostDir
    )
    if ($InstanceId) {
        $argList += @("-InstanceId", $InstanceId)
    }

    try {
        $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru
    }
    catch {
        throw "Administrator approval was not granted. Run windows-usb-setup.cmd from the win-x64 folder and accept the prompt."
    }

    if ($null -eq $proc) {
        throw "Administrator approval was not granted."
    }

    exit $proc.ExitCode
}

function Invoke-Setup {
    if (-not (Test-Admin)) {
        Invoke-ElevateSetup
    }

    Invoke-FetchLibusb
    $parents = @(Get-AppleParents)
    $ready = @($parents | Where-Object { (Test-KeepsApple $_.Service) -and -not (Test-Replaced $_.Service) })
    if ($InstanceId) {
        $ready = @($ready | Where-Object { $_.InstanceId -eq $InstanceId })
    }
    if ($ready.Count -eq 0) {
        Write-Step "libusb-1.0.dll is in $HostDir. No Apple usbccgp parent is ready for a device filter. Plug in the phone, unlock it, tap Trust This Computer, and run windows-usb-setup.cmd again. Install Apple Devices from $AppleDevicesUrl only when neither the Store package nor Apple Mobile Device Support is present. Do not uninstall Apple Devices. This script does not download Apple software." "用户态 DLL 已放好。当前没有可安装过滤驱动的 usbccgp 父设备。插上手机、解锁并信任后，再运行一次 windows-usb-setup.cmd。商店版已安装时不要卸载 Apple Devices。"
        $script:ExitCode = 4
        return
    }

    $zip = Get-OfficialFilterZip
    try {
        Expand-Archive -Path $zip -DestinationPath (Split-Path -Parent $zip) -Force
        Install-FromExtractedTree (Split-Path -Parent $zip)
    }
    finally {
        $dir = Split-Path -Parent $zip
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

try {
    if ($Setup -and -not (Test-Admin)) {
        Invoke-ElevateSetup
    }
    Show-Check
    if ($Setup) {
        Invoke-Setup
    }
    else {
        if ($FetchLibusb) {
            Invoke-FetchLibusb
        }
        if ($InstallFilter) {
            Invoke-InstallFilter
        }
    }
}
catch {
    Write-Step $_.Exception.Message ""
    if ($script:ExitCode -eq 0) {
        $script:ExitCode = 1
    }
}

if ($script:DriverFileMissing -and -not $Setup -and -not $InstallFilter -and -not $FetchLibusb) {
    if ($script:ExitCode -eq 0) {
        $script:ExitCode = 5
    }
}

if ($Pause) {
    Write-Host ""
    Write-Host "Press Enter to close."
    [void](Read-Host)
}

exit $script:ExitCode
