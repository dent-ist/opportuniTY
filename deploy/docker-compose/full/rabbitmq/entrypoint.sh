#!/bin/sh
# Full profile RabbitMQ node entrypoint (E19-T06), as root before the image entrypoint drops to the rabbitmq user:
#  - the Erlang cookie every node of the cluster shares comes from a Docker secret (0400, owned by rabbitmq);
#  - the TLS certificate and key are copied where only rabbitmq can read them;
#  - the operator's administrator password is read from its secret file (never a plain variable, ADR-015 D10.1).
set -eu

cp /run/secrets/rabbitmq-erlang-cookie /var/lib/rabbitmq/.erlang.cookie
chown rabbitmq:rabbitmq /var/lib/rabbitmq/.erlang.cookie
chmod 0400 /var/lib/rabbitmq/.erlang.cookie

mkdir -p /var/lib/rabbitmq/tls
cp /etc/opportunity/tls-src/server.crt /etc/opportunity/tls-src/server.key /etc/opportunity/tls-src/ca.crt /var/lib/rabbitmq/tls/
chown -R rabbitmq:rabbitmq /var/lib/rabbitmq/tls
chmod 0700 /var/lib/rabbitmq/tls
chmod 0600 /var/lib/rabbitmq/tls/server.key

RABBITMQ_DEFAULT_PASS="$(cat /run/secrets/rabbitmq-admin-password)"
export RABBITMQ_DEFAULT_PASS

exec docker-entrypoint.sh "$@"
