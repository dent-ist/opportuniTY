#!/bin/sh
# Full profile primary, first start only (E19-T06): the replication login for postgres-replica. The replica creates its
# own physical replication slot when it clones the primary, so WAL is kept until the replica has received it.
# Asynchronous by default (RPO <= 5 min, ADR-016); POSTGRES_SYNC_STANDBY='FIRST 1 (postgres_replica)' makes it synchronous.
set -eu

password="$(cat "$REPLICATION_PASSWORD_FILE")"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v password="$password" -v standby="${POSTGRES_SYNC_STANDBY:-}" <<'SQL'
CREATE ROLE replicator WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'password';
ALTER SYSTEM SET synchronous_standby_names = :'standby';
SQL
