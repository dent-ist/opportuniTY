#!/bin/bash
# Full profile OpenSearch node entrypoint (E19-T08, ADR-016 §3.1). The repository-s3 plugin reads its credentials from
# the keystore only (never opensearch.yml): the snapshot bucket's own S3 identity (opportunity-snapshots, full/seaweedfs/
# run.sh) goes in from its Docker secret at every start. The S3 gateways serve TLS with the installation CA, which the
# plugin's HTTP client checks against the JVM trust store: a copy of the bundled one plus that CA. Then the image's
# entrypoint starts the node as before.
set -euo pipefail

home=/usr/share/opensearch
keystore="$home/bin/opensearch-keystore"
[[ -f "$home/config/opensearch.keystore" ]] || "$keystore" create >/dev/null
printf '%s' opportunity-snapshots | "$keystore" add --stdin --force s3.client.default.access_key >/dev/null
"$keystore" add --stdin --force s3.client.default.secret_key </run/secrets/s3-snapshots-secret-key >/dev/null

truststore="$home/config/opportunity-truststore.p12"
rm -f "$truststore"
"$home/jdk/bin/keytool" -importkeystore -noprompt -srckeystore "$home/jdk/lib/security/cacerts" -srcstorepass changeit \
  -destkeystore "$truststore" -deststoretype PKCS12 -deststorepass changeit >/dev/null 2>&1
"$home/jdk/bin/keytool" -importcert -noprompt -alias opportunity-ca -file "$home/config/certs/ca.crt" \
  -keystore "$truststore" -storepass changeit >/dev/null
export OPENSEARCH_JAVA_OPTS="${OPENSEARCH_JAVA_OPTS:-} -Djavax.net.ssl.trustStore=$truststore -Djavax.net.ssl.trustStorePassword=changeit -Djavax.net.ssl.trustStoreType=PKCS12"

exec "$home/opensearch-docker-entrypoint.sh" "$@"
