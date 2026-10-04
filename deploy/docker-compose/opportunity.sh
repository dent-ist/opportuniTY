#!/usr/bin/env bash
# Developer profile helper (E19-T03). Wraps `docker compose` with ../../versions.env (image versions, the single
# source of truth) and .env (local secrets). Run from anywhere; see README.md.
#
#   ./opportunity.sh [--images local|ghcr] <command> [args]
#     init        create .env from .env.example with random secrets (keeps an existing .env, adds new secrets)
#     preflight   check Docker/Compose versions, memory and vm.max_map_count
#     up          init + preflight + build + start, then wait until every service is healthy
#     down        stop and remove containers (volumes are kept)
#     reset       down and delete all volumes (database, index, queues, objects); asks unless -y
#     logs [svc]  follow logs
#     ps          service status
#     seed        create the demo workspace (idempotent)
#     compose ... any other docker compose command with the right files, e.g. `compose config`
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
versions_env="$repo_root/versions.env"
env_file="$here/.env"

images="${OPPORTUNITY_IMAGES:-local}"
if [[ "${1:-}" == "--images" ]]; then
  images="${2:?--images needs local or ghcr}"
  shift 2
fi

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
yellow() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
die() { red "error: $*"; exit 1; }

compose() {
  local files=(-f "$here/compose.yaml")
  case "$images" in
    local) ;;
    ghcr)
      local tag
      tag="${OPPORTUNITY_IMAGE_TAG:-$(env_value OPPORTUNITY_IMAGE_TAG)}"
      [[ -n "$tag" ]] || die "--images ghcr needs OPPORTUNITY_IMAGE_TAG (sha-<commit> or X.Y.Z), in the environment or .env"
      [[ "$tag" != latest && "$tag" != edge ]] || die "OPPORTUNITY_IMAGE_TAG=$tag is mutable; use sha-<commit> or X.Y.Z"
      files+=(-f "$here/compose.images.yaml")
      ;;
    *) die "--images must be local or ghcr (got '$images')" ;;
  esac
  # Local, git-ignored adjustments (build proxy/CA, extra ports), as plain `docker compose` would pick up.
  [[ -f "$here/compose.override.yaml" ]] && files+=(-f "$here/compose.override.yaml")
  [[ -f "$env_file" ]] || die "missing $env_file; run ./opportunity.sh init"
  docker compose --project-directory "$here" --env-file "$versions_env" --env-file "$env_file" "${files[@]}" "$@"
}

env_value() { [[ -f "$env_file" ]] && sed -n "s/^$1=//p" "$env_file" | tail -n 1 || true; }

random_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32 || true; }

cmd_init() {
  local line key
  if [[ -f "$env_file" ]]; then
    # Secrets added to .env.example after this .env was created (e.g. GRAFANA_ADMIN_PASSWORD) get generated values.
    local added=0
    while IFS= read -r line || [[ -n "$line" ]]; do
      key="${line%%=*}"
      if [[ "$line" =~ ^[A-Z0-9_]+(PASSWORD|SECRET_KEY)=$ ]] && ! grep -q "^$key=" "$env_file"; then
        printf '%s=%s\n' "$key" "$(random_secret)" >>"$env_file"
        added=1
      fi
    done <"$here/.env.example"
    ((added)) && echo "Added new secrets to $env_file." || echo ".env exists; keeping it."
    return
  fi
  umask 077
  while IFS= read -r line || [[ -n "$line" ]]; do
    key="${line%%=*}"
    if [[ "$line" =~ ^[A-Z0-9_]+(PASSWORD|SECRET_KEY)=$ ]]; then
      printf '%s=%s\n' "$key" "$(random_secret)"
    else
      printf '%s\n' "$line"
    fi
  done <"$here/.env.example" >"$env_file"
  echo "Created $env_file with random secrets (mode 600, git-ignored)."
}

version_ge() { [[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n 1)" == "$2" ]]; }

cmd_preflight() {
  local failed=0 engine compose_version mem_bytes mem_gib map_count min_mem_gib="${OPPORTUNITY_PREFLIGHT_MIN_MEM_GB:-6}"

  command -v docker >/dev/null || die "docker is not installed"
  engine="$(docker version --format '{{.Server.Version}}' 2>/dev/null)" || die "cannot reach the Docker daemon"
  compose_version="$(docker compose version --short 2>/dev/null | sed 's/^v//')" || die "Docker Compose v2 plugin missing"
  # Engine 25: healthcheck start_interval. Compose 2.24: several --env-file, !reset, inline configs.
  if version_ge "$engine" 25.0.0; then echo "ok    Docker Engine $engine"; else red "FAIL  Docker Engine $engine < 25.0"; failed=1; fi
  if version_ge "$compose_version" 2.24.0; then echo "ok    Docker Compose $compose_version"; else red "FAIL  Docker Compose $compose_version < 2.24"; failed=1; fi

  # Memory Docker can use (the VM's size on Docker Desktop/WSL2).
  mem_bytes="$(docker info --format '{{.MemTotal}}')"
  mem_gib=$((mem_bytes / 1024 / 1024 / 1024))
  if ((mem_bytes < min_mem_gib * 1024 * 1024 * 1024 - 256 * 1024 * 1024)); then
    red "FAIL  Docker has ${mem_gib} GiB of memory; the developer profile needs at least ${min_mem_gib} GiB (8 GiB minimum, 16 GiB recommended)"
    failed=1
  elif ((mem_gib < 15)); then
    yellow "warn  Docker has ${mem_gib} GiB of memory; 16 GiB is recommended (Q-39). Consider OPENSEARCH_HEAP=1g."
  else
    echo "ok    Docker memory ${mem_gib} GiB"
  fi

  # Free memory right now, on a Linux host (Docker Desktop reports its VM above). Skipped while the stack runs.
  if [[ -r /proc/meminfo ]] && [[ -z "$(compose ps -q 2>/dev/null)" ]]; then
    local avail_kib
    avail_kib="$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo)"
    if ((avail_kib < min_mem_gib * 1024 * 1024)); then
      red "FAIL  only $((avail_kib / 1024)) MiB of memory is available; need ${min_mem_gib} GiB free to start the stack"
      failed=1
    else
      echo "ok    available memory $((avail_kib / 1024 / 1024)) GiB"
    fi
  fi

  # Kernel setting of the Docker host (the VM on Docker Desktop/WSL2), read from inside a container.
  local probe_image
  probe_image="postgres:$(sed -n 's/^POSTGRES=//p' "$versions_env")@$(sed -n 's/^POSTGRES_DIGEST=//p' "$versions_env")"
  map_count="$(docker run --rm --pull missing --network none --entrypoint cat "$probe_image" /proc/sys/vm/max_map_count 2>/dev/null || echo 0)"
  if ((map_count >= 262144)); then
    echo "ok    vm.max_map_count $map_count"
  else
    red "FAIL  vm.max_map_count is $map_count; OpenSearch needs >= 262144. Fix (see README, Troubleshooting):"
    red "        Linux:  sudo sysctl -w vm.max_map_count=262144   (persist in /etc/sysctl.d/99-opensearch.conf)"
    red "        WSL2:   wsl -d docker-desktop sysctl -w vm.max_map_count=262144, or kernelCommandLine in .wslconfig"
    failed=1
  fi

  if ((failed)); then
    [[ "${OPPORTUNITY_SKIP_PREFLIGHT:-0}" == 1 ]] && { yellow "preflight failed; continuing because OPPORTUNITY_SKIP_PREFLIGHT=1"; return 0; }
    die "preflight failed (set OPPORTUNITY_SKIP_PREFLIGHT=1 to ignore at your own risk)"
  fi
}

lite_warning() {
  yellow "------------------------------------------------------------------------------------------"
  yellow " opportuniTY developer profile (Lite): EVALUATION AND DEVELOPMENT ONLY."
  yellow " Do not load real client data. No TLS, OpenSearch security plugin disabled, no malware"
  yellow " scanning, no sandboxed renderers. Full is the only production profile (decision Q-01)."
  yellow "------------------------------------------------------------------------------------------"
}

cmd_up() {
  cmd_init
  cmd_preflight
  lite_warning
  local build=(--build)
  [[ "$images" == ghcr ]] && build=(--no-build --pull missing)
  # Group roles added by newer migrations must exist before the migrator runs (it cannot create roles).
  compose up -d --wait --wait-timeout "${OPPORTUNITY_WAIT_TIMEOUT:-600}" postgres
  compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U postgres -d postgres \
    -f /docker-entrypoint-initdb.d/20-group-roles.sql
  compose up -d "${build[@]}" --wait --wait-timeout "${OPPORTUNITY_WAIT_TIMEOUT:-600}" "$@"
  compose ps
  local web api
  web="$(env_value WEB_PORT)"; api="$(env_value API_PORT)"
  echo
  echo "Web:  http://localhost:${web:-8080}/  (sign in here; use localhost, not 127.0.0.1)"
  echo "API:  http://127.0.0.1:${api:-8081}/health/ready"
  echo "RabbitMQ management: http://127.0.0.1:$(env_value RABBITMQ_MANAGEMENT_PORT | grep . || echo 15672)/"
  if [[ ",${COMPOSE_PROFILES:-$(env_value COMPOSE_PROFILES)}," == *,observability,* ]]; then
    echo "Grafana:    http://127.0.0.1:$(env_value GRAFANA_PORT | grep . || echo 3000)/ (anonymous viewer; admin password GRAFANA_ADMIN_PASSWORD)"
    echo "Prometheus: http://127.0.0.1:$(env_value PROMETHEUS_PORT | grep . || echo 9090)/"
    echo "Jaeger:     http://127.0.0.1:$(env_value JAEGER_UI_PORT | grep . || echo 16686)/"
    [[ -n "${OTEL_EXPORTER_OTLP_ENDPOINT:-$(env_value OTEL_EXPORTER_OTLP_ENDPOINT)}" ]] \
      || yellow "OTEL_EXPORTER_OTLP_ENDPOINT is not set: api/worker export no telemetry (set it to http://otel-collector:4317)."
  fi
}

cmd_reset() {
  if [[ "${1:-}" != "-y" ]]; then
    read -r -p "Delete ALL developer-profile data (database, index, queues, objects)? [y/N] " answer
    [[ "$answer" == [yY] ]] || { echo "Aborted."; return 1; }
  fi
  compose --profile '*' down --volumes --remove-orphans
}

cmd_seed() {
  compose exec -T postgres sh -c 'psql -v ON_ERROR_STOP=1 -U "$OPPORTUNITY_DB_OWNER_USER" -d "$OPPORTUNITY_DB"' \
    <"$here/seed/demo-workspace.sql"
}

command="${1:-}"
[[ $# -gt 0 ]] && shift
[[ -f "$versions_env" ]] || die "missing $versions_env"
# After a pull, .env.example may list new secrets (e.g. KEYCLOAK_ADMIN_PASSWORD); add them before compose needs them.
if [[ -f "$env_file" && "$command" != init && "$command" != help && -n "$command" ]]; then
  cmd_init | grep -v '^.env exists' || true
fi
case "$command" in
  init) cmd_init ;;
  preflight) cmd_preflight ;;
  up) cmd_up "$@" ;;
  down) compose --profile '*' down --remove-orphans "$@" ;;
  reset) cmd_reset "$@" ;;
  logs) compose logs -f --tail 200 "$@" ;;
  ps) compose ps "$@" ;;
  seed) cmd_seed ;;
  compose) compose "$@" ;;
  *) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; [[ -z "$command" || "$command" == help ]] || exit 2 ;;
esac
