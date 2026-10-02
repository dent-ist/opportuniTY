# ADR-NNN: <Short title in imperative or noun form>

<!--
Copy this file to docs/adr/NNNN-kebab-case-title.md (ADR-004a/004b use 0004a-/0004b-).
Keep it decisive: one decision per ADR, ~1-4 pages. Delete these comments.
-->

| Field | Value |
|---|---|
| **Status** | Proposed · Accepted · Spike pending · Superseded by ADR-NNN · Deprecated · Rejected |
| **Date** | YYYY-MM-DD (date of the last status change) |
| **Owner (role)** | e.g. Backend · Data (PostgreSQL) · Search (OpenSearch) · Security & Compliance · DevOps / SRE · UI/UX |
| **Deciders** | Roles/people who must approve (lead architect always; product owner when a decision in `docs/plan/decisions.md` is affected) |
| **Tracking issue** | #NN (plan key `ENN-TNN`) |
| **Baseline sections** | §N, §N (links into [architecture-baseline.md](../architecture/architecture-baseline.md)) |
| **Related** | ADR-NNN, review finding IDs (A-NN), product decisions (Q-NN) |

## Context

What forces are at play: the baseline text this ADR elaborates or amends, the review findings, product-owner decisions,
constraints (scale targets §17, security rules §24) and what goes wrong if nothing is decided. Facts, not advocacy.

## Decision

The decision, stated normatively ("MUST", "MUST NOT", "SHOULD" per RFC 2119). Prefer numbered rules, tables and
diagrams that tests and reviewers can check. Give initial numeric values where a number is needed and say which ticket
tunes them.

## Consequences

- **Positive:** what becomes easier or safer.
- **Negative / costs:** what becomes harder, slower or more expensive; accepted risks.
- **Follow-up work:** tickets that implement or verify the decision (plan keys / issue numbers).
- **Verification:** how compliance is enforced (architecture test, contract test, benchmark gate, review checklist).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Option A | … |
| Option B | … |

## Baseline amendments

Changes this ADR proposes to the normative baseline (§35), each marked *Proposed* until the lead architect and product
owner sign off. The baseline document is edited only after sign-off. Write "None" if the ADR only elaborates the baseline.

## Links

- Baseline: §N …
- Review findings: [review-findings.md](../plan/review-findings.md) A-NN
- Decisions: [decisions.md](../plan/decisions.md) Q-NN
- Spike / benchmark results (if any)
