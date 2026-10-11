#!/bin/bash
# Full profile point-in-time restore of PostgreSQL (E19-T08, ADR-016 §2 and §8 phase R3; runbook
# docs/operations/backup-and-restore.md). Runs in a one-off `pgbackrest` container with the data volume mounted read-write
# while the postgres service is STOPPED (`./opportunity.sh --profile full restore-pg ...` does all of this):
#   restore-to-time.sh <timestamp>   recover to that time (e.g. "2026-10-11 09:41:00+00"), then promote
#   restore-to-time.sh latest        recover to the end of the archived WAL (crash, host loss with the archive intact)
# Optional: --set <backup label> (from `restore plan`), --dry-run (prints the command only).
# The data directory is restored in place with --delta (only changed files are copied). Recovery settings are written
# into postgresql.auto.conf and recovery.signal by pgBackRest; the server promotes when it reaches the target and starts
# a new timeline. The streaming replica must be re-cloned afterwards (it is on the old timeline).
set -euo pipefail

stanza="${PGBACKREST_STANZA:-opportunity}"
target="${1:?usage: restore-to-time.sh <timestamp|latest> [--set <label>] [--dry-run]}"
shift
set_args=()
dry_run=0
while (($#)); do
  case "$1" in
    --set) set_args=(--set="${2:?--set needs a backup label}"); shift 2 ;;
    --dry-run) dry_run=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

data=/var/lib/postgresql/data
# pgBackRest refuses to restore over a data directory with postmaster.pid as well; say why first.
if [[ -e "$data/postmaster.pid" ]]; then
  echo "$data/postmaster.pid exists: stop the postgres service first (no writer may survive a restore, ADR-016 §1.4)." >&2
  echo "After a crash the file is stale: remove it only when no server runs on this volume." >&2
  exit 1
fi

if [[ "$target" == latest ]]; then
  # Replays to the end of the archived WAL, then promotes on its own.
  args=(--type=default)
else
  # Inclusive (the default): the restored database holds exactly the transactions committed at or before T_r.
  args=(--type=time "--target=$target" --target-action=promote)
fi

command=(pgbackrest --stanza="$stanza" --delta "${set_args[@]}" "${args[@]}" restore)
echo "${command[*]}"
((dry_run)) && exit 0
started=$(date +%s)
"${command[@]}"
echo "restored in $(($(date +%s) - started)) s; start the postgres service: it replays WAL to the target and promotes."
echo "then: pgbackrest --stanza=$stanza check   and   ./opportunity.sh --profile full compose run --rm migrator"
