#!/bin/bash
# Full profile one-shot `opensearch-security` (E19-T06, ADR-015 D14.3): builds the security plugin configuration from the
# committed files (config.yml, roles.yml, roles_mapping.yml) plus internal users whose bcrypt hashes are computed here
# from the Docker secrets, uploads it with securityadmin and the operator's admin certificate, then waits until the
# cluster is green. Runs on every `up`, so a password changed in full/secrets/ is applied (like the database logins).
# Nothing else holds the admin certificate.
set -euo pipefail

tools=/usr/share/opensearch/plugins/opensearch-security/tools
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp /usr/share/opensearch/config/opensearch-security/*.yml "$work/"
cp /etc/opportunity/opensearch/config.yml /etc/opportunity/opensearch/roles.yml /etc/opportunity/opensearch/roles_mapping.yml "$work/"

hash() {
  # The last line of hash.sh's output is the bcrypt hash; the password goes through the environment, not argv.
  OPENSEARCH_JAVA_HOME=/usr/share/opensearch/jdk "$tools/hash.sh" -env OPPORTUNITY_HASH_INPUT 2>/dev/null | tail -n 1
}

{
  printf '_meta:\n  type: "internalusers"\n  config_version: 2\n'
  for user in admin opportunity-api opportunity-indexer opportunity-dispatcher opportunity-migrator opportunity-monitor; do
    secret="/run/secrets/opensearch-${user#opportunity-}-password"
    [[ "$user" == admin ]] && secret=/run/secrets/opensearch-admin-password
    OPPORTUNITY_HASH_INPUT="$(cat "$secret")"
    export OPPORTUNITY_HASH_INPUT
    printf '%s:\n  hash: "%s"\n  reserved: true\n  description: "opportuniTY Full profile (generated)"\n' "$user" "$(hash)"
  done
} >"$work/internal_users.yml"
unset OPPORTUNITY_HASH_INPUT

certs=/etc/opportunity/opensearch-admin
for attempt in $(seq 1 60); do
  if OPENSEARCH_JAVA_HOME=/usr/share/opensearch/jdk "$tools/securityadmin.sh" -cd "$work" -icl -h "${OPENSEARCH_HOST:-opensearch-1}" \
      -cacert "$certs/ca.crt" -cert "$certs/admin.crt" -key "$certs/admin.key"; then
    break
  fi
  [[ "$attempt" == 60 ]] && { echo "opensearch-security: securityadmin failed 60 times" >&2; exit 1; }
  echo "opensearch-security: cluster not ready, retrying in 5 s ($attempt/60)"
  sleep 5
done

monitor="opportunity-monitor:$(cat /run/secrets/opensearch-monitor-password)"
until curl -fsS --cacert "$certs/ca.crt" -u "$monitor" \
    "https://${OPENSEARCH_HOST:-opensearch-1}:9200/_cluster/health?wait_for_status=green&wait_for_nodes=${OPENSEARCH_NODES:-3}&timeout=30s" >/dev/null; do
  echo "opensearch-security: waiting for a green cluster with ${OPENSEARCH_NODES:-3} nodes"
  sleep 5
done
echo "opensearch-security: configuration applied; cluster green"
