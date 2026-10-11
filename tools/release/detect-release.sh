#!/usr/bin/env bash
# Decides whether a push to main is a merged release PR (E01-T06). release-please bumps .release-please-manifest.json
# only in its release PR, so "the manifest names a version that has no v* tag yet" means a release is due. It is
# released only from the commit that bumped the manifest (the release PR's merge commit); a later push while a release
# is still untagged reports `pending` and does nothing, so a failed release is retried by re-running its own run.
#
# Outputs (to $GITHUB_OUTPUT when set, always to stdout):
#   release=true|false   pending=true|false   version=X.Y.Z   tag=vX.Y.Z   previous-tag=vA.B.C (empty if none)
# Needs the full history and tags (actions/checkout with fetch-depth: 0).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
manifest="$root/.release-please-manifest.json"
head="$(git -C "$root" rev-parse HEAD)"

version="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["."])' "$manifest")"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$ ]] \
  || { echo "::error file=.release-please-manifest.json::'$version' is not a SemVer version" >&2; exit 1; }
tag="v$version"

release=false pending=false
if [[ "$version" == 0.0.0 ]]; then
  echo "No release yet (manifest at 0.0.0)."
elif git -C "$root" rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  echo "$tag is already released."
else
  bumped="$(git -C "$root" log -1 --format=%H -- .release-please-manifest.json)"
  if [[ "$bumped" == "$head" ]]; then
    release=true
    echo "Release $tag: this commit merged the release PR."
  else
    pending=true
    echo "::warning::$tag is not tagged yet but was merged in $bumped, not in this commit. Re-run the failed Release run of $bumped to finish it; the release PR is not updated meanwhile."
  fi
fi

# The newest earlier release (SemVer order), the N-1 of the upgrade test.
previous=""
while IFS= read -r t; do
  [[ "$t" != "$tag" ]] || continue
  [[ "$t" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || continue
  previous="$t"
  break
done < <(git -C "$root" tag --list 'v*' --sort=-v:refname --merged "$head")

{
  echo "release=$release"
  echo "pending=$pending"
  echo "version=$version"
  echo "tag=$tag"
  echo "previous-tag=$previous"
} | tee -a "${GITHUB_OUTPUT:-/dev/null}"
