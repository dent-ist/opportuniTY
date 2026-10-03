# syntax=docker/dockerfile:1
# Web image: the Angular production build served by nginx-unprivileged (E01-T05, docs/ci.md#container-images).
#   docker buildx build -f deploy/docker/web.Dockerfile -t opportunity-web .
# The Node stage runs on the build host (the bundle is architecture-independent); only the nginx stage is per-arch.
# Base images are pinned by multi-arch index digest directly in FROM, where Dependabot (docker ecosystem) finds and
# bumps tag and digest together.

FROM --platform=$BUILDPLATFORM node:24-alpine@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1 AS build
WORKDIR /web
ENV NG_CLI_ANALYTICS=false CI=true
COPY src/Opportunity.Web/package.json src/Opportunity.Web/package-lock.json ./
# build-ca: optional build secret for builders behind a TLS-intercepting proxy; never stored in a layer.
RUN --mount=type=cache,target=/root/.npm,sharing=locked \
    --mount=type=secret,id=build-ca,required=false \
    if [ -s /run/secrets/build-ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/build-ca; fi; \
    npm ci --no-audit --no-fund
COPY src/Opportunity.Web/ ./
RUN npm run build -- --configuration production

# ---------------------------------------------------------------------------------------------------------------------
# Runs as the unprivileged nginx user (UID 101) on port 8080 with a read-only root filesystem; mount a tmpfs at /tmp
# (pid file and nginx temp paths). nginx starts directly, skipping the image's entrypoint scripts, which rewrite config.
FROM nginxinc/nginx-unprivileged:1.31-alpine-slim@sha256:c81a27f28bc2d9c2da8998444e653c7b85b9bbbaa92e44ef18d8920784e06507 AS web
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
ARG CREATED
LABEL org.opencontainers.image.title="opportunity-web" \
      org.opencontainers.image.description="opportuniTY web UI: Angular static build served by nginx-unprivileged on port 8080." \
      org.opencontainers.image.vendor="opportuniTY contributors" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/plogramer/opportuniTY" \
      org.opencontainers.image.url="https://github.com/plogramer/opportuniTY" \
      org.opencontainers.image.documentation="https://github.com/plogramer/opportuniTY/blob/main/docs/ci.md#container-images" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}"
# Alpine often ships a fixed package before the nginx image is rebuilt; take the fixes so the Trivy gate (High/Critical)
# stays green between base-image bumps. This is the only per-arch RUN (QEMU for arm64 in CI).
USER root
# apk ignores SSL_CERT_FILE, so the optional build-ca secret is mounted over the CA bundle for this step only.
# APK_UPGRADE=0 skips it for builders without access to the Alpine mirrors (the Trivy gate then sees base-image CVEs).
ARG APK_UPGRADE=1
RUN --mount=type=secret,id=build-ca,required=false,target=/etc/ssl/certs/ca-certificates.crt \
    if [ "$APK_UPGRADE" = 1 ]; then apk upgrade --no-cache; fi
COPY --link deploy/docker/web/nginx.conf /etc/nginx/nginx.conf
COPY --link deploy/docker/web/security-headers.conf /etc/nginx/security-headers.conf
COPY --link --from=build /web/dist/opportunity-web/browser/ /usr/share/nginx/html/
USER 101
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/healthz"]
STOPSIGNAL SIGQUIT
ENTRYPOINT ["nginx", "-g", "daemon off;"]
