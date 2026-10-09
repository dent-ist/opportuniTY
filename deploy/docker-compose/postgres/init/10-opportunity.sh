#!/bin/sh
# First start only (empty data volume): the migrator login and the application database.
#   OPPORTUNITY_DB_OWNER_USER  runs the migrator and owns every object (no superuser, no CREATEROLE)
#   OPPORTUNITY_DB_APP_USER    optional shared runtime login, member of opportunity_app (DML only, no BYPASSRLS); only
#                              the benchmark reference stack still sets it. The developer profile gives every component
#                              its own login instead (30-component-logins.sh, E05-T07).
# The NOLOGIN group roles are created here with the same attributes V0001 uses, so membership can be granted before
# the first migration; V0001 skips roles that already exist. Changing the owner password in .env later has no effect on
# an existing volume: run `./opportunity.sh reset` (or ALTER ROLE ... PASSWORD).
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v db="$OPPORTUNITY_DB" \
  -v owner="$OPPORTUNITY_DB_OWNER_USER" -v owner_password="$OPPORTUNITY_DB_OWNER_PASSWORD" <<'SQL'
CREATE ROLE opportunity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE opportunity_readonly NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE :"owner" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'owner_password';
CREATE DATABASE :"db" OWNER :"owner";
SQL

if [ -n "${OPPORTUNITY_DB_APP_USER:-}" ] && [ -n "${OPPORTUNITY_DB_APP_PASSWORD:-}" ]; then
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
    -v app="$OPPORTUNITY_DB_APP_USER" -v app_password="$OPPORTUNITY_DB_APP_PASSWORD" <<'SQL'
CREATE ROLE :"app" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'app_password'
    IN ROLE opportunity_app;
SQL
fi
