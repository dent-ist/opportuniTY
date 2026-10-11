#!/bin/sh
# Full profile (E19-T08): pgBackRest's repository secrets from Docker secrets (ADR-015 D10.1) into the environment the
# server inherits, so archive_command (archive-push) and every pgbackrest command in this container can reach repo1.
# Then runs the given command (the TLS entrypoint and docker-entrypoint.sh, the scheduler, or a restore).
set -eu

secret() { [ -s "/run/secrets/$1" ] && cat "/run/secrets/$1"; }

PGBACKREST_REPO1_CIPHER_PASS="$(secret pgbackrest-cipher-pass)"
PGBACKREST_REPO1_S3_KEY=opportunity-pgbackrest
PGBACKREST_REPO1_S3_KEY_SECRET="$(secret s3-pgbackrest-secret-key)"
export PGBACKREST_REPO1_CIPHER_PASS PGBACKREST_REPO1_S3_KEY PGBACKREST_REPO1_S3_KEY_SECRET

# The spool, log and lock folders live on volumes or tmpfs that start out owned by root.
for dir in /var/spool/pgbackrest /var/log/pgbackrest /tmp/pgbackrest; do
  mkdir -p "$dir"
  [ "$(id -u)" = 0 ] && chown postgres:postgres "$dir"
done

exec "$@"
