#!/usr/bin/env bash
# Print tag=, version=, and prerelease= for .github/workflows/release.yml.
# EVENT_NAME is push or workflow_dispatch.
# REF_NAME is the tag name for a tag push (github.ref_name).
# INPUT_VERSION is the workflow_dispatch version input.
set -euo pipefail

event="${EVENT_NAME:-}"
case "$event" in
  workflow_dispatch) raw="${INPUT_VERSION:-}" ;;
  push) raw="${REF_NAME:-}" ;;
  *)
    echo "EVENT_NAME must be push or workflow_dispatch." >&2
    exit 2
    ;;
esac

# Trim leading and trailing whitespace. Dispatch inputs are sometimes padded.
raw="${raw#"${raw%%[![:space:]]*}"}"
raw="${raw%"${raw##*[![:space:]]}"}"

if [ -z "$raw" ]; then
  echo "Version is empty. Push a v* tag or pass a version to workflow_dispatch." >&2
  exit 1
fi

case "$raw" in
  [vV]*) tag="v${raw:1}" ;;
  *) tag="v${raw}" ;;
esac

# SemVer with a leading v. Match the whole string, not one line: a newline
# inside the input must not be able to emit extra key=value lines.
# Build metadata (+) is rejected: it is awkward in git tags and asset names.
semver_re='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'
if [[ ! "$tag" =~ $semver_re ]]; then
  echo "Version must look like 1.2.3 or v1.2.3, with an optional prerelease such as v1.2.3-rc.1. Got: ${tag}" >&2
  exit 1
fi

version="${tag#v}"
prerelease=false
case "$version" in
  *-*) prerelease=true ;;
esac

printf 'tag=%s\n' "$tag"
printf 'version=%s\n' "$version"
printf 'prerelease=%s\n' "$prerelease"
