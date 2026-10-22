#!/usr/bin/env bash
# Fixture check for the release zip: keep the binary and runtime libraries,
# drop debug symbols, and keep the linux unit file on linux-x64 only.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stage="$root/packaging/stage-release.sh"
assert="$root/packaging/assert-no-publish-symbols.sh"

tmp="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp"
}
trap cleanup EXIT

if command -v python3 >/dev/null 2>&1; then
  py=python3
elif command -v python >/dev/null 2>&1; then
  py=python
else
  echo "python3 is required" >&2
  exit 1
fi

fail() {
  echo "stage-release-test: $*" >&2
  exit 1
}

write_publish() {
  local dir="$1"
  local binary="$2"
  mkdir -p "$dir"
  printf 'binary' > "$dir/$binary"
  printf 'pdb' > "$dir/AirPlayServer.Host.pdb"
  printf 'dbg' > "$dir/AirPlayServer.Host.dbg"
  printf 'PDB' > "$dir/Extra.PDB"
  printf 'dwo' > "$dir/extra.dwo"
  printf 'debug' > "$dir/extra.debug"
  printf 'sym' > "$dir/extra.sym"
  printf 'dll' > "$dir/SomeNative.dll"
  printf 'so' > "$dir/libsomething.so"
  printf 'dylib' > "$dir/libsomething.dylib"
  printf 'junk' > "$dir/deps.json"
  mkdir -p "$dir/nested"
  printf 'nested-pdb' > "$dir/nested/nested.pdb"
}

expect_zip() {
  local zip_path="$1"
  local rid="$2"
  local binary="$3"
  local expect_service="$4"
  ZIP_PATH="$zip_path" RID="$rid" BINARY_NAME="$binary" EXPECT_SERVICE="$expect_service" "$py" - <<'PY'
import os
import sys
import zipfile

zip_path = os.environ["ZIP_PATH"]
rid = os.environ["RID"]
binary = os.environ["BINARY_NAME"]
expect_service = os.environ["EXPECT_SERVICE"] == "1"
flavor = os.environ.get("FLAVOR", "lean")
suffix = "-ui" if flavor == "ui" else ""
prefix = f"AirPlayServer.Host-v0.0.0-test-{rid}{suffix}"
with zipfile.ZipFile(zip_path) as archive:
    names = set(archive.namelist())
    version = archive.read(f"{prefix}/VERSION.txt").decode()
    run = archive.read(f"{prefix}/RUN.txt").decode()
    service_text = ""
    service_name = f"{prefix}/airplayserver.service"
    if service_name in names:
        service_text = archive.read(service_name).decode()

required = {
    f"{prefix}/RUN.txt",
    f"{prefix}/LICENSE",
    f"{prefix}/NOTICE.md",
    f"{prefix}/VERSION.txt",
    f"{prefix}/{binary}",
    f"{prefix}/SomeNative.dll",
    f"{prefix}/libsomething.so",
    f"{prefix}/libsomething.dylib",
}
service = f"{prefix}/airplayserver.service"
if expect_service:
    required.add(service)
elif service in names:
    sys.exit("service file does not belong in " + rid)

missing = sorted(required - names)
if missing:
    sys.exit("missing " + ", ".join(missing))

banned_parts = (
    "AirPlayServer.Host.pdb",
    "AirPlayServer.Host.dbg",
    "Extra.PDB",
    "extra.dwo",
    "extra.debug",
    "extra.sym",
    "deps.json",
    "nested.pdb",
)
present = sorted(
    name
    for name in names
    if name.split("/")[-1] in banned_parts
    or name.lower().endswith((".pdb", ".dbg", ".dwo", ".debug", ".sym", ".dsym"))
)
if present:
    sys.exit("unexpected entries " + ", ".join(present))

expected_version = f"tag=v0.0.0-test\nrid={rid}\nbinary={binary}\nlicense=GPL-3.0-only\nflavor={flavor}\n"
if expect_service and flavor == "ui" and "--no-window" not in service_text:
    sys.exit("ui linux unit file must pass --no-window")
if expect_service and flavor == "lean" and "--no-window" in service_text:
    sys.exit("lean linux unit file must not force --no-window")
if version != expected_version:
    sys.exit("VERSION.txt was " + version)

if ".pdb" not in run or "调试符号" not in run:
    sys.exit("RUN.txt no longer mentions that debug symbols are omitted")
PY
}

write_publish "$tmp/linux" "AirPlayServer.Host"
# 0600 plus chmod +x becomes 0711 on Unix. The zip must still say 0755,
# including when this fixture runs on Windows where chmod does not stick.
# Sidecars are 0777 here so a leaked host mode fails the 0644 check.
chmod 600 "$tmp/linux/AirPlayServer.Host" || true
chmod 777 "$tmp/linux/SomeNative.dll" "$tmp/linux/libsomething.so" "$tmp/linux/libsomething.dylib" || true
"$stage" linux-x64 v0.0.0-test "$tmp/linux" "$tmp/linux.zip"
expect_zip "$tmp/linux.zip" linux-x64 "AirPlayServer.Host" 1
"$stage" linux-x64 v0.0.0-test "$tmp/linux" "$tmp/linux-ui.zip" ui
FLAVOR=ui expect_zip "$tmp/linux-ui.zip" linux-x64 "AirPlayServer.Host" 1

mode="$("$py" - "$tmp/linux.zip" <<'PY'
import sys
import zipfile
name = "AirPlayServer.Host-v0.0.0-test-linux-x64/AirPlayServer.Host"
with zipfile.ZipFile(sys.argv[1]) as archive:
    info = archive.getinfo(name)
    print(info.external_attr >> 16)
    if info.create_system != 3:
        sys.exit("linux binary create_system is not Unix")
PY
)"
if [ $((mode & 0777)) -ne $((0755)) ]; then
  fail "linux binary mode is ${mode}, want 0755"
fi

write_publish "$tmp/win" "AirPlayServer.Host.exe"
# Host mode must not leak into the zip. Windows CPython reports .exe as 0777.
chmod 777 "$tmp/win/AirPlayServer.Host.exe" "$tmp/win/SomeNative.dll" "$tmp/win/libsomething.so" || true
"$stage" win-x64 v0.0.0-test "$tmp/win" "$tmp/win.zip"
expect_zip "$tmp/win.zip" win-x64 "AirPlayServer.Host.exe" 0
"$py" - "$tmp/win.zip" "$tmp/linux.zip" <<'PY'
import sys
import zipfile

win, linux = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(win) as archive:
    text = archive.read("AirPlayServer.Host-v0.0.0-test-win-x64/windows-usb-driver.ps1").decode()
    setup = archive.read("AirPlayServer.Host-v0.0.0-test-win-x64/windows-usb-setup.cmd").decode()
if "usbccgp" not in text or "Do not run Zadig" not in text or "does not vendor libusb0.sys" not in text:
    sys.exit("windows helper is missing the driver rules")
if "-Setup" not in text or "release_1.4.0.2" not in text or "--device-id=" not in text:
    sys.exit("windows helper is missing the one-step filter install")
if "--all-classes" in text or "--device-classes" in text:
    sys.exit("windows helper must not offer a class filter")
if "-Setup" not in setup:
    sys.exit("windows-usb-setup.cmd must launch -Setup")
with zipfile.ZipFile(linux) as archive:
    names = archive.namelist()
    if any(name.endswith("/windows-usb-driver.ps1") or name.endswith("/windows-usb-setup.cmd") for name in names):
        sys.exit("linux zip must not carry the windows helper")
PY

"$py" - "$tmp/linux.zip" "$tmp/win.zip" <<'PY'
import sys
import zipfile

def check(path, binary, binary_perm):
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist():
            if info.create_system != 3:
                sys.exit(f"{info.filename} create_system is {info.create_system}, want Unix")
            mode = info.external_attr >> 16
            perm = mode & 0o777
            name = info.filename.split("/")[-1]
            want = binary_perm if name == binary else 0o644
            if perm != want:
                sys.exit(f"{info.filename} mode is {oct(mode)}, want {oct(want)}")
            if name != binary and perm & 0o111:
                sys.exit(f"{name} must not be marked executable (mode {mode})")

check(sys.argv[1], "AirPlayServer.Host", 0o755)
check(sys.argv[2], "AirPlayServer.Host.exe", 0o644)
PY

win_mode="$("$py" - "$tmp/win.zip" <<'PY'
import sys
import zipfile
name = "AirPlayServer.Host-v0.0.0-test-win-x64/AirPlayServer.Host.exe"
with zipfile.ZipFile(sys.argv[1]) as archive:
    print(archive.getinfo(name).external_attr >> 16)
PY
)"
if [ $((win_mode & 0111)) -ne 0 ]; then
  fail "win-x64 binary must not be marked executable (mode ${win_mode})"
fi
if [ $((win_mode & 0777)) -ne $((0644)) ]; then
  fail "win-x64 binary mode is ${win_mode}, want 0644"
fi

if "$stage" linux-x64 v0.0.0-test "$tmp/empty-publish" "$tmp/missing.zip" >/dev/null 2>"$tmp/missing.err"; then
  fail "missing binary should fail"
fi
mkdir -p "$tmp/empty-publish"
if "$stage" linux-x64 v0.0.0-test "$tmp/empty-publish" "$tmp/missing.zip" >/dev/null 2>"$tmp/missing.err"; then
  fail "missing binary should fail"
fi
if ! grep -q "missing published binary" "$tmp/missing.err"; then
  fail "missing-binary error was: $(cat "$tmp/missing.err")"
fi

mkdir -p "$tmp/clean"
printf 'binary' > "$tmp/clean/AirPlayServer.Host"
printf 'dll' > "$tmp/clean/SomeNative.dll"
"$assert" "$tmp/clean"

mkdir -p "$tmp/dirty"
printf 'pdb' > "$tmp/dirty/AirPlayServer.Host.pdb"
if "$assert" "$tmp/dirty" >/dev/null 2>"$tmp/dirty.err"; then
  fail "assert-no-publish-symbols should fail when a pdb is present"
fi
if ! grep -q "debug symbols" "$tmp/dirty.err"; then
  fail "symbol assert error was: $(cat "$tmp/dirty.err")"
fi

mkdir -p "$tmp/dirty-case"
printf 'dbg' > "$tmp/dirty-case/Host.DBG"
if "$assert" "$tmp/dirty-case" >/dev/null 2>"$tmp/dirty-case.err"; then
  fail "assert-no-publish-symbols should fail when a .DBG is present"
fi

strip="$root/packaging/strip-publish-symbols.sh"
mkdir -p "$tmp/strip/nested"
printf 'binary' > "$tmp/strip/AirPlayServer.Host"
printf 'so' > "$tmp/strip/libSkiaSharp.so"
printf 'pdb' > "$tmp/strip/libHarfBuzzSharp.pdb"
printf 'dbg' > "$tmp/strip/nested/libHarfBuzzSharp.DBG"
"$strip" "$tmp/strip" >"$tmp/strip.out"
if [ -e "$tmp/strip/libHarfBuzzSharp.pdb" ] || [ -e "$tmp/strip/nested/libHarfBuzzSharp.DBG" ]; then
  fail "strip-publish-symbols left a debug symbol"
fi
if [ ! -f "$tmp/strip/AirPlayServer.Host" ] || [ ! -f "$tmp/strip/libSkiaSharp.so" ]; then
  fail "strip-publish-symbols removed a runtime file"
fi
"$assert" "$tmp/strip"
if ! grep -q "libHarfBuzzSharp.pdb" "$tmp/strip.out"; then
  fail "strip log was: $(cat "$tmp/strip.out")"
fi

mkdir -p "$tmp/natives"
cp /bin/true "$tmp/natives/libSkiaSharp.so"
printf 'leave' > "$tmp/natives/AirPlayServer.Host"
"$root/packaging/strip-native-symbols.sh" "$tmp/natives" >"$tmp/natives.out"
if ! grep -q "libSkiaSharp.so" "$tmp/natives.out"; then
  fail "strip-native-symbols did not see libSkiaSharp.so: $(cat "$tmp/natives.out")"
fi
if [ ! -f "$tmp/natives/AirPlayServer.Host" ] || [ ! -f "$tmp/natives/libSkiaSharp.so" ]; then
  fail "strip-native-symbols removed a runtime file"
fi

echo "stage-release-test ok"
