#!/bin/sh
# Full profile SeaweedFS filer and S3 gateway entrypoint (E19-T06). Reads credentials from Docker secrets (ADR-015 D10.1)
# and then runs the image entrypoint, which drops to the seaweed user.
#   run.sh filer ...  the filer's PostgreSQL password -> WEED_POSTGRES2_PASSWORD (overrides filer.toml)
#   run.sh s3 ...     one S3 identity per component (ADR-015 D9.6), written to /tmp/s3.json:
#                       opportunity-migrator  Admin (creates the bucket at bootstrap)
#                       opportunity-api, -import, -render, -export, -production, -index  Read, List, Write, Tagging on
#                       the opportunity bucket only. The dispatcher and the bulk-coding worker have no object-storage
#                       identity at all. SeaweedFS cannot scope by key prefix, so artifact-class scoping (D9.6) stays
#                       with the application (accepted risk, docs/operations/deployment-profiles.md).
set -eu

bucket="${S3_BUCKET:-opportunity}"

case "$1" in
  filer)
    WEED_POSTGRES2_PASSWORD="$(cat /run/secrets/seaweedfs-db-password)"
    export WEED_POSTGRES2_PASSWORD
    ;;
  s3)
    identity() {
      # $1 access key (component name), $2 actions (JSON list)
      printf '{"name":"%s","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":%s}' \
        "$1" "$1" "$(cat "/run/secrets/s3-${1#opportunity-}-secret-key")" "$2"
    }
    rw="[\"Read:$bucket\",\"List:$bucket\",\"Write:$bucket\",\"Tagging:$bucket\"]"
    umask 077
    {
      printf '{"identities":['
      identity opportunity-migrator '["Admin"]'
      for component in api import index render export production; do
        printf ','
        identity "opportunity-$component" "$rw"
      done
      printf ']}\n'
    } >/tmp/s3.json
    chown seaweed:seaweed /tmp/s3.json
    ;;
esac

exec /entrypoint.sh "$@"
