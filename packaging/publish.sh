#!/usr/bin/env bash
# Publish a self-contained Native AOT build. Default RID is linux-x64.
# Cross-publishing win-x64 needs a Windows toolchain; use publish.ps1 there.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-linux-x64}"
configuration="${2:-Release}"
dotnet publish "$root/src/AirPlayServer.Host/AirPlayServer.Host.csproj" \
  --configuration "$configuration" \
  --runtime "$rid" \
  --self-contained true \
  --output "$root/artifacts/$rid"
echo "Published $rid to $root/artifacts/$rid"
