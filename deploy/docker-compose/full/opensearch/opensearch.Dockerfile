# syntax=docker/dockerfile:1
# Full profile OpenSearch with the repository-s3 plugin (E19-T08, ADR-016 §3.1): hourly snapshots to a dedicated bucket
# with its own credentials. The plugin (Apache-2.0) is not bundled in the official image; its version must equal the
# server's. compose.full.yaml builds it for opensearch-1..3:
#   docker build -f opensearch.Dockerfile --build-arg OPENSEARCH=3.2.0 --build-arg OPENSEARCH_DIGEST=sha256:... deploy/docker-compose/full/opensearch
ARG OPENSEARCH=3.2.0
ARG OPENSEARCH_DIGEST
FROM opensearchproject/opensearch:${OPENSEARCH}@${OPENSEARCH_DIGEST}

RUN /usr/share/opensearch/bin/opensearch-plugin install --batch repository-s3
