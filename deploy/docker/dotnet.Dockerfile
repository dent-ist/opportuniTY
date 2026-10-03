# syntax=docker/dockerfile:1
# .NET images: api, worker, migrator (E01-T05, docs/ci.md#container-images). Build from the repository root:
#   docker buildx build -f deploy/docker/dotnet.Dockerfile --target api -t opportunity-api .
# The SDK stage runs on the build host and cross-compiles for TARGETARCH, so multi-arch builds need no emulation.
# Base images are pinned by multi-arch index digest directly in FROM, where Dependabot (docker ecosystem) finds and
# bumps tag and digest together.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS source
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# Data directories the chiseled images cannot create at runtime (no shell). Named volumes mounted there inherit the
# app user's ownership, e.g. the Lite filesystem object store (ObjectStorage__FileSystem__RootPath).
RUN mkdir -p /rootfs/var/lib/opportunity/objects
WORKDIR /repo
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
ARG TARGETARCH
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
# publish <project> <output dir>: framework-dependent, cross-compiled for TARGETARCH (amd64/arm64 are valid -a values).
COPY <<'EOF' /usr/local/bin/publish
#!/bin/sh
set -eu
# Optional build secret for builders behind a TLS-intercepting proxy; never stored in a layer.
[ -s /run/secrets/build-ca ] && export SSL_CERT_FILE=/run/secrets/build-ca
# MSBuild imports environment variables as (case-insensitive) properties: an inherited VERSION would become $(Version)
# and a non-SemVer value such as "pr-171" breaks restore. Pass them explicitly instead and hide them from MSBuild.
exec env -u VERSION -u REVISION dotnet publish "src/$1/$1.csproj" -c Release -a "$TARGETARCH" --no-self-contained \
  -p:OpenApiGenerateDocuments=false -p:ContinuousIntegrationBuild=true \
  -p:InformationalVersion="$VERSION+$REVISION" -o "$2"
EOF
RUN chmod +x /usr/local/bin/publish

# One publish stage per image; BuildKit only builds the stages the requested --target needs.
FROM source AS publish-probe
RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages,sharing=locked \
    --mount=type=secret,id=build-ca,required=false publish Opportunity.HealthProbe /out/probe

FROM source AS publish-api
RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages,sharing=locked \
    --mount=type=secret,id=build-ca,required=false publish Opportunity.Api /out/app

FROM source AS publish-worker
RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages,sharing=locked \
    --mount=type=secret,id=build-ca,required=false publish Opportunity.Worker.All /out/app

FROM source AS publish-migrator
RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages,sharing=locked \
    --mount=type=secret,id=build-ca,required=false publish Opportunity.Migrator /out/app

# ---------------------------------------------------------------------------------------------------------------------
# Shared runtime settings. Chiseled images have no shell or package manager and run as the non-root "app" user
# (UID/GID 1654). Nothing below writes to the root filesystem, so containers run with read_only: true; give them a
# tmpfs at /tmp for the .NET diagnostics socket.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS aspnet-base
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
ARG CREATED
LABEL org.opencontainers.image.vendor="opportuniTY contributors" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/plogramer/opportuniTY" \
      org.opencontainers.image.url="https://github.com/plogramer/opportuniTY" \
      org.opencontainers.image.documentation="https://github.com/plogramer/opportuniTY/blob/main/docs/ci.md#container-images" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}"
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
COPY --from=source --link --chown=1654:1654 /rootfs/var/lib/opportunity/ /var/lib/opportunity/
WORKDIR /app
USER $APP_UID
EXPOSE 8080
# Liveness only (process up). Readiness (/health/ready) is for orchestrator probes, not container restarts.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/healthprobe/Opportunity.HealthProbe.dll", "http://127.0.0.1:8080/health/live"]

# ---------------------------------------------------------------------------------------------------------------------
FROM aspnet-base AS api
LABEL org.opencontainers.image.title="opportunity-api" \
      org.opencontainers.image.description="opportuniTY REST API (/api/v1) with /health/live and /health/ready on port 8080."
COPY --from=publish-probe --link /out/probe /app/healthprobe/
COPY --from=publish-api --link /out/app /app/
ENTRYPOINT ["dotnet", "/app/Opportunity.Api.dll"]

# ---------------------------------------------------------------------------------------------------------------------
# One worker image for every worker type: Workers__Enabled=all (default) runs the combined Lite worker,
# Workers__Enabled=import (or a comma-separated subset) runs one type per container (Full profile).
FROM aspnet-base AS worker
LABEL org.opencontainers.image.title="opportunity-worker" \
      org.opencontainers.image.description="opportuniTY worker host; Workers__Enabled selects the worker types (default: all). Health probes on port 8080."
ENV Workers__Enabled=all
COPY --from=publish-probe --link /out/probe /app/healthprobe/
COPY --from=publish-worker --link /out/app /app/
ENTRYPOINT ["dotnet", "/app/Opportunity.Worker.All.dll"]

# ---------------------------------------------------------------------------------------------------------------------
# One-shot job: applies PostgreSQL migrations and bootstrap steps, then exits (0 ok, 1 migration failed,
# 2 configuration error, 3 bootstrap step failed). No HEALTHCHECK: it is not a long-running service.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled@sha256:cef1854a63da04795952a34c970591693d1f31c8e8f42ec14892d6198a49774b AS migrator
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
ARG CREATED
LABEL org.opencontainers.image.title="opportunity-migrator" \
      org.opencontainers.image.description="opportuniTY one-shot migrator: PostgreSQL migrations and infrastructure bootstrap. Needs ConnectionStrings__Migrator." \
      org.opencontainers.image.vendor="opportuniTY contributors" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/plogramer/opportuniTY" \
      org.opencontainers.image.url="https://github.com/plogramer/opportuniTY" \
      org.opencontainers.image.documentation="https://github.com/plogramer/opportuniTY/blob/main/docs/ci.md#container-images" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}"
WORKDIR /app
USER $APP_UID
COPY --from=publish-migrator --link /out/app /app/
ENTRYPOINT ["dotnet", "/app/Opportunity.Migrator.dll"]
