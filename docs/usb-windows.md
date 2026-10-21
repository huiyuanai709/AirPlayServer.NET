# Windows wired USB

AirPlayServer.NET receives the QuickTime-over-USB screen with libusb-1.0. The host process only reads driver state and claims the QuickTime interface (class `0xFF`, subclass `0x2A`). It does not install a kernel driver, and it does not link the Qt widget toolkit.

Wireless AirPlay does not need this page. `--usb` stays off unless you pass it.

## What has to be true

1. `libusb-1.0.dll` sits next to `AirPlayServer.Host.exe`, or on the system `PATH`. This is the user-mode library. It is not a kernel driver.
2. The Apple composite parent stays on Windows `usbccgp` (or Apple's own `usbaapl64` / `appleusb` service). Apple Mobile Device Support, or the Microsoft Store Apple Devices package, provides that stack.
3. A libusb-win32 **device** UpperFilter is installed on that parent instance. WinUSB cannot issue `SET_CONFIGURATION`, and the QuickTime switch needs that request. The filter lets libusb change configuration while the Apple parent driver remains.
4. Do not run Zadig on the Apple composite parent. Replacing that parent with WinUSB or libusbK removes the Apple function driver. This project will refuse that shape and tell you to restore `usbccgp`.

The shape follows the public iPhoneMirror driver split: the mirror process does not rebind USB, and capture is a per-device libusb-win32 filter on the Apple parent rather than a replacement of it. The installer itself is not copied from that project. This repository does not bundle `libusb0.sys`.

## Helper

The win-x64 zip includes `windows-usb-driver.ps1`. From a source checkout it lives in `packaging/`.

Check, without changing anything:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -Check
```

Copy the official user-mode DLL next to the host (libusb 1.0.30 from the libusb GitHub release; the script prints the SHA256):

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -FetchLibusb -HostDir .
```

Install the filter. Download the official libusb-win32 binary zip yourself, then run this elevated. The script selects only Apple parents whose service is `usbccgp` or Apple's own service, and it calls `install-filter.exe install --device=<that instance>`. It does not pass `--all-classes` or `--device-classes`.

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-usb-driver.ps1 -InstallFilter -FilterZip C:\path\libusb-win32-bin.zip
```

Unplug the phone, plug it back in, unlock it, and tap Trust This Computer. Start the host with `--usb`.

`-InstanceId USB\VID_05AC&PID_…\…` limits the filter to one parent. A parent whose service is `WinUSB`, `libusbK`, or `libusb0` is skipped.

## What the log means

| libusb code | Meaning | Next step |
| --- | --- | --- |
| library not loaded | `libusb-1.0.dll` is missing | `-FetchLibusb` or copy the DLL. Wireless keeps running. |
| `-12` NOT_SUPPORTED | WinUSB cannot change configuration | Keep `usbccgp`. Install the device UpperFilter. Do not Zadig the parent. |
| `-3` ACCESS | The device is busy or not openable | Same filter path. Close other USB tools. Replug. |
| `-6` BUSY | The interface is claimed | Close other tools, then replug after the filter is installed. |
| `-4` / `-5` | The phone is gone | Unlock, trust this computer, replug. |

A missing library and a failed claim are different messages. Claim failures name the libusb code and the next step. Startup with `--usb` also prints the read-only parent check.

## Linux and macOS

No extra driver. Linux needs `libusb-1.0.so.0` (`apt install libusb-1.0-0`). macOS needs `libusb-1.0.0.dylib` (`brew install libusb`). The kernel detaches its own interface driver when libusb asks. Do not install a Windows filter there.

## 中文

父设备保持 `usbccgp`（Apple Mobile Device Support 或 Microsoft Store 的 Apple Devices）。不要用 Zadig 把 Apple 复合父设备换成 WinUSB。`libusb-1.0.dll` 放在程序旁边。QuickTime 切换配置需要 `SET_CONFIGURATION`，WinUSB 做不到，所以要在**这一台设备实例**上安装 libusb-win32 的 UpperFilter，而不是替换父驱动。发布包里的 `windows-usb-driver.ps1` 只检查、下载用户态 DLL，或调用你自己准备的官方 `install-filter.exe`。本仓库不附带 `libusb0.sys`，也不下载 Apple 的安装包。装完过滤驱动后重新插拔，解锁并信任电脑，再用 `--usb` 启动。无线 AirPlay 不依赖这些步骤。
