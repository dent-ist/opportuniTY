#!/usr/bin/env bash
# N-1 -> N upgrade test, deployment tier (E01-T06). Deploys the previous release's schema with ITS migrator, loads
# data (upgrade/seed.sql), upgrades with the new release's migrator, then smoke-tests: the migrator is a no-op on a
# second run, the seeded data is intact (upgrade/smoke.sql) and, with --to-api, the new API reports ready against the
# upgraded database. PostgreSQL is the versions.env image, pinned by digest. Run by .github/workflows/release.yml
# before anything is published; runs the same way locally (docs/releasing.md#upgrade-test).
#
#   tools/release/upgrade-test.sh --from MIGRATOR --to MIGRATOR [--to-api API] [--keep]
#
# MIGRATOR and API are either `image:<ref>` (a container image, e.g. image:ghcr.io/plogramer/opportunity-migrator@sha256:...)
# or `dir:<path>` (a `dotnet publish` output folder with Opportunity.Migrator.dll / Opportunity.Api.dll).
# --keep leaves the PostgreSQL container running for inspection.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"

from="" to="" to_api="" keep=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from) from="${2:?--from needs a value}"; shift 2 ;;
    --to) to="${2:?--to needs a value}"; shift 2 ;;
    --to-api) to_api="${2:?--to-api needs a value}"; shift 2 ;;
    --keep) keep=1; shift ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$from" && -n "$to" ]] || { echo "usage: $0 --from MIGRATOR --to MIGRATOR [--to-api API] [--keep]" >&2; exit 2; }
for spec in "$from" "$to" ${to_api:+"$to_api"}; do
  [[ "$spec" == image:?* || "$spec" == dir:?* ]] || { echo "error: '$spec' must be image:<ref> or dir:<path>" >&2; exit 2; }
done

versions() { sed -n "s/^$1=//p" "$root/versions.env" | tail -n1; }
postgres_image="postgres:$(versions POSTGRES)@$(versions POSTGRES_DIGEST)"

run_id="opp-upgrade-$$-$RANDOM"
network="$run_id"
pg="$run_id-postgres"
api="$run_id-api"
# Throwaway credentials for a throwaway database, generated per run.
pg_password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
app_password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
api_pid=""

log() { printf '\n== %s\n' "$*"; }
fail() { echo "::error::upgrade test: $*" >&2; exit 1; }

cleanup() {
  local rc=$?
  [[ -n "$api_pid" ]] && kill "$api_pid" 2>/dev/null || true
  if [[ $rc -ne 0 ]]; then
    docker logs "$api" 2>/dev/null | tail -n 50 || true
  fi
  docker rm -f "$api" >/dev/null 2>&1 || true
  if [[ $keep -eq 1 ]]; then
    echo "kept $pg (port $(docker port "$pg" 5432 2>/dev/null)); remove with: docker rm -f $pg && docker network rm $network"
  else
    docker rm -f -v "$pg" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
  fi
  exit "$rc"
}
trap cleanup EXIT

psql_run() { docker exec -i "$pg" psql -X -q -v ON_ERROR_STOP=1 -U postgres -d opportunity "$@"; }
schema_version() { psql_run -At -c 'SELECT coalesce(max(version), 0) FROM opportunity_migrations.schema_history'; }

# Connection string for a component: on the Docker network for images, through the published port for local folders.
connection_string() { # <spec> <user> <password>
  if [[ "$1" == image:* ]]; then
    printf 'Host=%s;Port=5432;Database=opportunity;Username=%s;Password=%s;GSS Encryption Mode=Disable' "$pg" "$2" "$3"
  else
    printf 'Host=127.0.0.1;Port=%s;Database=opportunity;Username=%s;Password=%s;GSS Encryption Mode=Disable' \
      "$(docker port "$pg" 5432 | head -n1 | sed 's/.*://')" "$2" "$3"
  fi
}

migrate() { # <spec>: runs the migrator as the database owner, returns its exit code
  local spec="$1" cs rc=0
  cs="$(connection_string "$spec" postgres "$pg_password")"
  if [[ "$spec" == image:* ]]; then
    docker run --rm --network "$network" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
      -e "ConnectionStrings__Migrator=$cs" "${spec#image:}" || rc=$?
  else
    ConnectionStrings__Migrator="$cs" DOTNET_ENVIRONMENT=Production dotnet "${spec#dir:}/Opportunity.Migrator.dll" || rc=$?
  fi
  return "$rc"
}

log "PostgreSQL ($postgres_image)"
docker network create "$network" >/dev/null
docker run -d --name "$pg" --network "$network" --shm-size 512m -p 127.0.0.1::5432 \
  -e POSTGRES_PASSWORD="$pg_password" -e POSTGRES_DB=opportunity "$postgres_image" >/dev/null
timeout 90 bash -c "until docker exec $pg pg_isready -U postgres -d opportunity -q && docker exec $pg psql -U postgres -d opportunity -Atqc 'SELECT 1' >/dev/null 2>&1; do sleep 1; done" \
  || fail "PostgreSQL did not become ready"

log "1/4 Deploy N-1: ${from}"
migrate "$from" || fail "the N-1 migrator failed"
from_version="$(schema_version)"
echo "schema version after N-1: $from_version"

log "2/4 Load data into the N-1 schema (upgrade/seed.sql)"
psql_run -o /dev/null < "$here/upgrade/seed.sql"

log "3/4 Upgrade to N: ${to}"
migrate "$to" || fail "the N migrator failed to upgrade schema $from_version"
to_version="$(schema_version)"
echo "schema version after N: $to_version"
(( to_version >= from_version )) || fail "schema went backwards ($from_version -> $to_version)"
migrate "$to" || fail "a second run of the N migrator was not a no-op"
[[ "$(schema_version)" == "$to_version" ]] || fail "a second run of the N migrator changed the schema"

log "4/4 Smoke tests"
psql_run < "$here/upgrade/smoke.sql"

if [[ -n "$to_api" ]]; then
  # The API runs as a login in opportunity_app (RLS applies), never as the owner.
  psql_run -c "CREATE ROLE upgrade_api LOGIN PASSWORD '$app_password' IN ROLE opportunity_app" >/dev/null
  settings=(
    "ConnectionStrings__App=$(connection_string "$to_api" upgrade_api "$app_password")"
    "Authentication__PublicOrigin=http://localhost:8080"
    "Authentication__Oidc__Authority=https://idp.invalid/realms/upgrade-test"
    "Authentication__Oidc__ClientId=upgrade-test"
  )
  if [[ "$to_api" == image:* ]]; then
    env_args=(); for s in "${settings[@]}"; do env_args+=(-e "$s"); done
    docker run -d --name "$api" --network "$network" --read-only --tmpfs /tmp --cap-drop ALL \
      --security-opt no-new-privileges -p 127.0.0.1::8080 "${env_args[@]}" "${to_api#image:}" >/dev/null
    api_url="http://127.0.0.1:$(docker port "$api" 8080 | head -n1 | sed 's/.*://')"
  else
    api_port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')"
    api_url="http://127.0.0.1:$api_port"
    (cd "${to_api#dir:}" && exec env "${settings[@]}" ASPNETCORE_URLS="$api_url" DOTNET_ENVIRONMENT=Production \
      dotnet Opportunity.Api.dll > "${TMPDIR:-/tmp}/$api.log" 2>&1) &
    api_pid=$!
  fi
  echo "waiting for $api_url/health/ready"
  if ! timeout 120 bash -c "until curl -fsS '$api_url/health/ready' >/dev/null 2>&1; do sleep 2; done"; then
    curl -sS "$api_url/health/ready" || true
    [[ -n "$api_pid" ]] && tail -n 50 "${TMPDIR:-/tmp}/$api.log"
    fail "the N API did not report ready against the upgraded database"
  fi
  echo "API ready: $(curl -fsS "$api_url/health/ready")"
  [[ -n "$api_pid" ]] && rm -f "${TMPDIR:-/tmp}/$api.log"
fi

log "PASSED: schema $from_version (N-1) -> $to_version (N) with data intact"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo "### Upgrade test (deployment tier)"
    echo "- N-1: \`${from}\` (schema $from_version)"
    echo "- N: \`${to}\` (schema $to_version)${to_api:+, API \`${to_api}\` ready}"
    echo "- Seeded data intact after the upgrade; second migrator run was a no-op."
  } >> "$GITHUB_STEP_SUMMARY"
fi
