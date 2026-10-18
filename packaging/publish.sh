#!/usr/bin/env bash
# Publish a self-contained Native AOT build. Default RID is linux-x64.
# Cross-publishing win-x64 needs a Windows toolchain; use publish.ps1 there.
# Release builds pass DebugType=none so the output has no .pdb or .dbg.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-linux-x64}"
configuration="${2:-Release}"
args=(
  "$root/src/AirPlayServer.Host/AirPlayServer.Host.csproj"
  --configuration "$configuration"
  --runtime "$rid"
  --self-contained true
)
if [ "$configuration" = "Release" ]; then
  args+=(
    -p:DebugType=none
    -p:DebugSymbols=false
    -p:NativeDebugSymbols=false
  )
fi
dotnet publish "${args[@]}" --output "$root/artifacts/$rid"
if [ "$configuration" = "Release" ]; then
  "$root/packaging/assert-no-publish-symbols.sh" "$root/artifacts/$rid"
fi
echo "Published $rid to $root/artifacts/$rid"
