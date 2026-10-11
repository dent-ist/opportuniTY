#!/bin/sh
# Full profile: the SeaweedFS filer's own PostgreSQL cluster (ADR-016 §2.7, amends ADR-020 rule 2), first start only.
# The filer connects with a login that owns its database and nothing else, not the cluster superuser.
set -eu

password="$(cat "$SEAWEEDFS_DB_PASSWORD_FILE")"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres -v password="$password" <<'SQL'
CREATE ROLE seaweedfs LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'password';
CREATE DATABASE seaweedfs OWNER seaweedfs;
SQL
