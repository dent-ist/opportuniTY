# Test-only image for the PostgreSQL PITR drill (E19-T08, PgBackRestDrillTests). The Full profile installs pgBackRest
# from the PGDG apt repository (deploy/docker-compose/full/pgbackrest/postgres.Dockerfile); sandboxes without access to
# it build this instead: the pgBackRest binary of a published image (Ubuntu, newer glibc) with its own loader and
# libraries. The binary's ELF interpreter is pointed at the bundled loader (same-length path, NUL padded) and the
# wrapper puts the bundled libraries first for pgBackRest and the processes it starts itself (asynchronous archive-push,
# parallel backup and restore), never for PostgreSQL.
ARG POSTGRES_IMAGE
ARG PGBACKREST_IMAGE
FROM ${PGBACKREST_IMAGE} AS pgbackrest
USER root
RUN mkdir -p /bundle/lib \
 && ldd /usr/bin/pgbackrest | awk '/=>/ { print $3 }' | sort -u | xargs -I{} cp -L {} /bundle/lib/ \
 && cp -L /lib64/ld-linux-x86-64.so.2 /bundle/lib/ld.so \
 && perl -pe 's#/lib64/ld-linux-x86-64.so.2#/opt/pgbackrest/lib/ld.so\0\0#' /usr/bin/pgbackrest > /bundle/pgbackrest \
 && chmod 0755 /bundle/pgbackrest

FROM ${POSTGRES_IMAGE}
COPY --from=pgbackrest /bundle /opt/pgbackrest
RUN printf '#!/bin/sh\nLD_LIBRARY_PATH=/opt/pgbackrest/lib exec /opt/pgbackrest/pgbackrest "$@"\n' > /usr/bin/pgbackrest \
 && chmod 0755 /usr/bin/pgbackrest \
 && install -d -o postgres -g postgres -m 0750 /var/lib/pgbackrest /var/spool/pgbackrest /var/log/pgbackrest /tmp/pgbackrest /etc/pgbackrest \
 && pgbackrest version
