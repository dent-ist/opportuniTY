#!/usr/bin/env bash
# Runs one opportuniTY k6 workload and records what ingest-k6 needs (E17-T04):
#   <out>/k6-raw.json.gz      every sample (k6 --out json), the source of the HDR histograms
#   <out>/k6-summary.json     k6 end-of-test summary
#   <out>/loadgen-cpu.jsonl   whole-host CPU of the load generator, 1 s samples (validity: < 70%)
#
# Usage: QUERIES=queries.json [BASE_URL=...] [SEARCH_RATE=...] tools/Opportunity.Benchmarks/k6/run.sh <script.js> <out-dir> [k6 args...]
#
# k6 binary: $K6_BIN if set; else `k6` on PATH when it is the pinned version (versions.env K6); else the pinned
# grafana/k6 image (K6@K6_DIGEST, host network so BASE_URL=http://127.0.0.1:... works). Then:
#   dotnet run --project tools/Opportunity.Benchmarks -- ingest-k6 --raw <out>/k6-raw.json.gz \
#     --summary <out>/k6-summary.json --loadgen-cpu <out>/loadgen-cpu.jsonl --queries "$QUERIES" ...
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
script="${1:?usage: run.sh <script.js> <out-dir> [k6 args...]}"
out="${2:?usage: run.sh <script.js> <out-dir> [k6 args...]}"
shift 2

: "${QUERIES:?QUERIES must point to the query file (opportunity-bench queries)}"
QUERIES="$(realpath "$QUERIES")"
export QUERIES
mkdir -p "$out"
out="$(cd "$out" && pwd)"
script_name="$(basename "$script")"
[[ -f "$here/$script_name" ]] || { echo "unknown workload script $script_name (expected one in $here)" >&2; exit 2; }

k6_version="$(grep -E '^K6=' "$root/versions.env" | cut -d= -f2-)"
k6_digest="$(grep -E '^K6_DIGEST=' "$root/versions.env" | cut -d= -f2-)"

sample_cpu() {
  local prev_total=0 prev_idle=0
  while true; do
    read -r _ user nice system idle iowait irq softirq steal _ < /proc/stat
    local idle_all=$((idle + iowait))
    local total=$((user + nice + system + idle + iowait + irq + softirq + steal))
    if ((prev_total > 0 && total > prev_total)); then
      local busy=$(((total - prev_total) - (idle_all - prev_idle)))
      printf '{"time":"%s","cpuPercent":%s}\n' "$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)" \
        "$(awk -v b="$busy" -v t="$((total - prev_total))" 'BEGIN { printf "%.1f", 100 * b / t }')"
    fi
    prev_total=$total
    prev_idle=$idle_all
    sleep 1
  done
}

sample_cpu > "$out/loadgen-cpu.jsonl" &
sampler=$!
trap 'kill "$sampler" 2>/dev/null || true' EXIT

passthrough=(BASE_URL API_BASE WORKSPACE_ID PATH_SEARCH PATH_DOCUMENT PATH_CODING PATH_BULK_JOBS PATH_JOB BENCH_TOKEN BENCH_TOKENS
  THINK_SCALE STRICT DURATION IDLE_DURATION BULK_DURATION SEARCH_RATE SEARCH_PRE_VUS SEARCH_MAX_VUS REVIEWERS
  BULK_DOCS_PER_SEC BULK_BATCH BULK_PRE_VUS BULK_MAX_VUS PHASE RUN_ID)

status=0
if [[ -n "${K6_BIN:-}" ]] || { command -v k6 > /dev/null && k6 version 2> /dev/null | grep -q "v${k6_version} "; }; then
  "${K6_BIN:-k6}" run --quiet --out "json=$out/k6-raw.json.gz" --summary-export "$out/k6-summary.json" "$@" "$here/$script_name" || status=$?
else
  env_args=(-e "QUERIES=/work/queries.json")
  for name in "${passthrough[@]}"; do
    [[ -n "${!name:-}" ]] && env_args+=(-e "$name=${!name}")
  done
  docker run --rm --network host -u "$(id -u):$(id -g)" \
    -v "$here:/work/k6:ro" -v "$QUERIES:/work/queries.json:ro" -v "$out:/work/out" \
    "grafana/k6:${k6_version}@${k6_digest}" run --quiet --out json=/work/out/k6-raw.json.gz \
    --summary-export /work/out/k6-summary.json "${env_args[@]}" "$@" "/work/k6/$script_name" || status=$?
fi

kill "$sampler" 2> /dev/null || true
echo "k6 exited with $status; outputs in $out" >&2
exit "$status"
