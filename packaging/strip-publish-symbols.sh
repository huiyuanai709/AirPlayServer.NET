#!/usr/bin/env bash
# Delete debug symbol files left in a Native AOT publish directory.
# Host Release builds pass DebugType=none. Avalonia, Skia, and HarfBuzz
# native packages can still drop a .pdb or .dbg beside libHarfBuzzSharp
# or libSkiaSharp. Remove those before assert-no-publish-symbols.sh.
# Usage: strip-publish-symbols.sh <publish-dir>
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: strip-publish-symbols.sh <publish-dir>" >&2
  exit 2
fi

dir="$1"
if [ ! -d "$dir" ]; then
  echo "publish directory does not exist: ${dir}" >&2
  exit 1
fi

found=0
while IFS= read -r -d '' path; do
  echo "removing debug symbol: ${path}"
  rm -f -- "$path"
  found=1
done < <(find "$dir" -type f \( \
  -iname '*.pdb' -o \
  -iname '*.dbg' -o \
  -iname '*.dwo' -o \
  -iname '*.debug' -o \
  -iname '*.sym' -o \
  -iname '*.dsym' \
\) -print0)

if [ "$found" -eq 0 ]; then
  echo "no debug symbols to remove in ${dir}"
fi
