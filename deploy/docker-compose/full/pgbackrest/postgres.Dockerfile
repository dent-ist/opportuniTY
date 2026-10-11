# syntax=docker/dockerfile:1
# Full profile PostgreSQL with pgBackRest (E19-T08, ADR-016 §2): continuous WAL archiving (archive-push), scheduled
# full/differential/incremental backups, `restore --type=time` and `verify`. pgBackRest is MIT-licensed and comes from
# the PostgreSQL Global Development Group apt repository that the official image already trusts, so it matches the
# server's major version. compose.full.yaml builds it for the `postgres`, `postgres-replica` and `pgbackrest` services:
#   docker build -f postgres.Dockerfile --build-arg POSTGRES=17 --build-arg POSTGRES_DIGEST=sha256:... deploy/docker-compose/full/pgbackrest
ARG POSTGRES=17
ARG POSTGRES_DIGEST
FROM postgres:${POSTGRES}@${POSTGRES_DIGEST}

# Builders behind a TLS-intercepting proxy pass its CA as a build secret (never stored in a layer).
RUN --mount=type=secret,id=build-ca,required=false \
    set -eu; \
    if [ -s /run/secrets/build-ca ]; then cp /run/secrets/build-ca /usr/local/share/ca-certificates/build-ca.crt && update-ca-certificates; fi; \
    apt-get update; \
    apt-get install -y --no-install-recommends pgbackrest ca-certificates; \
    rm -rf /var/lib/apt/lists/* /usr/local/share/ca-certificates/build-ca.crt; \
    update-ca-certificates --fresh >/dev/null; \
    install -d -o postgres -g postgres -m 0750 /var/lib/pgbackrest /var/spool/pgbackrest /var/log/pgbackrest /tmp/pgbackrest; \
    pgbackrest version
