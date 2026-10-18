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
prefix = f"AirPlayServer.Host-v0.0.0-test-{rid}"

with zipfile.ZipFile(zip_path) as archive:
    names = set(archive.namelist())
    version = archive.read(f"{prefix}/VERSION.txt").decode()
    run = archive.read(f"{prefix}/RUN.txt").decode()

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

expected_version = f"tag=v0.0.0-test\nrid={rid}\nbinary={binary}\nlicense=GPL-3.0-only\n"
if version != expected_version:
    sys.exit("VERSION.txt was " + version)

if ".pdb" not in run or "调试符号" not in run:
    sys.exit("RUN.txt no longer mentions that debug symbols are omitted")
PY
}

write_publish "$tmp/linux" "AirPlayServer.Host"
"$stage" linux-x64 v0.0.0-test "$tmp/linux" "$tmp/linux.zip"
expect_zip "$tmp/linux.zip" linux-x64 "AirPlayServer.Host" 1

mode="$("$py" - "$tmp/linux.zip" <<'PY'
import sys
import zipfile
name = "AirPlayServer.Host-v0.0.0-test-linux-x64/AirPlayServer.Host"
with zipfile.ZipFile(sys.argv[1]) as archive:
    print(archive.getinfo(name).external_attr >> 16)
PY
)"
if [ $((mode & 0111)) -eq 0 ]; then
  fail "linux binary is not executable in the zip (mode ${mode})"
fi

write_publish "$tmp/win" "AirPlayServer.Host.exe"
"$stage" win-x64 v0.0.0-test "$tmp/win" "$tmp/win.zip"
expect_zip "$tmp/win.zip" win-x64 "AirPlayServer.Host.exe" 0

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

echo "stage-release-test ok"
