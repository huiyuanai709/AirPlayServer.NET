@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting administrator approval. The Apple parent stays on usbccgp.
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows-usb-driver.ps1" -Setup -Elevated
echo.
pause
