#!/usr/bin/env bash
# Fail if a Native AOT publish directory still contains debug symbol files.
# Usage: assert-no-publish-symbols.sh <publish-dir>
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: assert-no-publish-symbols.sh <publish-dir>" >&2
  exit 2
fi

dir="$1"
if [ ! -d "$dir" ]; then
  echo "publish directory does not exist: ${dir}" >&2
  exit 1
fi

found=()
while IFS= read -r -d '' path; do
  found+=("$path")
done < <(find "$dir" -type f \( \
  -iname '*.pdb' -o \
  -iname '*.dbg' -o \
  -iname '*.dwo' -o \
  -iname '*.debug' -o \
  -iname '*.sym' -o \
  -iname '*.dsym' \
\) -print0)

if [ "${#found[@]}" -ne 0 ]; then
  echo "publish directory contains debug symbols:" >&2
  printf '  %s\n' "${found[@]}" >&2
  exit 1
fi
