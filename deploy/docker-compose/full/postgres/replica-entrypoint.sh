#!/bin/bash
# Full profile streaming replica (E19-T06), run as postgres: clone the primary over TLS on first start
# (pg_basebackup -R writes primary_conninfo and standby.signal; --create-slot creates the replication slot), then run
# the container's command. It is a hot standby for failover to the end of WAL, not a backup (ADR-016 §2.6).
set -euo pipefail

if [[ ! -s "$PGDATA/PG_VERSION" ]]; then
  export PGPASSWORD
  PGPASSWORD="$(cat "$REPLICATION_PASSWORD_FILE")"
  conninfo="host=${PRIMARY_HOST} port=5432 user=replicator sslmode=verify-full sslrootcert=/var/lib/postgresql/tls/ca.crt application_name=postgres_replica"
  slot=(--create-slot --slot=postgres_replica)
  echo "postgres-replica: cloning ${PRIMARY_HOST} ..."
  until pg_basebackup --pgdata="$PGDATA" --wal-method=stream --write-recovery-conf --checkpoint=fast \
      "${slot[@]}" --dbname="$conninfo"; do
    echo "postgres-replica: primary not ready (or the slot exists from an earlier replica), retrying"
    rm -rf "${PGDATA:?}"/*
    # A slot left behind by a previous replica volume is reused instead of created.
    slot=(--slot=postgres_replica)
    sleep 3
  done
  chmod 0700 "$PGDATA"
fi

exec "$@"
