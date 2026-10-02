#!/usr/bin/env bash
# Fails if versions.env (single source of truth) disagrees with the files that tools read directly:
#   global.json            sdk.version          == DOTNET_SDK
#   src/Opportunity.Web/.nvmrc                  == NODE (major)
#   src/Opportunity.Web/package.json engines.node major == NODE
# Usage: tools/ci/verify-versions.sh   (run from anywhere; resolves the repo root itself)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
versions_file="$root/versions.env"

read_var() {
  local value
  value="$(grep -E "^$1=" "$versions_file" | tail -n1 | cut -d= -f2- | tr -d '[:space:]"')"
  if [[ -z "$value" ]]; then
    echo "::error file=versions.env::$1 is not defined in versions.env" >&2
    exit 1
  fi
  printf '%s' "$value"
}

dotnet_sdk="$(read_var DOTNET_SDK)"
node_major="$(read_var NODE)"
node_major="${node_major%%.*}"

global_json_sdk="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$root/global.json")"
nvmrc="$(tr -d '[:space:]v' < "$root/src/Opportunity.Web/.nvmrc")"
nvmrc_major="${nvmrc%%.*}"
engines_major="$(python3 -c 'import json,re,sys; m=re.search(r"\d+", json.load(open(sys.argv[1]))["engines"]["node"]); print(m.group(0) if m else "")' "$root/src/Opportunity.Web/package.json")"

status=0
check() {
  local what="$1" file="$2" expected="$3" actual="$4"
  if [[ "$expected" == "$actual" ]]; then
    echo "ok   $what: $actual"
  else
    echo "::error file=$file::$what mismatch: versions.env says '$expected' but $file says '$actual'"
    status=1
  fi
}

check ".NET SDK" "global.json" "$dotnet_sdk" "$global_json_sdk"
check "Node major" "src/Opportunity.Web/.nvmrc" "$node_major" "$nvmrc_major"
check "Node major (engines)" "src/Opportunity.Web/package.json" "$node_major" "$engines_major"

exit "$status"
