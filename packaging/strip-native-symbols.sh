#!/usr/bin/env bash
# Drop local symbol tables from Skia, HarfBuzz, and Avalonia native libraries.
# Dynamic exports stay, so NativeLibrary can still load them. The host binary
# is left alone (Native AOT already strips it). Missing tools or an empty
# directory are not an error: the lean flavor has no natives.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: strip-native-symbols.sh <publish-dir>" >&2
  exit 2
fi

dir="$1"
if [ ! -d "$dir" ]; then
  echo "publish directory does not exist: ${dir}" >&2
  exit 1
fi

shopt -s nullglob
files=()
for pattern in libSkiaSharp.* libHarfBuzzSharp.* libAvaloniaNative.* av_libglesv2.*; do
  for file in "$dir"/$pattern; do
    if [ -f "$file" ]; then
      files+=("$file")
    fi
  done
done

if [ "${#files[@]}" -eq 0 ]; then
  exit 0
fi

if ! command -v strip >/dev/null 2>&1; then
  echo "strip is not installed; leaving native symbol tables in ${dir}" >&2
  exit 0
fi

if strip --help 2>&1 | grep -q -- '--strip-unneeded'; then
  strip --strip-unneeded "${files[@]}"
else
  # macOS strip. -x removes local symbols and keeps exported ones.
  strip -x "${files[@]}"
fi

for file in "${files[@]}"; do
  echo "stripped native symbols: ${file}"
done
