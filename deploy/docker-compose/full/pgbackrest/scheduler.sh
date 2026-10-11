#!/bin/bash
# Full profile backup scheduler (E19-T08, ADR-016 §2.2): runs as the `pgbackrest` service next to PostgreSQL, with the
# data directory read-only and the server socket shared. Every 5 minutes it decides by the age of the newest backups in
# the repository, so a missed window (restart, outage) is made up at once and nothing depends on a clock daemon:
#   full          when the newest full backup is older than BACKUP_FULL_EVERY (7 days)
#   differential  when the newest full or differential is older than BACKUP_DIFF_EVERY (1 day)
#   incremental   when the newest backup of any type is older than BACKUP_INCR_EVERY (6 hours)
#   verification  weekly (BACKUP_VERIFY_EVERY): `pgbackrest verify` plus a restore test of the newest backup into a
#                 scratch directory, started on a private port and queried, then deleted (docs/operations/backup-and-restore.md)
# Status lines go to stdout (docker compose logs pgbackrest); the newest outcome is kept in $state for `opportunity.sh
# --profile full backup status`.
set -euo pipefail

stanza="${PGBACKREST_STANZA:-opportunity}"
full_every="${BACKUP_FULL_EVERY:-604800}"
diff_every="${BACKUP_DIFF_EVERY:-86400}"
incr_every="${BACKUP_INCR_EVERY:-21600}"
verify_every="${BACKUP_VERIFY_EVERY:-604800}"
state=/var/spool/pgbackrest/scheduler
mkdir -p "$state"

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

# Seconds since the newest backup whose type is in $1 (a jq-free parse of `info --output=json`: stop timestamps by type).
age_of() {
  local types="$1" newest=0 type stop
  while read -r type stop; do
    [[ " $types " == *" $type "* ]] && ((stop > newest)) && newest=$stop
  # Each backup object ends with ..."timestamp":{"start":N,"stop":N},"type":"full|diff|incr"} (keys are sorted).
  done < <(pgbackrest --stanza="$stanza" --output=json info 2>/dev/null \
    | tr '{}' '\n\n' | sed -n 's/^"start":[0-9]*,"stop":\([0-9]*\)$/S \1/p; s/^,"type":"\(full\|diff\|incr\)".*/T \1/p' \
    | awk '/^S / { s = $2; next } /^T / && s != "" { print $2, s; s = "" }')
  if ((newest == 0)); then echo 999999999; else echo $(($(date +%s) - newest)); fi
}

ensure_stanza() {
  until pg_isready -q -h /var/run/postgresql; do sleep 5; done
  pgbackrest --stanza="$stanza" stanza-create >/dev/null 2>&1 || pgbackrest --stanza="$stanza" stanza-upgrade >/dev/null 2>&1 || true
  pgbackrest --stanza="$stanza" check
}

backup() {
  log "backup $1 starting"
  if pgbackrest --stanza="$stanza" --type="$1" backup; then
    log "backup $1 ok"
    date -u +%s >"$state/last-$1-ok"
  else
    log "backup $1 FAILED"
    date -u +%s >"$state/last-$1-failed"
  fi
}

# Restores the newest backup into a scratch directory, starts it on a private socket, runs a query, removes it.
restore_test() {
  local scratch=/var/spool/pgbackrest/restore-test port=54329 started
  rm -rf "$scratch" && mkdir -p "$scratch"
  started=$(date +%s)
  pgbackrest --stanza="$stanza" --pg1-path="$scratch/data" --no-delta --type=immediate --target-action=promote --archive-mode=off restore
  # Socket only, in the scratch folder, and only this OS user (peer): the restored copy is never reachable from outside.
  printf 'local all postgres peer\n' >"$scratch/hba.conf"
  pg_ctl -D "$scratch/data" -o "-p $port -k $scratch -c archive_mode=off -c hba_file=$scratch/hba.conf -c listen_addresses=''" -w -t 3600 start >/dev/null
  psql -h "$scratch" -p "$port" -d postgres -tAc "SELECT 'restore-test ok: ' || count(*) || ' databases' FROM pg_database"
  local opp
  opp="$(psql -h "$scratch" -p "$port" -d "${OPPORTUNITY_DB:-opportunity}" -tAc \
    "SELECT max(version) FROM opportunity_migrations.schema_history" 2>/dev/null || echo '?')"
  pg_ctl -D "$scratch/data" -m fast -w stop >/dev/null
  rm -rf "$scratch"
  log "restore test ok in $(($(date +%s) - started)) s (schema version $opp)"
}

verify() {
  log "verify starting"
  if pgbackrest --stanza="$stanza" verify && restore_test; then
    date -u +%s >"$state/last-verify-ok"
  else
    log "verify FAILED"
    date -u +%s >"$state/last-verify-failed"
  fi
}

since() { [[ -s "$state/$1" ]] && echo $(($(date +%s) - $(cat "$state/$1"))) || echo 999999999; }

# `source scheduler.sh --functions-only` (opportunity.sh backup verify) defines the functions without starting the loop.
if [[ "${1:-}" == --functions-only ]]; then
  return 0 2>/dev/null || exit 0
fi

ensure_stanza
log "scheduler running: full every ${full_every}s, diff every ${diff_every}s, incr every ${incr_every}s, verify every ${verify_every}s"
while true; do
  if (($(age_of "full") >= full_every)); then
    backup full
  elif (($(age_of "full diff") >= diff_every)); then
    backup diff
  elif (($(age_of "full diff incr") >= incr_every)); then
    backup incr
  elif (($(since last-verify-ok) >= verify_every)); then
    verify
  fi
  sleep 300
done
