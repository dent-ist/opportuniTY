#!/bin/sh
# Broker users per component (E05-T07, ADR-015 D9.5/D9.6), applied by `./opportunity.sh up` inside the rabbitmq
# container after it is healthy. Idempotent: creates each user or resets its password, removes every tag (no management
# UI access) and sets its permissions in the virtual host. The patterns are RabbitMqPermissions (configure, write, read)
# in src/Opportunity.Messaging/RabbitMqTopology.cs; a unit test keeps the two in step.
#   dispatcher   publishes work to opportunity.work, consumes only the dead-letter recorder's queue
#   <area>       consumes the area's work queues, publishes only to <area>.retry and <area>.dlx; never reads another
#                area's queue, a *.dlq or a *.parking queue (those stay with the operator's user, RABBITMQ_USER)
# The API has no broker user (its work goes through the PostgreSQL outbox). Passwords come from the environment
# (RABBITMQ_<NAME>_PASSWORD, generated into .env by `./opportunity.sh init`); rabbitmqctl takes them as arguments inside
# the container, which already holds them in its environment. A user whose password variable is empty is skipped.
set -eu

vhost="${OPPORTUNITY_RABBITMQ_VHOST:-/}"
prefix="${OPPORTUNITY_RABBITMQ_USER_PREFIX:-opportunity-}"

user() {
  # $1 name (without prefix), $2 password, $3 configure, $4 write, $5 read
  [ -n "$2" ] || return 0
  name="$prefix$1"
  if rabbitmqctl -q list_users | cut -f1 | grep -qx "$name"; then
    rabbitmqctl -q change_password "$name" "$2"
  else
    rabbitmqctl -q add_user "$name" "$2"
  fi
  rabbitmqctl -q set_user_tags "$name"
  rabbitmqctl -q set_permissions -p "$vhost" "$name" "$3" "$4" "$5"
}

user dispatcher "${RABBITMQ_DISPATCHER_PASSWORD:-}" '^$' '^opportunity\.work$' '^opportunity\.dead-letter\.record$'
user index "${RABBITMQ_INDEX_PASSWORD:-}" '^$' '^(index)\.(retry|dlx)$' '^(index\.security|index\.interactive|index\.security-bulk|index\.bulk)$'
user import "${RABBITMQ_IMPORT_PASSWORD:-}" '^$' '^(import)\.(retry|dlx)$' '^(import\.chunks)$'
user bulkcoding "${RABBITMQ_BULKCODING_PASSWORD:-}" '^$' '^(bulkcoding)\.(retry|dlx)$' '^(bulkcoding\.chunks)$'
user render "${RABBITMQ_RENDER_PASSWORD:-}" '^$' '^(render)\.(retry|dlx)$' '^(render\.chunks)$'
user export "${RABBITMQ_EXPORT_PASSWORD:-}" '^$' '^(export)\.(retry|dlx)$' '^(export\.chunks)$'
user production "${RABBITMQ_PRODUCTION_PASSWORD:-}" '^$' '^(production)\.(retry|dlx)$' '^(production\.chunks)$'
