# Windows wired USB

AirPlayServer.NET receives the QuickTime-over-USB screen with libusb-1.0. The host process only reads driver state and claims the QuickTime interface (class `0xFF`, subclass `0x2A`). It does not install a kernel driver, and it does not link the Qt widget toolkit.

Wireless AirPlay does not need this page. `--usb` stays off unless you pass it.

## What has to be true

1. `libusb-1.0.dll` sits next to `AirPlayServer.Host.exe`, or on the system `PATH`. This is the user-mode library. It is not a kernel driver.
2. The Apple composite parent stays on Windows `usbccgp` (or Apple's own `usbaapl64` / `appleusb` service). Apple Mobile Device Support, or the Microsoft Store Apple Devices package, provides that stack.
3. A libusb-win32 **device** UpperFilter is installed on that parent instance. WinUSB cannot issue `SET_CONFIGURATION`, and the QuickTime switch needs that request. The filter lets libusb change configuration while the Apple parent driver remains.
4. Do not run Zadig on the Apple composite parent. Replacing that parent with WinUSB or libusbK removes the Apple function driver. This project will refuse that shape and tell you to restore `usbccgp`.

The shape follows the public iPhoneMirror driver split: the mirror process does not rebind USB, and capture is a per-device libusb-win32 filter on the Apple parent rather than a replacement of it. The installer itself is not copied from that project. This repository does not vendor `libusb0.sys`.

## One step

The win-x64 zip includes `windows-usb-setup.cmd` and `windows-usb-driver.ps1`. From a source checkout both live in `packaging/`.

Plug in the phone, then double-click `windows-usb-setup.cmd`. Accept the administrator prompt. The script:

1. Downloads the official user-mode library and checks its SHA256.
   `https://github.com/libusb/libusb/releases/download/v1.0.30/libusb-1.0.30.7z`
   SHA256 `7fb1dfec805b97983763d7d0ae244320da12add1003d4249c96cc4d586398c79`
2. Copies `libusb-1.0.dll` next to the host.
3. Downloads the official libusb-win32 1.4.0.2 binary zip and checks its SHA256.
   `https://github.com/mcuee/libusb-win32/releases/download/release_1.4.0.2/libusb-win32-bin-1.4.0.2.zip`
   SHA256 `00004c92cdb99be36e17fb2377165eb97e63b48ba895bfc04a642ea9c3e26d94`
   Release page: `https://github.com/mcuee/libusb-win32/releases/tag/release_1.4.0.2`
   The historical project page is `https://sourceforge.net/projects/libusb-win32/`.
4. Runs that zip's `bin\amd64\install-filter.exe` as `install --device-id=<Apple parent instance>`.

`--device-id` matches one device instance. The script only passes Apple parents whose registry key has no `&MI_` and whose service is `usbccgp`, `usbaapl64`, `usbaapl`, or `appleusb`. A parent on WinUSB, libusbK, or libusb0 is skipped. The temporary zip is deleted after the filter is registered. The repository still does not contain `libusb0.sys`.

The same step from PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -Setup
```

If no phone is plugged in, the DLL is still copied and the script asks you to connect, unlock, trust this computer, and run it again. Apple's installer is not downloaded. Install Apple Devices from the Microsoft Store when the Apple service is missing: `https://apps.microsoft.com/detail/9np83lwlpz9k`

Unplug the phone, plug it back in, unlock it, and tap Trust This Computer. Start the host with `--usb`.

## Pieces, if you do not want the one step

Check, without changing anything:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -Check
```

Copy only the official user-mode DLL (the same libusb 1.0.30 URL and SHA256 as above):

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -FetchLibusb -HostDir .
```

Install from a zip you already downloaded. It has to be a libusb-win32 build whose `install-filter.exe` accepts `--device-id` (1.4.0.2 does). The script still selects only Apple parents and still calls `install --device-id=<that instance>`.

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -InstallFilter -FilterZip C:\path\libusb-win32-bin-1.4.0.2.zip
```

`-InstanceId USB\VID_05AC&PID_…\…` limits the filter to one parent.

## What the log means

| libusb code | Meaning | Next step |
| --- | --- | --- |
| library not loaded | `libusb-1.0.dll` is missing | `-FetchLibusb` or copy the DLL. Wireless keeps running. |
| `-12` NOT_SUPPORTED | WinUSB cannot change configuration | Keep `usbccgp`. Install the device UpperFilter. Do not Zadig the parent. |
| `-3` ACCESS | The device is busy or not openable | Same filter path. Close other USB tools. Replug. |
| `-6` BUSY | The interface is claimed | Close other tools, then replug after the filter is installed. |
| `-4` / `-5` | The phone is gone | Unlock, trust this computer, replug. |

A missing library and a failed claim are different messages. Claim failures name the libusb code and the next step. Startup with `--usb` also prints the read-only parent check.

## USB audio

ASYN `EAT!` carries clear LPCM in a CMSampleBuffer (`sdat`), not AAC-ELD. SYNC `AFMT` is the AudioStreamBasicDescription. The usual layout is 48 kHz, stereo, signed 16-bit, little-endian, packed. The host writes that PCM to the same default playback device the wireless path uses: WASAPI on Windows, PulseAudio then ALSA on Linux. macOS uses those same libraries when they are present. A closed device drops PCM and leaves the picture running. Wireless AAC-ELD and its `SET_PARAMETER` volume stay on the AirPlay session. USB has no volume parameter, so the samples play at unity.

## Linux and macOS

No extra driver. Linux needs `libusb-1.0.so.0` (`apt install libusb-1.0-0`). macOS needs `libusb-1.0.0.dylib` (`brew install libusb`). The kernel detaches its own interface driver when libusb asks. Do not install a Windows filter there.

## 中文

父设备保持 `usbccgp`（Apple Mobile Device Support 或 Microsoft Store 的 Apple Devices，`https://apps.microsoft.com/detail/9np83lwlpz9k`）。不要用 Zadig 把 Apple 复合父设备换成 WinUSB。win-x64 包里双击 `windows-usb-setup.cmd`，在管理员提示里允许。脚本下载官方 `libusb-1.0.dll` 和官方 libusb-win32 1.4.0.2，校验 SHA256，然后只给这一台 Apple 父设备实例安装 UpperFilter（`install-filter.exe install --device-id=<实例>`）。本仓库不附带 `libusb0.sys`，也不下载 Apple 的安装包。装完重新插拔，解锁并信任电脑，再用 `--usb` 启动。USB 伴音是 ASYN `EAT!` 里的明文 LPCM，写到系统默认播放设备。无线 AirPlay 不依赖这些步骤。
