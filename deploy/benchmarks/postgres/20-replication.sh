#!/bin/sh
# Enterprise-reference primary, first start only: a replication login for pg-replica, and the synchronous standby
# setting (ALTER SYSTEM, so it applies from the first real start; the temporary init server must not wait for a
# standby that does not exist yet). REF_PG_SYNC_STANDBY='' keeps the replica asynchronous.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v password="$REPLICATION_PASSWORD" -v standby="${REF_PG_SYNC_STANDBY:-}" <<'SQL'
CREATE ROLE replicator WITH LOGIN REPLICATION PASSWORD :'password';
ALTER SYSTEM SET synchronous_standby_names = :'standby';
SQL

echo "host replication replicator all scram-sha-256" >>"$PGDATA/pg_hba.conf"
