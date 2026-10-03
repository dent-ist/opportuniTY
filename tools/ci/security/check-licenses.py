#!/usr/bin/env python3
"""Check the licenses of shipped third-party components against tools/ci/security/license-policy.json.

Usage: check-licenses.py [--policy FILE] [--report FILE] SBOM.cdx.json [SBOM.cdx.json ...]

Inputs are CycloneDX JSON SBOMs that contain only shipped (linked/bundled) components. A component passes when its
license, or its SPDX expression, is satisfied by the 'allowed' list ('A OR B' needs one allowed side, 'A AND B' both,
several license entries all). Unknown licenses fail. A 'denied' license (GPL/AGPL family and similar) fails even
when an exception names the component. Writes a Markdown license report to --report and $GITHUB_STEP_SUMMARY.
"""
import argparse
import datetime as dt
import fnmatch
import json
import os
import re
import sys

COPYLEFT_NAME = re.compile(r"affero|(?<!lesser )(?<!library )general public license|\bA?GPL\b", re.IGNORECASE)


class Policy:
    def __init__(self, path, today):
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        self.allowed = {x.lower() for x in data.get("allowed", [])}
        self.denied = [x.lower() for x in data.get("denied", [])]
        self.errors = []
        self.exceptions = []
        for i, e in enumerate(data.get("exceptions", [])):
            missing = [k for k in ("component", "reason", "approvedBy") if not str(e.get(k, "")).strip()]
            if missing:
                self.errors.append(f"license exception #{i + 1} is missing {', '.join(missing)}")
            elif e.get("expires") and dt.date.fromisoformat(e["expires"]) < today:
                self.errors.append(f"license exception for {e['component']} expired on {e['expires']}")
            else:
                self.exceptions.append(e)

    def is_denied(self, lic):
        lic = lic.lower()
        return any(fnmatch.fnmatchcase(lic, pat) for pat in self.denied)

    def exception_for(self, purl):
        base = purl.split("?")[0]
        for e in self.exceptions:
            c = e["component"]
            if base == c or base.split("@")[0] == c:
                return e
        return None


def tokenize(expr):
    return re.findall(r"\(|\)|[^\s()]+", expr)


def evaluate(expr, policy):
    """Return (allowed, denied_terms) for an SPDX license expression."""
    tokens = tokenize(expr)
    pos = 0
    denied = []

    def term():
        nonlocal pos
        if pos < len(tokens) and tokens[pos] == "(":
            pos += 1
            v = disjunction()
            pos += 1  # ')'
            return v
        lic = tokens[pos]
        pos += 1
        if pos + 1 < len(tokens) and tokens[pos].upper() == "WITH":
            lic = f"{lic} WITH {tokens[pos + 1]}"
            pos += 2
        if policy.is_denied(lic.split(" WITH ")[0]):
            denied.append(lic)
        return lic.lower() in policy.allowed

    def conjunction():
        nonlocal pos
        v = term()
        while pos < len(tokens) and tokens[pos].upper() == "AND":
            pos += 1
            v = term() and v
        return v

    def disjunction():
        nonlocal pos
        v = conjunction()
        while pos < len(tokens) and tokens[pos].upper() == "OR":
            pos += 1
            v = conjunction() or v
        return v

    ok = disjunction()
    # A denied term only blocks when no allowed alternative exists ('GPL-2.0 OR MIT' may be used under MIT).
    return ok, ([] if ok else denied)


def license_terms(component):
    for entry in component.get("licenses", []) or []:
        if "expression" in entry:
            yield "expression", entry["expression"]
        else:
            lic = entry.get("license", {})
            if lic.get("id"):
                yield "id", lic["id"]
            elif lic.get("name"):
                yield "name", lic["name"]
            elif lic.get("url"):
                yield "url", lic["url"]


def check_component(component, policy):
    terms = list(license_terms(component))
    if not terms:
        return "unknown", "no license declared", False
    shown, ok_all, denied = [], True, []
    for kind, value in terms:
        shown.append(value)
        if kind in ("id", "expression"):
            ok, d = evaluate(value, policy)
            denied += d
        elif kind == "name":
            ok = value.lower() in policy.allowed
            if COPYLEFT_NAME.search(value):
                denied.append(value)
        else:
            ok = False  # license URL only: needs a human to classify it and add an exception
        ok_all = ok_all and ok
    label = " AND ".join(shown) if len(shown) > 1 else shown[0]
    if denied:
        return label, f"denied license ({', '.join(denied)})", False
    if ok_all:
        return label, "allowed", True
    return label, "not on allow-list", False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--policy", default=os.path.join(os.path.dirname(__file__), "license-policy.json"))
    ap.add_argument("--report")
    ap.add_argument("--today", help="override the current date (tests)")
    ap.add_argument("sboms", nargs="+")
    args = ap.parse_args()
    today = dt.date.fromisoformat(args.today) if args.today else dt.datetime.now(dt.timezone.utc).date()
    policy = Policy(args.policy, today)
    errors = list(policy.errors)

    lines = ["## Third-party licenses (shipped components)", ""]
    for path in args.sboms:
        with open(path, encoding="utf-8") as f:
            bom = json.load(f)
        lines += [f"### {os.path.basename(path)}", "", "| Component | Version | License | Status |", "|---|---|---|---|"]
        components = sorted(bom.get("components", []), key=lambda c: "/".join(filter(None, (c.get("group"), c.get("name")))).lower())
        for c in components:
            name = "/".join(x for x in (c.get("group"), c.get("name")) if x)
            purl = c.get("purl", name)
            label, status, ok = check_component(c, policy)
            if not ok and not status.startswith("denied"):
                exc = policy.exception_for(purl)
                if exc:
                    ok, status = True, f"exception: {exc['reason']} ({exc['approvedBy']})"
            if not ok:
                status = f"**{status}**"
                errors.append(f"{purl}: {label} — {status.strip('*')}")
            lines.append(f"| {name} | {c.get('version', '')} | {label} | {status} |")
        lines += ["", f"{len(components)} components.", ""]
    if errors:
        lines += ["### Failures", *[f"- {e}" for e in errors]]
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
