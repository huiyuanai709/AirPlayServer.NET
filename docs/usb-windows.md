# Windows wired USB

AirPlayServer.NET receives the QuickTime-over-USB screen with libusb-1.0. The host process only reads driver state and claims the QuickTime interface (class `0xFF`, subclass `0x2A`). It does not install a kernel driver, and it does not link the Qt widget toolkit.

Wireless AirPlay does not need this page. `--usb` stays off unless you pass it.

## What has to be true

1. `libusb-1.0.dll` sits next to `AirPlayServer.Host.exe`, or on the system `PATH`. This is the user-mode library. It is not a kernel driver.
2. The Apple composite parent stays on Windows `usbccgp` (or Apple's own `usbaapl64` / `appleusb` service). Apple Mobile Device Support, or the Microsoft Store Apple Devices package, provides that stack. The Store package `AppleInc.AppleDevices` does not register the classic `Apple Mobile Device Service` and does not create `Common Files\Apple\Mobile Device Support`. `AppleLowerFilter`, `AppleKmdfFilter`, `AppleMobileDeviceProcess.exe`, or `AMDS32\MobileDevice.dll` still mean the Apple stack is installed. Do not uninstall Apple Devices because the classic service is absent.
3. A libusb-win32 **device** UpperFilter is installed on that parent instance. WinUSB cannot issue `SET_CONFIGURATION`, and the QuickTime switch needs that request. The filter lets libusb change configuration while the Apple parent driver remains.
4. `C:\Windows\System32\drivers\libusb0.sys` is actually on disk. The `libusb0` service ImagePath is `system32\drivers\libusb0.sys`. A registry UpperFilter without that file is Device Manager Code 39 (`CM_PROB_DRIVER_FAILED_LOAD`): the Apple parent does not load, the phone only charges, and the Trust dialog never appears.
5. Do not run Zadig on the Apple composite parent. Replacing that parent with WinUSB or libusbK removes the Apple function driver. This project will refuse that shape and tell you to restore `usbccgp`.

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
5. After `install-filter.exe` returns 0, copies that same `bin\amd64\libusb0.sys` to `%SystemRoot%\System32\drivers\libusb0.sys` and the matching `bin\amd64\libusb0.dll` to `%SystemRoot%\System32\libusb0.dll`. It checks the `.sys` with `Test-Path` and stops with an error if the file is still missing. The temporary zip is deleted only after that check.

`install-filter.exe` creates the `libusb0` kernel service (demand start, ImagePath `system32\drivers\libusb0.sys`) and the device UpperFilter. It does not copy `libusb0.sys`. On Windows 11 that is Code 39 (`CM_PROB_DRIVER_FAILED_LOAD`): the composite parent fails to load, the phone only charges, and the Trust dialog never appears. The kernel driver imports `USBD.SYS` and `ntoskrnl.exe`, not the DLL. The DLL is still copied because the official package installs that amd64 `libusb0.dll` to `%SystemRoot%\System32\libusb0.dll`. The script does not install the package INF and does not copy the x86 DLL.

`--device-id` matches one device instance. The script only passes Apple parents whose registry key has no `&MI_` and whose service is `usbccgp`, `usbaapl64`, `usbaapl`, or `appleusb`. A parent on WinUSB, libusbK, or libusb0 is skipped. The repository still does not contain `libusb0.sys`.

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

`-Check` does not treat a `libusb0` UpperFilter, or the `libusb0` service, as success when `System32\drivers\libusb0.sys` is missing. It prints Code 39 and tells you to re-run `-Setup`, or to copy `bin\amd64\libusb0.sys` from the official zip into that folder. That check exits 5. A filter with the file on disk is the healthy line.

Copy only the official user-mode DLL (the same libusb 1.0.30 URL and SHA256 as above):

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -FetchLibusb -HostDir .
```

Install from a zip you already downloaded. It has to be a libusb-win32 build whose `install-filter.exe` accepts `--device-id` (1.4.0.2 does). The script still selects only Apple parents and still calls `install --device-id=<that instance>`.

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -InstallFilter -FilterZip C:\path\libusb-win32-bin-1.4.0.2.zip
```

`-InstanceId USB\VID_05AC&PID_…\…` limits the filter to one parent.

## MTP is not the mirror

Windows can show the phone under This PC as a portable device (WPD / MTP) while QuickTime mirroring is still off. That is the PTP function, USB class `0x06` subclass `0x01`. It is not the QuickTime interface (class `0xFF`, subclass `0x2A`).

iOS hides that interface until a host sends vendor control transfer `0x40` / `0x52` with `wIndex` 2. The phone may drop off the bus and come back with the QuickTime configuration. `--usb` sends that request. It does not wait for Control Center → Screen Mirroring, and it is not AirPlay. `ingress` stays `airplay` until a QuickTime USB session is actually open.

Apple Mobile Device Support, or the Apple Devices app from the Microsoft Store, is still required on Windows. A parent on `usbccgp` plus a libusb0 UpperFilter does not replace that stack. This host does not download Apple's installer. Without the Apple stack, libusb often sees only the MTP interfaces (or an empty device list) even though Explorer already shows storage. The filter is what lets libusb issue `SET_CONFIGURATION` after the QuickTime interface exists. It does not create that interface by itself.

While `--usb` is on, an empty scan or a skipped Apple device is written about every 30 seconds, not on every poll:

| Log | Meaning |
| --- | --- |
| libusb device list is empty (0 devices) | libusb sees nothing to switch. Storage in Explorer only means MTP is up. |
| Apple VID seen, neither mux (`0xFF`/`0xFE`) nor QuickTime (`0xFF`/`0x2A`) | The descriptors that libusb could read are not a mirror target, and this device was not queued. |
| open failed, `libusb` code | The Apple device was seen, including one that already has mux or QuickTime, but `libusb_open` failed. The `0x40`/`0x52` switch was not sent. `-1` is `LIBUSB_ERROR_IO`: libusb-1.0 listed the phone and could not open it. |
| MTP/PTP only, `0x40`/`0x52` will be sent | A normal-mode iPhone/iPad (or any Apple device whose only readable interfaces are PTP) was opened. The existing QuickTime enable runs. A device that already shows mux or QuickTime still uses that original path and is not treated as MTP-only. |

Mux is class `0xFF` subclass `0xFE` (Apple Mobile Device). When that interface is already visible and QuickTime is not, the host sends the same `0x40`/`0x52` request as before. The MTP row is only the case where those vendor interfaces were not in the descriptors libusb could read.

## What the log means

| libusb code | Meaning | Next step |
| --- | --- | --- |
| library not loaded | `libusb-1.0.dll` is missing | `-FetchLibusb` or copy the DLL. Wireless keeps running. |
| `-12` NOT_SUPPORTED | WinUSB cannot change configuration | Keep `usbccgp`. Install the device UpperFilter. Do not Zadig the parent. |
| `-3` ACCESS | The device is busy or not openable | Same filter path. Close other USB tools. Replug. |
| `-6` BUSY | The interface is claimed | Close other tools, then replug after the filter is installed. |
| `-4` / `-5` | The phone is gone | Unlock, trust this computer, replug. |
| `-1` IO | `libusb-1.0` listed the device, then `libusb_open` failed (`LIBUSB_ERROR_IO`) | The phone is not missing. Do not uninstall Apple Devices. Wireless keeps running. |

A missing library and a failed claim are different messages. Claim failures name the libusb code and the next step. Startup with `--usb` also prints the read-only parent check.

## USB audio

ASYN `EAT!` carries clear LPCM in a CMSampleBuffer (`sdat`), not AAC-ELD. SYNC `AFMT` is the AudioStreamBasicDescription. The usual layout is 48 kHz, stereo, signed 16-bit, little-endian, packed. The host writes that PCM to the same default playback device the wireless path uses: WASAPI on Windows, PulseAudio then ALSA on Linux. macOS uses those same libraries when they are present. A closed device drops PCM and leaves the picture running. Wireless AAC-ELD and its `SET_PARAMETER` volume stay on the AirPlay session. USB has no volume parameter, so the samples play at unity.

## Linux and macOS

No extra driver. Linux needs `libusb-1.0.so.0` (`apt install libusb-1.0-0`). macOS needs `libusb-1.0.0.dylib` (`brew install libusb`). The kernel detaches its own interface driver when libusb asks. Do not install a Windows filter there.

## 中文

父设备保持 `usbccgp`（Apple Mobile Device Support 或 Microsoft Store 的 Apple Devices，`https://apps.microsoft.com/detail/9np83lwlpz9k`）。不要用 Zadig 把 Apple 复合父设备换成 WinUSB。win-x64 包里双击 `windows-usb-setup.cmd`，在管理员提示里允许。脚本下载官方 `libusb-1.0.dll` 和官方 libusb-win32 1.4.0.2，校验 SHA256，然后只给这一台 Apple 父设备实例安装 UpperFilter（`install-filter.exe install --device-id=<实例>`）。`install-filter` 只注册服务和 UpperFilter，不复制驱动文件。脚本接着把官方 zip 的 `bin\amd64\libusb0.sys` 复制到 `%SystemRoot%\System32\drivers\libusb0.sys`，并把同目录的 `libusb0.dll` 复制到 `System32`，再用 `Test-Path` 确认 `.sys` 存在，否则失败。文件不在时设备管理器是代码 39（`CM_PROB_DRIVER_FAILED_LOAD`），手机只能充电，也不会弹出信任对话框。`-Check` 能区分「过滤已注册但 `.sys` 缺失」和「文件已就位」。本仓库不附带 `libusb0.sys`，也不下载 Apple 的安装包。装完重新插拔，解锁并信任电脑，再用 `--usb` 启动。

商店版 Apple Devices（`AppleInc.AppleDevices`）不注册传统的 Apple Mobile Device Service，也没有 `Common Files\Apple\Mobile Device Support`。`AppleLowerFilter`、`AppleKmdfFilter`、`AppleMobileDeviceProcess.exe` 或 `AMDS32\MobileDevice.dll` 在，就算 Apple 栈已安装。不要因此卸载 Apple Devices。

资源管理器里的 MTP/存储不是 QuickTime 镜像。`libusb_open` 返回 `-1`（`LIBUSB_ERROR_IO`）时，设备已经在列表里，只是 libusb-1.0 打不开，所以不会发出 `0x40`/`0x52`。日志会写出这个错误码，大约每 30 秒一次。

USB 伴音是 ASYN `EAT!` 里的明文 LPCM，写到系统默认播放设备。无线 AirPlay 不依赖这些步骤。
