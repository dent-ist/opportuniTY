#!/bin/bash
# Enterprise-reference streaming replica: clone the primary on first start (pg_basebackup -R writes primary_conninfo
# and standby.signal), then run postgres with the arguments given to the container.
set -euo pipefail

if [[ ! -s "$PGDATA/PG_VERSION" ]]; then
  echo "pg-replica: cloning ${PRIMARY_HOST} ..."
  until PGPASSWORD="$REPLICATION_PASSWORD" pg_basebackup --pgdata="$PGDATA" --wal-method=stream --write-recovery-conf \
      --checkpoint=fast --dbname="host=${PRIMARY_HOST} port=5432 user=replicator application_name=pg_replica"; do
    echo "pg-replica: primary not ready, retrying"
    rm -rf "${PGDATA:?}"/*
    sleep 2
  done
  chmod 0700 "$PGDATA"
fi

exec postgres "$@"
