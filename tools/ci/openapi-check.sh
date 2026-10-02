#!/usr/bin/env bash
# OpenAPI gate (ADR-019 §2.1). Run after `dotnet build`, which regenerates the document.
#   1. The generated document must be committed (no drift between code and src/Opportunity.Api/openapi/).
#   2. Compared with BASE_REF, the v1 document must have no breaking changes (oasdiff, ERR level).
# Usage: tools/ci/openapi-check.sh [BASE_REF]   (default: origin/main)
set -euo pipefail

BASE_REF="${1:-origin/main}"
SPEC="src/Opportunity.Api/openapi/opportunity-api-v1.json"
OASDIFF_IMAGE="tufin/oasdiff:v1.11.7"

cd "$(git rev-parse --show-toplevel)"

if ! git diff --exit-code -- "$SPEC"; then
  echo "::error file=$SPEC::Generated OpenAPI document differs from the committed one; build locally and commit it." >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if ! git show "$BASE_REF:$SPEC" >"$work/base.json" 2>/dev/null; then
  echo "No $SPEC at $BASE_REF; nothing to compare."
  exit 0
fi
cp "$SPEC" "$work/head.json"

docker run --rm -v "$work:/specs:ro" "$OASDIFF_IMAGE" breaking --fail-on ERR /specs/base.json /specs/head.json
