#!/usr/bin/env bash
# Usage: assert-publish-flavor.sh <publish-dir> <lean|ui>
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: assert-publish-flavor.sh <publish-dir> <lean|ui>" >&2
  exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v python3 >/dev/null 2>&1; then
  py=python3
elif command -v python >/dev/null 2>&1; then
  py=python
else
  echo "python3 is required" >&2
  exit 1
fi

exec "$py" "$root/packaging/assert-publish-flavor.py" "$1" "$2"
