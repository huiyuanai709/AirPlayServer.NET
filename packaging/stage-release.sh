#!/usr/bin/env bash
# Stage a Native AOT publish directory into a release zip.
# Usage: stage-release.sh <rid> <tag> <publish-dir> <zip-path> [lean|ui]
# flavor defaults to lean. ui zips use the -ui name suffix inside the archive.
# The zip contains the binary, RUN.txt, LICENSE, NOTICE.md, and VERSION.txt.
# linux-x64 also gets airplayserver.service.
# win-x64 also gets windows-usb-driver.ps1. That script does not contain a driver.
# osx-arm64 and osx-x64 pack the same Mach-O name as linux, plus any
# Avalonia Skia/HarfBuzz .dylib files sitting next to the binary.
# Runtime *.dll, *.so, and *.dylib are kept. Debug symbols are not.
# No secrets are copied.
set -euo pipefail

if [ "$#" -lt 4 ] || [ "$#" -gt 5 ]; then
  echo "usage: stage-release.sh <rid> <tag> <publish-dir> <zip-path> [lean|ui]" >&2
  exit 2
fi

rid="$1"
tag="$2"
publish_dir="$3"
zip_path="$4"
flavor="${5:-lean}"
case "$flavor" in
  lean|ui) ;;
  *)
    echo "flavor must be lean or ui (got: ${flavor})" >&2
    exit 2
    ;;
esac
name_suffix=""
if [ "$flavor" = "ui" ]; then
  name_suffix="-ui"
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "$rid" in
  linux-x64|osx-arm64|osx-x64) binary="AirPlayServer.Host" ;;
  win-x64) binary="AirPlayServer.Host.exe" ;;
  *)
    echo "unsupported rid: ${rid}" >&2
    exit 2
    ;;
esac

case "$tag" in
  v*.*.*) ;;
  *)
    echo "tag must look like v1.2.3" >&2
    exit 2
    ;;
esac
case "$tag" in
  *[!A-Za-z0-9._-]*)
    echo "unsafe tag: ${tag}" >&2
    exit 2
    ;;
esac

if [ ! -d "$publish_dir" ]; then
  echo "publish directory does not exist: ${publish_dir}" >&2
  exit 1
fi

stage="$(mktemp -d)"
cleanup() {
  rm -rf "$stage"
}
trap cleanup EXIT

shopt -s nullglob
copied_binary=false
for extra in "$publish_dir"/*; do
  [ -f "$extra" ] || continue
  base="${extra##*/}"
  lower="${base,,}"
  case "$lower" in
    *.pdb | *.dbg | *.dwo | *.debug | *.sym | *.dsym)
      continue
      ;;
  esac
  case "$base" in
    "$binary" | *.dll | *.so | *.dylib)
      cp "$extra" "$stage/"
      if [ "$base" = "$binary" ]; then
        copied_binary=true
      fi
      ;;
  esac
done

if [ "$copied_binary" != true ]; then
  echo "missing published binary: ${publish_dir}/${binary}" >&2
  exit 1
fi

if [ "$rid" = "linux-x64" ] || [ "$rid" = "osx-arm64" ] || [ "$rid" = "osx-x64" ]; then
  chmod +x "$stage/$binary"
fi
if [ "$rid" = "win-x64" ]; then
  cp "$root/packaging/windows-usb-driver.ps1" "$stage/"
fi
if [ "$rid" = "linux-x64" ]; then
  cp "$root/packaging/airplayserver.service" "$stage/"
  if [ "$flavor" = "ui" ]; then
    # The UI binary opens a window unless told not to. The unit file is a
    # headless stub, so it passes --no-window.
    sed -i 's|--name AirPlayServer.NET|--name AirPlayServer.NET --no-window|' "$stage/airplayserver.service"
  fi
fi

cp "$root/LICENSE" "$root/NOTICE.md" "$root/packaging/RUN.txt" "$stage/"
printf 'tag=%s\nrid=%s\nbinary=%s\nlicense=GPL-3.0-only\nflavor=%s\n' "$tag" "$rid" "$binary" "$flavor" > "$stage/VERSION.txt"

mkdir -p -- "$(dirname -- "$zip_path")"
case "$zip_path" in
  /*) ;;
  *) zip_path="${PWD}/${zip_path}" ;;
esac

if command -v python3 >/dev/null 2>&1; then
  py=python3
elif command -v python >/dev/null 2>&1; then
  py=python
else
  echo "python3 is required to build the release zip." >&2
  exit 1
fi

expect_exec=0
expect_service=0
expect_usb_helper=0
if [ "$rid" = "linux-x64" ] || [ "$rid" = "osx-arm64" ] || [ "$rid" = "osx-x64" ]; then
  expect_exec=1
fi
if [ "$rid" = "linux-x64" ]; then
  expect_service=1
fi
if [ "$rid" = "win-x64" ]; then
  expect_usb_helper=1
fi

STAGE_DIR="$stage" \
  ZIP_PATH="$zip_path" \
  ZIP_PREFIX="AirPlayServer.Host-${tag}-${rid}${name_suffix}" \
  BINARY_NAME="$binary" \
  EXPECT_EXEC="$expect_exec" \
  EXPECT_SERVICE="$expect_service" \
  EXPECT_USB_HELPER="$expect_usb_helper" \
  "$py" - <<'PY'
import os
import sys
import zipfile

stage = os.environ["STAGE_DIR"]
dest = os.environ["ZIP_PATH"]
prefix = os.environ["ZIP_PREFIX"]
binary = os.environ["BINARY_NAME"]
expect_exec = os.environ["EXPECT_EXEC"] == "1"
expect_service = os.environ["EXPECT_SERVICE"] == "1"
expect_usb_helper = os.environ["EXPECT_USB_HELPER"] == "1"
# High 16 bits are the Unix mode, including the regular-file type.
# Set every entry explicitly. Windows CPython reports .exe as 0777, and
# Git Bash chmod does not produce a Unix execute bit for the linux binary.
exec_attr = 0o100755 << 16
file_attr = 0o100644 << 16

os.makedirs(os.path.dirname(dest) or ".", exist_ok=True)
if os.path.exists(dest):
    os.remove(dest)

with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for dirpath, dirnames, filenames in os.walk(stage):
        dirnames.sort()
        for name in sorted(filenames):
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, stage).replace("\\", "/")
            arcname = prefix + "/" + rel
            archive.write(path, arcname)
            info = archive.getinfo(arcname)
            info.create_system = 3  # Unix, so unzip honors external_attr
            if expect_exec and name == binary:
                info.external_attr = exec_attr
            else:
                info.external_attr = file_attr

    names = set(archive.namelist())
    required = [
        prefix + "/RUN.txt",
        prefix + "/LICENSE",
        prefix + "/NOTICE.md",
        prefix + "/VERSION.txt",
        prefix + "/" + binary,
    ]
    service = prefix + "/airplayserver.service"
    if expect_service:
        required.append(service)
    elif service in names:
        sys.exit("service file does not belong in this rid")
    helper = prefix + "/windows-usb-driver.ps1"
    if expect_usb_helper:
        required.append(helper)
    elif helper in names:
        sys.exit("windows usb helper does not belong in this rid")

    missing = [name for name in required if name not in names]
    if missing:
        sys.exit("zip missing " + ", ".join(missing))

    symbol_suffixes = (".pdb", ".dbg", ".dwo", ".debug", ".sym", ".dsym")
    symbols = sorted(name for name in names if name.lower().endswith(symbol_suffixes))
    if symbols:
        sys.exit("zip contains debug symbols: " + ", ".join(symbols))

    if expect_exec:
        mode = archive.getinfo(prefix + "/" + binary).external_attr >> 16
        if mode & 0o111 == 0:
            sys.exit(binary + " is not marked executable in the zip")
PY

echo "Wrote ${zip_path}"
