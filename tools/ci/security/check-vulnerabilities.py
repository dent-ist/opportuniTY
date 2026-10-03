#!/usr/bin/env python3
"""Fail on High/Critical dependency vulnerabilities unless a documented, unexpired exception exists.

Inputs (either or both):
  --dotnet FILE   output of `dotnet list <sln> package --vulnerable --include-transitive --format json`
  --npm FILE      output of `npm audit --json`
  --trivy FILE    output of `trivy image --format json` (container images, E01-T05); repeatable
Exceptions: tools/ci/security/vulnerability-exceptions.json (see docs/ci.md#security-and-supply-chain-gates).
Writes a Markdown summary to --report (and $GITHUB_STEP_SUMMARY when set). Exit 1 on any unexcepted finding,
on an expired exception, or on a malformed exceptions file.
"""
import argparse
import datetime as dt
import json
import os
import re
import sys

BLOCKING = {"high", "critical"}
MAX_EXCEPTION_DAYS = 90
GHSA = re.compile(r"GHSA(-[23456789cfghjmpqrvwx]{4}){3}", re.IGNORECASE)
REQUIRED = ("advisory", "package", "reason", "expires", "approvedBy")


def advisory_id(url_or_id):
    m = GHSA.search(url_or_id or "")
    return m.group(0).upper() if m else (url_or_id or "").strip()


def dotnet_findings(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    problems = data.get("problems", []) + [p for proj in data.get("projects", []) for p in proj.get("problems", [])]
    for problem in problems:
        # e.g. a project that was not restored: never pass silently.
        if problem.get("level", "error").lower() == "error":
            yield ("nuget", "<audit>", "", "critical", "error", problem.get("text", str(problem)))
    for project in data.get("projects", []):
        for fw in project.get("frameworks", []):
            for pkg in fw.get("topLevelPackages", []) + fw.get("transitivePackages", []):
                version = pkg.get("resolvedVersion", "")
                for v in pkg.get("vulnerabilities", []):
                    url = v.get("advisoryurl", "")
                    yield ("nuget", pkg["id"], version, v.get("severity", "").lower(), advisory_id(url), url)


def npm_findings(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if "error" in data:
        yield ("npm", "<audit>", "", "critical", "error", json.dumps(data["error"]))
    for name, vuln in data.get("vulnerabilities", {}).items():
        for via in vuln.get("via", []):
            if isinstance(via, dict):  # strings point at another entry that carries the advisory itself
                url = via.get("url", "")
                yield ("npm", via.get("name", name), vuln.get("range", ""), via.get("severity", "").lower(),
                       advisory_id(url), url)


def trivy_findings(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    image = data.get("ArtifactName", path)
    if "Results" not in data:
        yield ("image", "<scan>", "", "critical", "error", f"no Results in Trivy report for {image}")
    for result in data.get("Results") or []:
        eco = f"image:{result.get('Type', '?')}"
        for v in result.get("Vulnerabilities") or []:
            fixed = v.get("FixedVersion") or "no fix yet"
            yield (eco, v["PkgName"], f"{v.get('InstalledVersion', '')} (fixed: {fixed})", v.get("Severity", "").lower(),
                   advisory_id(v["VulnerabilityID"]), v.get("PrimaryURL") or v["VulnerabilityID"])


def load_exceptions(path, today):
    with open(path, encoding="utf-8") as f:
        entries = json.load(f).get("exceptions", [])
    errors, active = [], {}
    for i, e in enumerate(entries):
        missing = [k for k in REQUIRED if not str(e.get(k, "")).strip()]
        if missing:
            errors.append(f"exception #{i + 1} is missing {', '.join(missing)}")
            continue
        try:
            expires = dt.date.fromisoformat(e["expires"])
        except ValueError:
            errors.append(f"exception {e['advisory']}: expires '{e['expires']}' is not YYYY-MM-DD")
            continue
        if expires < today:
            errors.append(f"exception {e['advisory']} ({e['package']}) expired on {e['expires']}; "
                          "fix the dependency or renew the exception with a new review")
            continue
        if (expires - today).days > MAX_EXCEPTION_DAYS:
            errors.append(f"exception {e['advisory']}: expires more than {MAX_EXCEPTION_DAYS} days from today")
            continue
        active[(advisory_id(e["advisory"]), e["package"].lower())] = e
    return errors, active


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dotnet")
    ap.add_argument("--npm")
    ap.add_argument("--trivy", action="append", default=[])
    ap.add_argument("--title", default="Dependency vulnerabilities")
    ap.add_argument("--exceptions", default=os.path.join(os.path.dirname(__file__), "vulnerability-exceptions.json"))
    ap.add_argument("--report")
    ap.add_argument("--today", help="override the current date (tests)")
    args = ap.parse_args()

    today = dt.date.fromisoformat(args.today) if args.today else dt.datetime.now(dt.timezone.utc).date()
    errors, active = load_exceptions(args.exceptions, today)

    findings = []
    if args.dotnet:
        findings += list(dotnet_findings(args.dotnet))
    if args.npm:
        findings += list(npm_findings(args.npm))
    for path in args.trivy:
        findings += list(trivy_findings(path))
    findings = sorted(set(findings))

    rows, used = [], set()
    for eco, pkg, version, severity, adv, url in findings:
        if severity not in BLOCKING:
            status = "below threshold"
        elif (adv, pkg.lower()) in active:
            used.add((adv, pkg.lower()))
            status = f"excepted until {active[(adv, pkg.lower())]['expires']}"
        else:
            status = "**BLOCKING**"
            errors.append(f"{eco} {pkg} {version}: {severity} {adv} {url}".strip())
        rows.append(f"| {eco} | {pkg} | {version} | {severity} | {url or adv} | {status} |")

    lines = [f"## {args.title}", ""]
    if rows:
        lines += ["| Ecosystem | Package | Version | Severity | Advisory | Status |", "|---|---|---|---|---|---|", *rows]
    else:
        lines.append("No known vulnerabilities found.")
    for key, e in active.items():
        if key not in used:
            lines.append(f"\nNote: exception {e['advisory']} ({e['package']}) no longer matches a finding; remove it.")
    if errors:
        lines += ["", "### Failures", *[f"- {e}" for e in errors]]
    report = "\n".join(lines) + "\n"

    for target in filter(None, (args.report, os.environ.get("GITHUB_STEP_SUMMARY"))):
        with open(target, "a", encoding="utf-8") as f:
            f.write(report)
    print(report)
    for e in errors:
        print(f"::error::{e}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
