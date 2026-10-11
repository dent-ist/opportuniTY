#!/usr/bin/env bash
# Compose profile helper (E19-T03, E19-T06). Wraps `docker compose` with ../../versions.env (image versions, the single
# source of truth) and the profile's settings. Run from anywhere; see README.md and docs/operations/deployment-profiles.md.
#
#   ./opportunity.sh [--images local|ghcr] [--profile lite|full] <command> [args]
#     --profile lite (default) is the evaluation/development profile (compose.yaml, .env, tls/); --profile full is the
#     production topology (compose.full.yaml, full/.env, full/secrets/, full/tls/). OPPORTUNITY_PROFILE does the same.
#     init        Lite: create .env with random secrets (keeps an existing .env, adds new secrets) and the local TLS CA
#                 and localhost certificate. Full: full/.env, every secret as a file, the internal CA and certificates
#     preflight   check Docker/Compose versions, memory and vm.max_map_count
#     up [args]   init + preflight + build + start, then wait until every service is healthy; args go to `compose up`
#                 (e.g. `--profile full up --scale worker-index=4`)
#     verify      health of the stores: OpenSearch cluster status and nodes, PostgreSQL replication and lag, RabbitMQ
#                 cluster members and the consumers of every queue (Full; Lite shows its single nodes)
#     build       build the application images locally (opportunity-local/opportunity-<name>:dev)
#     down        stop and remove containers (volumes are kept)
#     reset       down and delete all volumes (database, index, queues, objects); asks unless -y
#     logs [svc]  follow logs
#     ps          service status
#     seed        Lite: create the demo workspace with demo coding fields and layout (idempotent)
#     demo-documents [file.dat]
#                 Lite: import synthetic demo documents into the demo workspace through the real import API, signed in
#                 as admin.dev (default: seed/demo-volume, 10 readable documents with extracted text, copied into the
#                 import share; a file.dat is imported as metadata only; run after `up` and `seed`)
#     compose ... any other docker compose command with the right files, e.g. `compose config`
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
versions_env="$repo_root/versions.env"

images="${OPPORTUNITY_IMAGES:-local}"
profile="${OPPORTUNITY_PROFILE:-lite}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --images) images="${2:?--images needs local or ghcr}"; shift 2 ;;
    --profile) profile="${2:?--profile needs lite or full}"; shift 2 ;;
    *) break ;;
  esac
done

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
yellow() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
die() { red "error: $*"; exit 1; }

case "$profile" in
  lite)
    env_file="$here/.env"
    tls_dir="$here/tls"
    ;;
  full)
    full_dir="$here/full"
    env_file="$full_dir/.env"
    secrets_dir="$full_dir/secrets"
    tls_dir="$full_dir/tls"
    ;;
  *) die "--profile must be lite or full (got '$profile')" ;;
esac

compose() {
  local files
  # Create the import share as the current user first: a bind-mount source Docker has to create is owned by root.
  mkdir -p "$(import_share)" 2>/dev/null || true
  if [[ "$profile" == full ]]; then
    files=(-f "$here/compose.full.yaml")
    # Every Full service carries the `full` profile; keep any extra profile (observability) set in full/.env.
    local profiles="${COMPOSE_PROFILES:-$(env_value COMPOSE_PROFILES)}"
    [[ ",$profiles," == *,full,* ]] || profiles="full${profiles:+,$profiles}"
    export COMPOSE_PROFILES="$profiles"
  else
    files=(-f "$here/compose.yaml")
  fi
  case "$images" in
    local) ;;
    ghcr)
      local tag
      tag="${OPPORTUNITY_IMAGE_TAG:-$(env_value OPPORTUNITY_IMAGE_TAG)}"
      [[ -n "$tag" ]] || die "--images ghcr needs OPPORTUNITY_IMAGE_TAG (sha-<commit> or X.Y.Z), in the environment or $env_file"
      [[ "$tag" != latest && "$tag" != edge ]] || die "OPPORTUNITY_IMAGE_TAG=$tag is mutable; use sha-<commit> or X.Y.Z"
      if [[ "$profile" == full ]]; then
        # compose.full.yaml has no build sections; point it at the published images.
        export OPPORTUNITY_IMAGE_PREFIX="${OPPORTUNITY_IMAGE_PREFIX:-$(env_value OPPORTUNITY_IMAGE_PREFIX | grep . || echo ghcr.io/plogramer/)}"
        export OPPORTUNITY_IMAGE_TAG="$tag"
      else
        files+=(-f "$here/compose.images.yaml")
      fi
      ;;
    *) die "--images must be local or ghcr (got '$images')" ;;
  esac
  # Local, git-ignored adjustments (build proxy/CA, extra ports), as plain `docker compose` would pick up.
  if [[ "$profile" == full ]]; then
    [[ -f "$here/compose.full.override.yaml" ]] && files+=(-f "$here/compose.full.override.yaml")
  else
    [[ -f "$here/compose.override.yaml" ]] && files+=(-f "$here/compose.override.yaml")
  fi
  [[ -f "$env_file" ]] || die "missing $env_file; run ./opportunity.sh --profile $profile init"
  docker compose --project-directory "$here" --env-file "$versions_env" --env-file "$env_file" "${files[@]}" "$@"
}

env_value() { [[ -f "$env_file" ]] && sed -n "s/^$1=//p" "$env_file" | tail -n 1 || true; }

# 32 characters; a *_HASH_SECRET_KEY is a base64 key of at least 32 bytes, so it gets 44 (alphanumerics are base64 digits).
random_secret() {
  local length=32
  [[ "${1:-}" == *_HASH_SECRET_KEY ]] && length=44
  [[ "${1:-}" == 44 ]] && length=44
  LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "$length" || true
}

probe_image() {
  printf 'postgres:%s@%s' "$(sed -n 's/^POSTGRES=//p' "$versions_env")" "$(sed -n 's/^POSTGRES_DIGEST=//p' "$versions_env")"
}

# --- TLS (E19-T06): an internal CA per profile and certificates signed by it -------------------------------------
# openssl from the host, or from the pinned PostgreSQL image when the host has none (the TLS folder is mounted at the
# same path, so arguments stay valid).
openssl_run() {
  if command -v openssl >/dev/null; then
    openssl "$@"
  else
    docker run --rm --network none --user "$(id -u):$(id -g)" -v "$tls_dir:$tls_dir" -w "$tls_dir" \
      --entrypoint openssl "$(probe_image)" "$@"
  fi
}

# make_ca OU: the profile's CA (tls/ca.crt, ca.key); kept once created.
make_ca() {
  mkdir -p "$tls_dir" && chmod 0700 "$tls_dir"
  # No config defaults (some add CA extensions to every certificate): only the extensions given here.
  printf '[req]\ndistinguished_name = dn\nprompt = no\n[dn]\n' >"$tls_dir/openssl.cnf"
  [[ -s "$tls_dir/ca.crt" && -s "$tls_dir/ca.key" ]] && return 0
  openssl_run req -config "$tls_dir/openssl.cnf" -x509 -newkey rsa:3072 -nodes -days 3650 \
    -keyout "$tls_dir/ca.key" -out "$tls_dir/ca.crt" -subj "/O=opportuniTY/OU=$1/CN=opportuniTY $1 internal CA" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  chmod 0600 "$tls_dir/ca.key"
  chmod 0644 "$tls_dir/ca.crt"
  echo "Created the $1 internal CA $tls_dir/ca.crt (trust it in your browser to avoid certificate warnings)."
}

# make_cert OU NAME CN SAN [DIR]: NAME.crt/NAME.key signed by the CA, for server and client authentication; kept once
# created. Keys are 0644 inside the 0700 TLS folder: containers run as other users and read them through bind mounts
# (PostgreSQL and RabbitMQ copy theirs to 0600 at start).
make_cert() {
  local ou="$1" name="$2" cn="$3" san="$4" dir="${5:-$tls_dir}"
  [[ -s "$dir/$name.crt" && -s "$dir/$name.key" ]] && return 0
  mkdir -p "$dir"
  openssl_run req -config "$tls_dir/openssl.cnf" -x509 -CA "$tls_dir/ca.crt" -CAkey "$tls_dir/ca.key" -newkey rsa:2048 \
    -nodes -days 825 -keyout "$dir/$name.key" -out "$dir/$name.crt" -subj "/O=opportuniTY/OU=$ou/CN=$cn" \
    -addext "subjectAltName=$san" -addext "basicConstraints=critical,CA:FALSE" \
    -addext "keyUsage=critical,digitalSignature,keyEncipherment" -addext "extendedKeyUsage=serverAuth,clientAuth" 2>/dev/null
  chmod 0644 "$dir/$name.key" "$dir/$name.crt"
}

# The nginx include that turns on the TLS listener of the web container (port 8443).
write_web_tls_conf() {
  cat >"$1/tls.conf" <<'NGINX'
# Generated by ./opportunity.sh init (E19-T06): TLS listener of the web container. Replace server.crt/server.key with
# your own certificate if you have one.
listen 8443 ssl;
ssl_certificate /etc/nginx/tls/server.crt;
ssl_certificate_key /etc/nginx/tls/server.key;
ssl_protocols TLSv1.2 TLSv1.3;
ssl_session_cache shared:opportunity_tls:1m;
ssl_session_timeout 1h;
NGINX
}

lite_tls() {
  make_ca Lite
  make_cert Lite server localhost "DNS:localhost,IP:127.0.0.1" "$tls_dir/web"
  [[ -f "$tls_dir/web/tls.conf" ]] || write_web_tls_conf "$tls_dir/web"
  chmod 0755 "$tls_dir/web"
}

cmd_init() {
  [[ "$profile" == full ]] && { full_init; return; }
  lite_tls
  local line key
  if [[ -f "$env_file" ]]; then
    # Secrets added to .env.example after this .env was created (e.g. GRAFANA_ADMIN_PASSWORD) get generated values.
    local added=0
    while IFS= read -r line || [[ -n "$line" ]]; do
      key="${line%%=*}"
      if [[ "$line" =~ ^[A-Z0-9_]+(PASSWORD|SECRET_KEY)=$ ]] && ! grep -q "^$key=" "$env_file"; then
        printf '%s=%s\n' "$key" "$(random_secret "$key")" >>"$env_file"
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
      printf '%s=%s\n' "$key" "$(random_secret "$key")"
    else
      printf '%s\n' "$line"
    fi
  done <"$here/.env.example" >"$env_file"
  echo "Created $env_file with random secrets (mode 600, git-ignored)."
}

# --- Full profile: secrets as files, certificates -----------------------------------------------------------------
worker_types=(dispatcher import index render export production bulkcoding)

# full_secret NAME [44]: a random secret file, kept once created. Files are 0644 inside the 0700 secrets folder because
# Compose bind-mounts them into containers that run as other users.
full_secret() {
  [[ -s "$secrets_dir/$1" ]] && return 0
  random_secret "${2:-}" >"$secrets_dir/$1"
  chmod 0644 "$secrets_dir/$1"
}

# derived NAME CONTENT: a file computed from other secrets (password file for a login, AMQP URI); rewritten every time.
derived() {
  printf '%s\n' "$2" >"$secrets_dir/$1"
  chmod 0644 "$secrets_dir/$1"
}

full_init() {
  local created=0 name host type
  if [[ ! -f "$env_file" ]]; then
    cp "$full_dir/.env.example" "$env_file"
    chmod 0600 "$env_file"
    created=1
  fi
  mkdir -p "$secrets_dir" && chmod 0700 "$secrets_dir"

  # PostgreSQL: superuser, migrator owner, one login per component and worker type, replication, filer cluster.
  for name in postgres-password db-owner-password db-api-password db-monitor-password db-sealer-password \
      db-replication-password filer-postgres-password seaweedfs-db-password; do
    full_secret "$name"
  done
  for type in "${worker_types[@]}"; do full_secret "db-worker-$type-password"; done
  # Npgsql password files (Passfile=): the connection strings in compose.full.yaml carry no password.
  derived pgpass-owner "*:*:*:opportunity_owner:$(cat "$secrets_dir/db-owner-password")"
  derived pgpass-api "*:*:*:opportunity_api:$(cat "$secrets_dir/db-api-password")"
  derived pgpass-sealer "*:*:*:opportunity_sealer:$(cat "$secrets_dir/db-sealer-password")"
  for type in "${worker_types[@]}"; do
    derived "pgpass-worker-$type" "*:*:*:opportunity_worker_$type:$(cat "$secrets_dir/db-worker-$type-password")"
  done

  # RabbitMQ: the cluster's Erlang cookie, the operator's administrator and one user per component; AMQPS URIs.
  full_secret rabbitmq-erlang-cookie 44
  full_secret rabbitmq-admin-password
  derived amqp-admin "amqps://opportunity:$(cat "$secrets_dir/rabbitmq-admin-password")@rabbitmq:5671/"
  for type in "${worker_types[@]}"; do
    full_secret "rabbitmq-$type-password"
    derived "amqp-$type" "amqps://opportunity-$type:$(cat "$secrets_dir/rabbitmq-$type-password")@rabbitmq:5671/"
  done

  # OpenSearch internal users (full/opensearch/roles.yml), S3 identities (full/seaweedfs/run.sh), application keys.
  for name in admin api indexer dispatcher migrator monitor; do full_secret "opensearch-$name-password"; done
  for name in migrator api import index render export production; do full_secret "s3-$name-secret-key"; done
  full_secret envelope-hmac-k1 44
  full_secret audit-session-hash-key 44
  full_secret grafana-admin-password
  # Supplied by the operator (the IdP's client registration); `up` refuses to start while it is empty.
  [[ -f "$secrets_dir/oidc-client-secret" ]] || { : >"$secrets_dir/oidc-client-secret"; chmod 0644 "$secrets_dir/oidc-client-secret"; }

  # Internal CA and certificates (ADR-015 D14.2). Node certificates also name the data-lb alias clients connect to.
  make_ca Full
  make_cert Full postgres postgres "DNS:postgres"
  make_cert Full postgres-replica postgres-replica "DNS:postgres-replica"
  make_cert Full filer-postgres filer-postgres "DNS:filer-postgres"
  for host in opensearch-1 opensearch-2 opensearch-3; do
    make_cert Full "$host" "$host" "DNS:$host,DNS:opensearch"
  done
  make_cert Full opensearch-admin opensearch-admin "DNS:opensearch-admin"
  for host in rabbitmq-1 rabbitmq-2 rabbitmq-3; do
    make_cert Full "$host" "$host" "DNS:$host,DNS:rabbitmq"
  done
  make_cert Full seaweedfs seaweedfs "DNS:seaweedfs-master-1,DNS:seaweedfs-master-2,DNS:seaweedfs-master-3,DNS:seaweedfs-volume-1,DNS:seaweedfs-volume-2,DNS:seaweedfs-filer,DNS:seaweedfs-s3,DNS:s3,DNS:localhost,IP:127.0.0.1"
  make_cert Full api api "DNS:api"
  host="$(env_value OPPORTUNITY_HOSTNAME | grep . || echo opportunity.internal)"
  make_cert Full server "$host" "DNS:$host" "$tls_dir/web"
  cp "$tls_dir/ca.crt" "$tls_dir/web/ca.crt"
  [[ -f "$tls_dir/web/tls.conf" ]] || write_web_tls_conf "$tls_dir/web"
  chmod 0755 "$tls_dir/web"

  ((created)) && echo "Created $env_file: set OPPORTUNITY_HOSTNAME, OPPORTUNITY_PUBLIC_ORIGIN and OIDC_AUTHORITY there."
  echo "Full secrets in $secrets_dir (one file each), certificates in $tls_dir."
  [[ -s "$secrets_dir/oidc-client-secret" ]] || yellow "Put the IdP client secret into $secrets_dir/oidc-client-secret before \`up\`."
}

version_ge() { [[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n 1)" == "$2" ]]; }

cmd_preflight() {
  local failed=0 engine compose_version mem_bytes mem_gib map_count
  local default_min=6 recommended=16
  [[ "$profile" == full ]] && { default_min=16; recommended=32; }
  local min_mem_gib="${OPPORTUNITY_PREFLIGHT_MIN_MEM_GB:-$default_min}"

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
    red "FAIL  Docker has ${mem_gib} GiB of memory; the $profile profile needs at least ${min_mem_gib} GiB (${recommended} GiB recommended)"
    failed=1
  elif ((mem_gib < recommended - 1)); then
    yellow "warn  Docker has ${mem_gib} GiB of memory; ${recommended} GiB is recommended for the $profile profile (Q-39). Consider a smaller OPENSEARCH_HEAP."
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
  map_count="$(docker run --rm --pull missing --network none --entrypoint cat "$(probe_image)" /proc/sys/vm/max_map_count 2>/dev/null || echo 0)"
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
  yellow " Do not load real client data. Single node of everything, secrets in .env, OpenSearch"
  yellow " security plugin disabled, TLS only between your browser and the web container."
  yellow " Full is the only production profile (decision Q-01): --profile full."
  yellow "------------------------------------------------------------------------------------------"
}

# The four application images, built locally (the Full file has no build sections).
cmd_build() {
  local target
  for target in migrator api worker; do
    docker build -f "$repo_root/deploy/docker/dotnet.Dockerfile" --target "$target" \
      -t "opportunity-local/opportunity-$target:${OPPORTUNITY_IMAGE_TAG:-dev}" "$repo_root"
  done
  docker build -f "$repo_root/deploy/docker/web.Dockerfile" --target web \
    --build-arg "APK_UPGRADE=${WEB_APK_UPGRADE:-1}" -t "opportunity-local/opportunity-web:${OPPORTUNITY_IMAGE_TAG:-dev}" "$repo_root"
}

cmd_up() {
  cmd_init
  cmd_preflight
  [[ "$profile" == full ]] && { full_up "$@"; return; }
  lite_warning
  local build=(--build)
  [[ "$images" == ghcr ]] && build=(--no-build --pull missing)
  # Group roles added by newer migrations must exist before the migrator runs (it cannot create roles); the
  # per-component database logins and broker users (E05-T07) are created or updated before anything connects.
  compose up -d --wait --wait-timeout "${OPPORTUNITY_WAIT_TIMEOUT:-600}" postgres rabbitmq
  compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U postgres -d postgres \
    -f /docker-entrypoint-initdb.d/20-group-roles.sql
  compose exec -T postgres sh /docker-entrypoint-initdb.d/30-component-logins.sh
  compose exec -T rabbitmq sh /etc/opportunity/rabbitmq-users.sh
  # The audit sealer login (E14-T03): created on first use, password kept in step with .env.
  compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U postgres -d postgres \
    -v login="$(env_value OPPORTUNITY_DB_AUDIT_SEALER_USER | grep . || echo opportunity_sealer)" \
    -v password="$(env_value OPPORTUNITY_DB_AUDIT_SEALER_PASSWORD)" -f - <"$here/postgres/audit-sealer-login.sql"
  compose up -d "${build[@]}" --wait --wait-timeout "${OPPORTUNITY_WAIT_TIMEOUT:-600}" "$@"
  compose ps
  local web api tls
  web="$(env_value WEB_PORT)"; api="$(env_value API_PORT)"; tls="$(env_value WEB_TLS_PORT)"
  echo
  echo "Web:  http://localhost:${web:-8080}/  (sign in here; use localhost, not 127.0.0.1)"
  echo "      https://localhost:${tls:-8443}/ (local CA tls/ca.crt; set OPPORTUNITY_PUBLIC_ORIGIN=https://localhost:${tls:-8443} to sign in over HTTPS)"
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

full_up() {
  local wait=(--wait --wait-timeout "${OPPORTUNITY_WAIT_TIMEOUT:-900}")
  [[ -s "$secrets_dir/oidc-client-secret" ]] || die "put the IdP client secret into $secrets_dir/oidc-client-secret first"
  for name in OPPORTUNITY_HOSTNAME OPPORTUNITY_PUBLIC_ORIGIN OIDC_AUTHORITY; do
    [[ -n "$(env_value "$name")" ]] || die "set $name in $env_file"
  done
  if [[ "$images" == local ]]; then
    cmd_build
  fi
  # As in Lite, logins and broker users are created or re-keyed from the secret files before anything connects; the
  # database superuser authenticates by peer on the local socket only (full/postgres/pg_hba.conf).
  compose up -d "${wait[@]}" postgres rabbitmq-1 rabbitmq-2 rabbitmq-3
  compose exec -T -u postgres postgres psql -q -v ON_ERROR_STOP=1 -d postgres \
    -f /docker-entrypoint-initdb.d/20-group-roles.sql
  compose exec -T -u postgres postgres sh /docker-entrypoint-initdb.d/30-component-logins.sh
  compose exec -T -u postgres postgres sh -c \
    'psql -q -v ON_ERROR_STOP=1 -d postgres -v login=opportunity_sealer -v password="$(cat /run/secrets/db-sealer-password)" -f -' \
    <"$here/postgres/audit-sealer-login.sql"
  compose exec -T rabbitmq-1 sh /etc/opportunity/rabbitmq-users.sh
  compose up -d --no-build "${wait[@]}" "$@"
  compose ps
  echo
  echo "Web: $(env_value OPPORTUNITY_PUBLIC_ORIGIN)/  (certificate from $tls_dir/ca.crt unless you replaced full/tls/web/server.*)"
  echo "Check the stores: ./opportunity.sh --profile full verify"
}

# Health of the stores, for the E19-T06 checks: OpenSearch green with three nodes, PostgreSQL replication streaming
# with its lag, RabbitMQ cluster members and how many consumers each queue has (one per scaled worker replica).
cmd_verify() {
  local failed=0 health
  if [[ "$profile" == full ]]; then
    echo "== OpenSearch"
    health="$(compose exec -T opensearch-1 sh -c 'printf "user = \"opportunity-monitor:%s\"\n" "$(cat /run/secrets/opensearch-monitor-password)" | curl -fsS --cacert config/certs/ca.crt --config - "https://opensearch-1:9200/_cluster/health"')" || health='{}'
    printf '%s\n' "$health" | tr ',' '\n' | grep -E '"(status|number_of_nodes|unassigned_shards|active_shards_percent_as_number)"' || true
    [[ "$health" == *'"status":"green"'* && "$health" == *'"number_of_nodes":3'* ]] || { red "FAIL  OpenSearch is not green with 3 nodes"; failed=1; }
    echo "== PostgreSQL replication (primary: standby, state, replay lag in s)"
    compose exec -T -u postgres postgres psql -d postgres -tA -F ' ' -c \
      "SELECT application_name, state, sync_state, COALESCE(EXTRACT(EPOCH FROM replay_lag), 0) FROM pg_stat_replication" \
      | grep . || { red "FAIL  no standby is streaming"; failed=1; }
    echo "== PostgreSQL replica (in recovery, seconds since the last replayed transaction)"
    compose exec -T -u postgres postgres-replica psql -d postgres -tA -F ' ' -c \
      "SELECT pg_is_in_recovery(), COALESCE(EXTRACT(EPOCH FROM now() - pg_last_xact_replay_timestamp()), 0)" || failed=1
    echo "== RabbitMQ cluster (running nodes)"
    local nodes
    nodes="$(compose exec -T rabbitmq-1 rabbitmq-diagnostics -q cluster_status \
      | awk '/^Running Nodes/ { f = 1; next } /^Versions/ { f = 0 } f && /rabbit@/')" || true
    printf '%s\n' "$nodes"
    [[ "$(printf '%s\n' "$nodes" | grep -c rabbit@)" == 3 ]] || { red "FAIL  RabbitMQ does not run 3 nodes"; failed=1; }
    echo "== Queues (name, type, consumers, ready, unacknowledged)"
    compose exec -T rabbitmq-1 rabbitmqctl -q list_queues name type consumers messages_ready messages_unacknowledged \
      | grep -Ev '\.(dlq|parking)|retry' || true
  else
    echo "== OpenSearch"
    compose exec -T opensearch curl -fsS 'http://127.0.0.1:9200/_cluster/health' | tr ',' '\n' | grep -E '"(status|number_of_nodes)"' || failed=1
    echo "== Queues (name, type, consumers, ready, unacknowledged)"
    compose exec -T rabbitmq rabbitmqctl -q list_queues name type consumers messages_ready messages_unacknowledged \
      | grep -Ev '\.(dlq|parking)|retry' || failed=1
  fi
  ((failed == 0)) || die "verify failed"
  echo "ok"
}

cmd_reset() {
  if [[ "${1:-}" != "-y" ]]; then
    read -r -p "Delete ALL $profile-profile data (database, index, queues, objects)? [y/N] " answer
    [[ "$answer" == [yY] ]] || { echo "Aborted."; return 1; }
  fi
  compose --profile '*' down --volumes --remove-orphans
}

lite_only() { [[ "$profile" == lite ]] || die "$1 is for the Lite developer profile only (no demo data in Full)"; }

cmd_seed() {
  lite_only seed
  local sql
  for sql in demo-workspace.sql demo-coding.sql; do
    compose exec -T postgres sh -c 'psql -v ON_ERROR_STOP=1 -U "$OPPORTUNITY_DB_OWNER_USER" -d "$OPPORTUNITY_DB"' \
      <"$here/seed/$sql"
  done
}

# Signs in through the BFF and Keycloak like a browser (the demo user's password is the realm's dev default), then
# uploads the DAT to POST /api/v1/workspaces/{demo}/imports with the anti-forgery header. Developer profile only.
# The import share the worker reads volumes from (OPPORTUNITY_IMPORT_SHARE in .env, relative to this folder).
import_share() {
  local share
  share="$(sed -n 's/^OPPORTUNITY_IMPORT_SHARE=//p' "$env_file" 2>/dev/null | tail -n 1)"
  share="${OPPORTUNITY_IMPORT_SHARE:-${share:-./import-share}}"
  [[ "$share" == /* ]] || share="$here/$share"
  printf '%s' "$share"
}

cmd_demo_documents() {
  lite_only demo-documents
  command -v curl >/dev/null || die "curl is required"
  local dat request='{"name":"Demo documents"}'
  if [[ $# -gt 0 ]]; then
    dat="$1"
  else
    # The demo volume (load file + extracted text) goes into the import share; the import reads the text from there.
    local share
    share="$(import_share)"
    if [[ -d "$share" && ! -w "$share" ]]; then
      die "the import share $share is not writable by $(id -un) (Docker created it as root); run: sudo chown -R \"\$USER\": \"$share\""
    fi
    mkdir -p "$share" && rm -rf "$share/demo-volume" && cp -R "$here/seed/demo-volume" "$share/demo-volume" ||
      die "cannot copy the demo volume into the import share $share"
    dat="$share/demo-volume/DATA/DEMO001.dat"
    request='{"name":"Demo documents","profile":{"paths":{"volumeRoot":"demo-volume"}}}'
  fi
  [[ -f "$dat" ]] || die "no such load file: $dat"
  local origin="${OPPORTUNITY_PUBLIC_ORIGIN:-http://localhost:8080}"
  local user="${DEMO_USER:-admin.dev}" password="${DEMO_PASSWORD:-opportunity}"
  local workspace="00000000-0000-4000-8000-00000000d3e0"
  local jar page action xsrf response
  jar="$(mktemp)"
  trap 'rm -f "$jar"' RETURN

  # 1. /bff/login redirects to the Keycloak login form; 2. post the credentials; curl follows the callback home.
  page="$(curl -fsSL -c "$jar" -b "$jar" "$origin/bff/login?returnUrl=/")" || die "cannot reach $origin (run ./opportunity.sh up)"
  action="$(printf '%s' "$page" | grep -o 'id="kc-form-login"[^>]*action="[^"]*"' | sed 's/.*action="//; s/"$//; s/&amp;/\&/g')"
  [[ -n "$action" ]] || die "the Keycloak login form was not found; is the demo realm loaded?"
  curl -fsSL -c "$jar" -b "$jar" -o /dev/null --data-urlencode "username=$user" --data-urlencode "password=$password" \
    "$action" || die "sign-in as $user failed"

  # 3. The anti-forgery cookie comes with the first API call.
  curl -fsS -c "$jar" -b "$jar" -o /dev/null "$origin/api/v1/me" || die "sign-in as $user did not create a session"
  xsrf="$(awk '$6 ~ /opp-xsrf$/ { print $7 }' "$jar" | tail -n 1)"
  [[ -n "$xsrf" ]] || die "no anti-forgery token after sign-in"

  # 4. Append import with the default profile: known columns are auto-mapped, the rest are ignored.
  response="$(curl -sS -c "$jar" -b "$jar" -w '\n%{http_code}' -H "Origin: $origin" -H "X-XSRF-TOKEN: $xsrf" \
    -H "Idempotency-Key: demo-documents-$(date +%s)-$$" -F "file=@$dat;type=application/octet-stream" \
    -F "request=$request;type=application/json" \
    "$origin/api/v1/workspaces/$workspace/imports")"
  local status="${response##*$'\n'}"
  [[ "$status" == 202 ]] || die "import was not accepted (HTTP $status): ${response%$'\n'*}"
  echo "Import started. The documents appear in Documents within a minute: $origin/w/$workspace/documents"
}

command="${1:-}"
[[ $# -gt 0 ]] && shift
[[ -f "$versions_env" ]] || die "missing $versions_env"
# After a pull, .env.example may list new secrets (e.g. KEYCLOAK_ADMIN_PASSWORD); add them before compose needs them.
if [[ "$profile" == lite && -f "$env_file" && "$command" != init && "$command" != help && -n "$command" ]]; then
  cmd_init | grep -v '^.env exists' || true
fi
case "$command" in
  init) cmd_init ;;
  preflight) cmd_preflight ;;
  up) cmd_up "$@" ;;
  verify) cmd_verify ;;
  build) cmd_build ;;
  down) compose --profile '*' down --remove-orphans "$@" ;;
  reset) cmd_reset "$@" ;;
  logs) compose logs -f --tail 200 "$@" ;;
  ps) compose ps "$@" ;;
  seed) cmd_seed ;;
  demo-documents) cmd_demo_documents "$@" ;;
  compose) compose "$@" ;;
  *) sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; [[ -z "$command" || "$command" == help ]] || exit 2 ;;
esac
