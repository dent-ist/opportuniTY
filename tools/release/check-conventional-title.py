#!/usr/bin/env python3
"""Check that a pull-request title is a Conventional Commits header (E01-T06).

Usage: check-conventional-title.py [TITLE]     (default: the TITLE environment variable)

Pull requests are squash-merged, so the title becomes the commit on main that release-please reads to pick the next
version and write the changelog: `feat` bumps the minor version; `fix`, `perf`, `deps`, `docs` and `revert` the patch
version; `!` or a `BREAKING CHANGE:` footer the major version (the minor version before 1.0). The hidden types
(refactor, test, build, ci, chore, style) alone do not make a release. See docs/releasing.md.
"""
import os
import re
import sys

TYPES = ("feat", "fix", "perf", "revert", "deps", "docs", "refactor", "test", "build", "ci", "chore", "style")
HEADER = re.compile(r"^(?P<type>[a-z]+)(?:\((?P<scope>[a-z0-9][a-z0-9._/-]*)\))?(?P<breaking>!)?: (?P<subject>\S.*)$")
MAX_LENGTH = 100


def problems(title):
    title = title.strip()
    m = HEADER.match(title)
    if not m:
        return ["must look like 'type(scope): subject' or 'type: subject', e.g. 'feat(review): add batch check-in'"]
    found = []
    if m.group("type") not in TYPES:
        found.append(f"type '{m.group('type')}' is not one of: {', '.join(TYPES)}")
    if len(title) > MAX_LENGTH:
        found.append(f"is {len(title)} characters long; keep it to {MAX_LENGTH}")
    if m.group("subject").endswith("."):
        found.append("subject must not end with a period")
    return found


def main():
    title = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("TITLE", "")
    found = problems(title)
    if found:
        for p in found:
            print(f"::error title=Pull-request title is not a Conventional Commit::'{title}' {p}")
        print("The title becomes the squash commit that release-please turns into the version bump and changelog "
              "(docs/releasing.md#conventional-commits).")
        return 1
    print(f"OK: '{title}'")
    return 0


if __name__ == "__main__":
    sys.exit(main())
