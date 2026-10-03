#!/usr/bin/env bash
# Benchmark environments (E17-T03, baseline §29). Provisions, captures and destroys the two reference topologies with
# image tags and digests from ../../versions.env and secrets from ../docker-compose/.env (created on first use).
# See docs/benchmarks/reference-environments.md.
#
#   ./bench.sh up      --profile dev|reference [--relaxed] [--infra-only] [--build] [--no-ulimits]
#   ./bench.sh capture --profile dev|reference [--out FILE] [-- <extra capture-env options>]
#   ./bench.sh down    --profile dev|reference          destroys containers AND volumes (runs start clean)
#   ./bench.sh ps|config|logs --profile dev|reference
#
#   dev        developer-regression: the Compose developer profile + compose.bench.yaml (+ compose.bench-relaxed.yaml
#              with --relaxed, nightly only per Q-05). Project opportunity-bench-dev.
#   reference  enterprise-reference: compose.reference.yaml (3 OpenSearch nodes, PostgreSQL primary + replica,
#              RabbitMQ, SeaweedFS, API x2, worker pools). Project opportunity-ref. --infra-only skips app services.
#   --no-ulimits  hosts that cannot raise rlimits (rootless Docker, sandboxed CI): no memlock/nofile, no heap locking.
#
# Uses the same host ports as the developer profile: stop that stack first (deploy/docker-compose/opportunity.sh down).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
versions_env="$repo_root/versions.env"
dev_dir="$repo_root/deploy/docker-compose"
env_file="$dev_dir/.env"

die() { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

command="${1:-help}"
[[ $# -gt 0 ]] && shift
profile=dev relaxed=0 infra_only=0 build=0 no_ulimits=0 out="" extra=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) profile="${2:?--profile needs dev or reference}"; shift 2 ;;
    --relaxed) relaxed=1; shift ;;
    --infra-only) infra_only=1; shift ;;
    --build) build=1; shift ;;
    --no-ulimits) no_ulimits=1; shift ;;
    --out) out="${2:?--out needs a file}"; shift 2 ;;
    --) shift; extra=("$@"); break ;;
    *) die "unknown option $1" ;;
  esac
done

case "$profile" in
  dev | developer-regression) profile=dev project="${BENCH_PROJECT:-opportunity-bench-dev}" ;;
  reference | enterprise-reference) profile=reference project="${BENCH_PROJECT:-opportunity-ref}" ;;
  *) die "--profile must be dev or reference" ;;
esac
[[ "$profile" == reference && "$relaxed" == 1 ]] && die "--relaxed is not allowed on the reference profile (decision Q-05)"

env_value() { sed -n "s/^$1=//p" "$env_file" | tail -n 1; }

compose() {
  local files=()
  if [[ "$profile" == dev ]]; then
    files=(-f "$dev_dir/compose.yaml" -f "$here/compose.bench.yaml")
    [[ "$relaxed" == 1 ]] && files+=(-f "$here/compose.bench-relaxed.yaml")
    [[ "$no_ulimits" == 1 ]] && files+=(-f "$here/compose.no-ulimits.yaml")
  else
    files=(-f "$here/compose.reference.yaml")
    [[ "$no_ulimits" == 1 ]] && files+=(-f "$here/compose.reference-no-ulimits.yaml")
    [[ "$infra_only" == 1 ]] || files+=(--profile app)
  fi
  docker compose -p "$project" --env-file "$versions_env" --env-file "$env_file" "${files[@]}" "$@"
}

cmd_up() {
  "$dev_dir/opportunity.sh" init
  local args=(up -d --wait)
  [[ "$build" == 1 ]] && args+=(--build)
  compose "${args[@]}"
  echo "Up: $profile ($project). Next: $0 capture --profile $profile"
}

cmd_down() {
  # Destroy, not stop: every benchmark run starts from empty volumes, so state never leaks between runs.
  compose down --volumes --remove-orphans
}

cmd_capture() {
  local stamp profile_name
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  profile_name=$([[ "$profile" == dev ]] && echo developer-regression || echo enterprise-reference)
  [[ -n "$out" ]] || out="$repo_root/artifacts/bench/environment-$profile_name-$stamp.json"
  local bind="${OPPORTUNITY_BIND:-127.0.0.1}" db rabbit_user
  db="$(env_value OPPORTUNITY_DB)"; db="${db:-opportunity}"
  rabbit_user="$(env_value RABBITMQ_USER)"; rabbit_user="${rabbit_user:-opportunity}"
  # Credentials travel in the environment (env:VAR), never on the command line, and never into the manifest.
  export BENCH_PG_PRIMARY="Host=$bind;Port=${POSTGRES_PORT:-5432};Database=$db;Username=postgres;Password=$(env_value POSTGRES_PASSWORD)"
  export BENCH_PG_REPLICA="Host=$bind;Port=${REF_PG_REPLICA_PORT:-5433};Database=$db;Username=postgres;Password=$(env_value POSTGRES_PASSWORD)"
  export BENCH_RABBITMQ_MANAGEMENT="http://$rabbit_user:$(env_value RABBITMQ_PASSWORD)@$bind:${RABBITMQ_MANAGEMENT_PORT:-15672}/"

  local args=(capture-env --profile "$profile_name" --compose-project "$project" --out "$out"
    --postgres primary=env:BENCH_PG_PRIMARY --opensearch "http://$bind:${OPENSEARCH_PORT:-9200}"
    --rabbitmq-management env:BENCH_RABBITMQ_MANAGEMENT)
  [[ "$profile" == reference ]] && args+=(--postgres replica=env:BENCH_PG_REPLICA)
  dotnet run --project "$repo_root/tools/Opportunity.Benchmarks" -c Release -- "${args[@]}" "${extra[@]}"
}

case "$command" in
  up) cmd_up ;;
  down) cmd_down ;;
  capture) cmd_capture ;;
  ps) compose ps ;;
  logs) compose logs -f ;;
  config) compose config ;;
  help | -h | --help) sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//' ;;
  *) die "unknown command $command (up, capture, down, ps, logs, config)" ;;
esac
