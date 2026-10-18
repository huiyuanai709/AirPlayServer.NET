#!/usr/bin/env bash
# Create or update a GitHub Release and upload the host zips.
# Usage: publish-github-release.sh <tag> <commit-sha> <true|false> <notes-file> <asset>...
# Requires gh, GH_TOKEN, and GITHUB_REPOSITORY. Refuses to upload when the tag
# already points at a different commit.
set -euo pipefail

if [ "$#" -lt 5 ]; then
  echo "usage: publish-github-release.sh <tag> <commit-sha> <true|false> <notes-file> <asset>..." >&2
  exit 2
fi

tag="$1"
sha="$2"
prerelease="$3"
notes="$4"
shift 4

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

if ! printf '%s\n' "$sha" | grep -Eq '^[0-9a-fA-F]{40,64}$'; then
  echo "commit sha looks wrong: ${sha}" >&2
  exit 2
fi

case "$prerelease" in
  true | false) ;;
  *)
    echo "prerelease must be true or false" >&2
    exit 2
    ;;
esac

if [ ! -f "$notes" ]; then
  echo "missing notes file: ${notes}" >&2
  exit 1
fi

for asset in "$@"; do
  if [ ! -f "$asset" ]; then
    echo "missing asset: ${asset}" >&2
    exit 1
  fi
done

repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
if [ -z "${GH_TOKEN:-}" ] && [ -z "${GITHUB_TOKEN:-}" ]; then
  echo "GH_TOKEN is required" >&2
  exit 2
fi
export GH_PROMPT_DISABLED=1

sha_lc=$(printf '%s' "$sha" | tr 'A-F' 'a-f')
api_err="$(mktemp)"
cleanup() {
  rm -f "$api_err"
}
trap cleanup EXIT

set +e
object_type=$(gh api "repos/${repo}/git/ref/tags/${tag}" --jq .object.type 2>"$api_err")
api_status=$?
set -e

if [ "$api_status" -ne 0 ]; then
  if grep -q 'HTTP 404' "$api_err"; then
    object_type=""
  else
    cat "$api_err" >&2
    exit "$api_status"
  fi
fi

if [ -n "$object_type" ]; then
  object_sha=$(gh api "repos/${repo}/git/ref/tags/${tag}" --jq .object.sha)
  case "$object_type" in
    commit) ;;
    tag)
      object_sha=$(gh api "repos/${repo}/git/tags/${object_sha}" --jq .object.sha)
      ;;
    *)
      echo "Tag ${tag} points at a ${object_type}, not a commit." >&2
      exit 1
      ;;
  esac
  object_sha=$(printf '%s' "$object_sha" | tr 'A-F' 'a-f')
  if [ "$object_sha" != "$sha_lc" ]; then
    echo "Tag ${tag} already points at ${object_sha}, not ${sha_lc}." >&2
    echo "Delete that tag and its Release first, or re-run on the commit the tag already names." >&2
    exit 1
  fi
fi

title="AirPlayServer.NET ${tag}"
args=(--repo "$repo" --title "$title" --notes-file "$notes")
if [ "$prerelease" = "true" ]; then
  args+=(--prerelease --latest=false)
fi

if gh release view "$tag" --repo "$repo" >/dev/null 2>&1; then
  # Re-runs replace assets of the same name, then refresh the notes.
  # --clobber deletes the previous asset before the new upload finishes.
  gh release upload "$tag" --repo "$repo" --clobber "$@"
  gh release edit "$tag" "${args[@]}" --draft=false
else
  gh release create "$tag" --target "$sha_lc" "${args[@]}" "$@"
fi
