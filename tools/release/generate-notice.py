#!/usr/bin/env python3
"""Aggregate the third-party license NOTICE of a release (E01-T06).

Usage: generate-notice.py --version X.Y.Z [--versions-env versions.env] [--policy license-policy.json]
                          [--dockerfile FILE ...] --output FILE SBOM.cdx.json [SBOM.cdx.json ...]

Inputs are the CycloneDX SBOMs of shipped code (the same ones the license gate checks in CI: non-test .NET projects
and npm production dependencies). The NOTICE lists, per license, every linked or bundled component with its version,
copyright (when the SBOM carries one) and package URL; then the base images of the opportuniTY images (from the
Dockerfiles' FROM lines) and the services the Compose bundle runs as separate, unmodified containers (from
versions.env). The per-image OS package inventory is in each image's SBOM attestation (docs/ci.md#container-images).
Where the license policy (tools/ci/security/license-policy.json) has a reviewed exception with a `license` for a
component (metadata without a license or with a URL only), that classification is used. Fails when a component has
neither, so a NOTICE never silently omits one.
"""
import argparse
import collections
import json
import os
import re
import sys

# Services the Compose bundle starts as separate containers (never linked). Key: versions.env variable.
SERVICES = {
    "POSTGRES": ("PostgreSQL", "postgres", "PostgreSQL", "database (all profiles)"),
    "OPENSEARCH": ("OpenSearch", "opensearchproject/opensearch", "Apache-2.0", "search (all profiles)"),
    "RABBITMQ": ("RabbitMQ", "rabbitmq", "MPL-2.0", "message broker (all profiles)"),
    "SEAWEEDFS": ("SeaweedFS", "chrislusf/seaweedfs", "Apache-2.0", "S3-compatible object store (`s3` profile)"),
    "KEYCLOAK": ("Keycloak", "keycloak/keycloak", "Apache-2.0", "developer identity provider (dev profile only)"),
    "OTEL_COLLECTOR": ("OpenTelemetry Collector (contrib)", "otel/opentelemetry-collector-contrib", "Apache-2.0",
                       "optional `observability` profile"),
    "PROMETHEUS": ("Prometheus", "prom/prometheus", "Apache-2.0", "optional `observability` profile"),
    "JAEGER": ("Jaeger", "jaegertracing/jaeger", "Apache-2.0", "optional `observability` profile"),
    "LOKI": ("Grafana Loki", "grafana/loki", "AGPL-3.0-only", "optional `observability` profile, unmodified (Q-38, Q-42)"),
    "GRAFANA": ("Grafana", "grafana/grafana", "AGPL-3.0-only", "optional `observability` profile, unmodified (Q-38, Q-42)"),
    "POSTGRES_EXPORTER": ("postgres_exporter", "prometheuscommunity/postgres-exporter", "Apache-2.0",
                          "optional `observability` profile"),
    "OPENSEARCH_EXPORTER": ("elasticsearch_exporter", "prometheuscommunity/elasticsearch-exporter", "Apache-2.0",
                            "optional `observability` profile"),
}

# Base images of the shipped images. The licenses are those of the image's main component; the OS packages inside
# are listed with their own licenses in the per-image SBOM.
BASE_IMAGES = {
    "mcr.microsoft.com/dotnet/aspnet": ("ASP.NET Core runtime (Ubuntu chiseled)", "MIT"),
    "mcr.microsoft.com/dotnet/runtime": (".NET runtime (Ubuntu chiseled)", "MIT"),
    "nginxinc/nginx-unprivileged": ("nginx (Alpine Linux)", "BSD-2-Clause"),
}


def read_env(path):
    values = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip('"')
    return values


def license_label(component):
    terms = []
    for entry in component.get("licenses", []) or []:
        if "expression" in entry:
            terms.append(entry["expression"])
        else:
            lic = entry.get("license", {})
            terms.append(lic.get("id") or lic.get("name") or lic.get("url") or "")
    terms = [t for t in terms if t]
    return " AND ".join(sorted(set(terms))) if terms else None


def walk(components):
    for c in components or []:
        yield c
        yield from walk(c.get("components"))


def base_images(dockerfiles):
    seen = {}
    for path in dockerfiles:
        with open(path, encoding="utf-8") as f:
            for line in f:
                m = re.match(r"^FROM\s+(?:--platform=\S+\s+)?(\S+?)(?::([^@\s]+))?(?:@(sha256:[0-9a-f]{64}))?(?:\s+AS\s+\S+)?\s*$",
                             line.strip(), re.IGNORECASE)
                if m and m.group(1) in BASE_IMAGES:
                    seen[m.group(1)] = (m.group(2) or "", m.group(3) or "")
    return seen


def md(text):
    return str(text).replace("|", "\\|").replace("\n", " ")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", required=True)
    ap.add_argument("--versions-env", default="versions.env")
    ap.add_argument("--policy", default=os.path.join(os.path.dirname(__file__), "..", "ci", "security", "license-policy.json"))
    ap.add_argument("--dockerfile", action="append", default=[])
    ap.add_argument("--output", required=True)
    ap.add_argument("sboms", nargs="+")
    args = ap.parse_args()

    with open(args.policy, encoding="utf-8") as f:
        reviewed = {e["component"]: e["license"] for e in json.load(f).get("exceptions", []) if e.get("license")}

    by_license = collections.defaultdict(dict)
    missing = []
    for path in args.sboms:
        with open(path, encoding="utf-8") as f:
            bom = json.load(f)
        for c in walk(bom.get("components")):
            if c.get("type") not in (None, "library", "framework"):
                continue
            name = f"{c['group']}/{c['name']}" if c.get("group") else c["name"]
            purl = c.get("purl", "")
            if purl.startswith("pkg:nuget/Opportunity.") or name.startswith("Opportunity."):
                continue  # our own projects (MIT, LICENSE)
            base = purl.split("?")[0]
            label = reviewed.get(base) or reviewed.get(base.split("@")[0]) or license_label(c)
            if label is None:
                missing.append(purl or name)
                continue
            key = purl.split("?")[0] or f"{name}@{c.get('version', '')}"
            by_license[label][key] = (name, c.get("version", ""), (c.get("copyright") or "").strip(), purl.split("?")[0])

    if missing:
        for m in sorted(set(missing)):
            print(f"error: {m} declares no license", file=sys.stderr)
        return 1

    env = read_env(args.versions_env)
    total = sum(len(v) for v in by_license.values())
    out = [
        f"# opportuniTY {args.version}: third-party notices",
        "",
        "opportuniTY is licensed under the MIT License (see `LICENSE`). It includes, links or runs the third-party",
        "software listed below under their own licenses. License texts: https://spdx.org/licenses/ (by identifier).",
        "Generated from the release's CycloneDX SBOMs by `tools/release/generate-notice.py`; do not edit by hand.",
        "",
        f"## Linked and bundled components ({total})",
        "",
        "Libraries of the shipped .NET projects (including the tools/ utilities) and of the web bundle, by license.",
        "",
    ]
    for label in sorted(by_license, key=str.lower):
        components = by_license[label]
        out += [f"### {md(label)} ({len(components)})", "", "| Component | Version | Copyright | Package |", "|---|---|---|---|"]
        for key in sorted(components, key=str.lower):
            name, version, copyright_, purl = components[key]
            out.append(f"| {md(name)} | {md(version)} | {md(copyright_)} | `{md(purl)}` |")
        out.append("")

    images = base_images(args.dockerfile)
    if images:
        out += ["## Base images", "", "The OS packages inside each image, with their licenses, are listed in the image's "
                "SBOM attestation (`gh attestation verify ... --predicate-type https://cyclonedx.org/bom`).", "",
                "| Image | Tag | Digest | Contents | License |", "|---|---|---|---|---|"]
        for image in sorted(images):
            tag, digest = images[image]
            what, lic = BASE_IMAGES[image]
            out.append(f"| `{image}` | {tag} | `{digest}` | {what} | {lic} |")
        out.append("")

    out += ["## Services run as separate containers", "",
            "The Compose bundle starts these unmodified upstream images as separate processes; opportuniTY does not "
            "link them.", "", "| Service | Image | Version | License | Used for |", "|---|---|---|---|---|"]
    for key, (name, image, lic, use) in SERVICES.items():
        if key in env:
            out.append(f"| {name} | `{image}` | {env[key]} | {lic} | {use} |")
    out.append("")

    with open(args.output, "w", encoding="utf-8") as f:
        f.write("\n".join(out))
    print(f"{args.output}: {total} components under {len(by_license)} licenses")
    return 0


if __name__ == "__main__":
    sys.exit(main())
