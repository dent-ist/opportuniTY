#!/bin/sh
# Full profile (E19-T06): PostgreSQL refuses a private key that its own user cannot exclusively read, and the
# bind-mounted certificate files belong to the operator. Copy them (as root, before the image entrypoint drops to
# postgres) into a directory only postgres can read, then run the given command (docker-entrypoint.sh postgres ..., or
# the replica's clone-and-run script through gosu).
set -eu

src=/etc/opportunity/tls-src
dst=/var/lib/postgresql/tls
mkdir -p "$dst"
cp "$src/server.crt" "$dst/server.crt"
cp "$src/server.key" "$dst/server.key"
cp "$src/ca.crt" "$dst/ca.crt"
chown -R postgres:postgres "$dst"
chmod 0700 "$dst"
chmod 0600 "$dst/server.key"
chmod 0644 "$dst/server.crt" "$dst/ca.crt"

exec "$@"
