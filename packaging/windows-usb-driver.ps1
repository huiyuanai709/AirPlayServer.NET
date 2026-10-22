# Windows wired-USB helper for AirPlayServer.NET.
# English first. A short Chinese summary is printed at the end of -Check.
#
# This script does not replace the Apple composite parent. It does not run Zadig.
# It does not install a class filter. It does not download Apple software.
# It does not vendor libusb0.sys. The repository zip does not contain that driver.
# -Setup downloads two official archives, checks the pinned SHA256, and then:
#   1. copies the user-mode libusb-1.0.dll next to the host
#   2. runs install-filter.exe with --device-id=<Apple parent instance>
# The filter is an UpperFilter on that one usbccgp (or usbaapl) parent.
# Interface functions (MI_) are not selected. The temp zip is deleted afterwards.
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

function Show-Check {
    $dll = Join-Path $PSScriptRoot "libusb-1.0.dll"
    $beside = Test-Path -LiteralPath $dll
    $service = Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service"
    $support = Test-Path "${env:CommonProgramFiles}\Apple\Mobile Device Support"
    if ($Setup) {
        Write-Step "Setup downloads the official user-mode DLL and the official libusb-win32 zip, then installs one device UpperFilter on an Apple usbccgp parent." "正在准备一键安装：只给 Apple 的 usbccgp 父设备加 UpperFilter。"
    }
    else {
        Write-Step "Check only. This does not change drivers." "只检查，不改驱动。"
    }
    Write-Step ("libusb-1.0.dll beside this script: " + $(if ($beside) { $dll } else { "missing" })) ""
    Write-Step ("Apple Mobile Device Service: " + $(if ($service) { "registered" } else { "missing" })) ""
    Write-Step ("Apple Mobile Device Support files: " + $(if ($support) { "present" } else { "missing" })) ""
    if (-not $service -and -not $support) {
        Write-Step "Install Apple Devices from the Microsoft Store ($AppleDevicesUrl), or Apple Mobile Device Support. This script does not download Apple software." "安装 Apple Devices 或 Apple Mobile Device Support。这个脚本不下载 Apple 的安装包。商店地址：$AppleDevicesUrl"
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

    Write-Step "One step: double-click windows-usb-setup.cmd, or run this script -Setup from an elevated prompt. That downloads the official user-mode DLL and the official libusb-win32 zip, then installs one device UpperFilter. Manual pieces: -FetchLibusb, and -InstallFilter -FilterZip <that official zip>." "一键：双击 windows-usb-setup.cmd，或用管理员运行 -Setup。它下载官方用户态 DLL 和官方 libusb-win32，然后只给一台 Apple 父设备装 UpperFilter。"
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
        $installed++
    }

    if ($installed -eq 0) {
        throw "No device filter was installed. Restore usbccgp on the Apple parent first. Do not run Zadig on that parent."
    }
    Write-Step "Unplug the phone and plug it back in, then unlock it and tap Trust This Computer. Start the host with --usb." "重新插拔手机，解锁并信任这台电脑，然后用 --usb 启动。"
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
        Write-Step "libusb-1.0.dll is in $HostDir. No Apple usbccgp parent is ready for a device filter. Plug in the phone, unlock it, tap Trust This Computer, and run windows-usb-setup.cmd again. Install Apple Devices from $AppleDevicesUrl when that service is missing. This script does not download Apple software." "用户态 DLL 已放好。当前没有可安装过滤驱动的 usbccgp 父设备。插上手机、解锁并信任后，再运行一次 windows-usb-setup.cmd。"
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

if ($Pause) {
    Write-Host ""
    Write-Host "Press Enter to close."
    [void](Read-Host)
}

exit $script:ExitCode
