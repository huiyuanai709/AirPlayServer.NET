#!/usr/bin/env bash
# Publish a self-contained Native AOT build.
# Usage: publish.sh [rid] [configuration] [flavor]
#   rid:            linux-x64 (default), win-x64, osx-arm64, osx-x64
#   configuration:  Release (default) or Debug
#   flavor:         lean (default, no Avalonia) or ui (Avalonia mirror window)
# Cross-publishing win-x64 needs a Windows toolchain; use publish.ps1 there.
# Release builds pass DebugType=none. Third-party native .pdb/.dbg files
# are removed, then Skia/HarfBuzz symbol tables are stripped.
# Optional env: PUBLISH_VERSION, PUBLISH_INFORMATIONAL_VERSION (release.yml).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-linux-x64}"
configuration="${2:-Release}"
flavor="${3:-lean}"
case "$flavor" in
  lean)
    enable_avalonia=false
    suffix=""
    ;;
  ui)
    enable_avalonia=true
    suffix="-ui"
    ;;
  *)
    echo "flavor must be lean or ui (got: ${flavor})" >&2
    exit 2
    ;;
esac
out="$root/artifacts/${rid}${suffix}"
args=(
  "$root/src/AirPlayServer.Host/AirPlayServer.Host.csproj"
  --configuration "$configuration"
  --runtime "$rid"
  --self-contained true
  -p:EnableAvalonia="$enable_avalonia"
)
if [ -n "${PUBLISH_VERSION:-}" ]; then
  args+=(-p:Version="$PUBLISH_VERSION")
fi
if [ -n "${PUBLISH_INFORMATIONAL_VERSION:-}" ]; then
  args+=(
    -p:InformationalVersion="$PUBLISH_INFORMATIONAL_VERSION"
    -p:IncludeSourceRevisionInInformationalVersion=false
  )
fi
if [ "$configuration" = "Release" ]; then
  args+=(
    -p:DebugType=none
    -p:DebugSymbols=false
    -p:NativeDebugSymbols=false
  )
fi
dotnet publish "${args[@]}" --output "$out"
if [ "$configuration" = "Release" ]; then
  "$root/packaging/strip-publish-symbols.sh" "$out"
  "$root/packaging/strip-native-symbols.sh" "$out"
  "$root/packaging/assert-no-publish-symbols.sh" "$out"
fi
"$root/packaging/assert-publish-flavor.sh" "$out" "$flavor"
echo "Published $rid ($flavor) to $out"
