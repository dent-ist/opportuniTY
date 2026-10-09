#!/bin/sh
# Runtime logins per component (E05-T07, ADR-015 D7.3/D9.6): no component shares a database credential, none is a
# superuser, owns objects or bypasses RLS.
#   OPPORTUNITY_DB_API_USER      the API (BFF): member of opportunity_app (DML, RLS applies)
#   OPPORTUNITY_DB_WORKER_USER   the combined Lite worker: member of opportunity_app. It runs every worker type in one
#                                process and so holds their union (accepted risk AR-04); split worker hosts each get a
#                                login of their own (opportunity_worker_<type>, see docs/operations/message-trust.md)
#   OPPORTUNITY_DB_MONITOR_USER  postgres-exporter: pg_monitor only, no access to application tables
# Idempotent, as superuser: runs on first start (after 10-opportunity.sh and 20-group-roles.sql) and again before every
# `./opportunity.sh up`, so a password changed in .env is applied and data volumes created before this script get the
# logins. A login whose password variable is empty is left alone.
set -eu

db="${OPPORTUNITY_DB:-opportunity}"

login() {
  # $1 role name, $2 password, $3 group role to join
  [ -n "$2" ] || return 0
  psql -q -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres \
    -v name="$1" -v password="$2" -v grp="$3" -v db="$db" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'name')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'name') \gexec
SELECT format('ALTER ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', :'name', :'password') \gexec
SELECT format('GRANT %I TO %I', :'grp', :'name') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'name') \gexec
SQL
}

login "${OPPORTUNITY_DB_API_USER:-opportunity_api}" "${OPPORTUNITY_DB_API_PASSWORD:-}" opportunity_app
login "${OPPORTUNITY_DB_WORKER_USER:-opportunity_worker}" "${OPPORTUNITY_DB_WORKER_PASSWORD:-}" opportunity_app
login "${OPPORTUNITY_DB_MONITOR_USER:-opportunity_monitor}" "${OPPORTUNITY_DB_MONITOR_PASSWORD:-}" pg_monitor
