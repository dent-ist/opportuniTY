# Compliance reports: ethical-wall proof, access review, audit export

Evidence for counsel, auditors and the operator's own control reviews (E14-T06, #120; legal finding 12, SEC-13). Every
report is generated on request from the live database, under the caller's permissions, and **its generation is
audited** before anything is returned. How the reports support SOC 2 and ISO 27001 controls is in
[control-mapping.md](control-mapping.md).

| Report | API (`/api/v1/workspaces/{ws}`) | Permission | Audit event |
|---|---|---|---|
| Ethical-wall proof (JSON) | `GET /compliance/wall-proof?from=&to=` | `Compliance.Report` | `Security.WallProofGenerated` |
| Ethical-wall proof (CSV) | `GET /compliance/wall-proof/export?from=&to=` | `Compliance.Report` | `Security.WallProofGenerated` |
| Access review (JSON) | `GET /compliance/access-review` | `Compliance.Report` | `Security.AccessReviewGenerated` |
| Access review (CSV) | `GET /compliance/access-review/export` | `Compliance.Report` | `Security.AccessReviewGenerated` |
| Audit export (JSON Lines) | `GET /compliance/audit-export?from=&to=` | `Audit.Export` | `Audit.Exported` |

`Compliance.Report` and `Audit.Export` are held by **Workspace Admin** and **Auditor** (see the
[permission matrix](permission-matrix.md)); break-glass never grants them. The CSV and JSON Lines downloads go through the
protected-content gateway (no caching, attachment only). Times are UTC; `from` is inclusive and `to` exclusive (ISO
8601). The reports name documents by **document ID only**, like the audit trail itself (ADR-013 §7): no control numbers,
file names or other metadata, so a report can be handed to someone who may not see the documents.

## Ethical-wall proof

*Question answered:* "In this period, did anyone behind an ethical wall reach a document the wall covers?" The expected
answer is **no: every attempt was denied**.

The report lists every audited action (`Document.*` retrievals and views, coding, any other event on a document) in the
period where

- the actor is a member of a wall that covers the document, directly or through an IdP group the identity provider last
  reported for them, and the action happened **after** they were added to that wall; or
- the action was refused because of a wall (reason `EthicalWall`), whoever the actor is now.

Each attempt is classified:

| Result | Meaning |
|---|---|
| `Denied` | the wall held (the request was answered like a missing document) |
| `BreakGlass` | read access under an active break-glass activation (Q-45: reason, MFA, at most 4 h, read only) |
| `Allowed` | **the wall did not hold**: walled content was served on the normal path |
| `Failed` | the action failed for another reason; nothing was served |

The report **held** when there is no `Allowed` attempt. Breaches are listed first, so a truncated list (500 attempts in
JSON, 50,000 in CSV) never hides one; the counts always cover every attempt. The CSV starts with a short header block
(workspace, period, result, counts, each wall with its members and covered document count, and the wall changes of the
period), followed by one row per attempt with the event ID and correlation ID, so every row can be traced back to the
audit trail and, through an [audit export](#audit-export), to the signed hash chain.

What it does **not** prove, and how to cover it:

- Walls are evaluated **as they are now**: wall scope and membership keep no history. Changes during the period
  (`Security.Wall*` events) are listed in the report; read the period in the light of them, or report sub-periods
  between changes.
- Group membership comes from the groups the IdP last reported for each user at sign-in. A user who left the group
  before the report was generated, but was in it during the period, is caught only by the `EthicalWall` refusals.
- Searches exclude walled documents inside the query (ADR-015 D8.1), so a walled user's search never reaches a walled
  document and leaves no per-document attempt; the search itself is audited as `Search.Executed`.

## Access review

*Question answered:* "Who can use this workspace, with which roles, who is walled off from what, and who has not used
their access?" One row per user and per IdP group:

- **users**: everyone with a role (directly or through an assigned group the IdP last reported for them) or behind an
  ethical wall; their roles with the source (`direct` or `via group …`) and assignment date, their walls with the source,
  the **last workspace access** (the newest successful audited action in this workspace) and the **last sign-in**;
- **groups**: every group with a role or a wall, with the number of known members.

Use it for the periodic access review (SOC 2 CC6.2/CC6.3, ISO 27001 A.5.18): revoke roles of people who no longer need
them (Admin › Users & Groups) and check that the walls name the right people. Users the IdP never reported in a group
cannot be listed under it; review group membership in the IdP as well.

## Audit export

A verifiable copy of the workspace's audit trail: a JSON Lines file with a manifest, the installation's checkpoint public
keys, the signed checkpoints, any purge records, and every sealed event as its canonical envelope with its hash. It
covers the checkpoint-aligned part of the hash chain around the requested period (without `from`/`to`: the whole chain),
so it verifies on its own, without the database:

```sh
# Public key obtained independently from the operator (see docs/operations/audit-chain.md)
docker compose run --rm --no-deps worker keys public-key --purpose audit-checkpoint --version 1 > audit-checkpoint-v1.pem

docker compose run --rm --no-deps -v "$PWD:/work:ro" worker \
  audit verify-export --file /work/audit-export-<ws>-<time>.jsonl --public-key /work/audit-checkpoint-v1.pem
```

The manifest records the verification result at generation; `verify-export` repeats it and reports any change to the
file. Search text (Q-16) is included only for callers who also hold `Audit.ReadSearchText`; otherwise those events are
marked *withheld* and verified through the chain links and Merkle roots. Events newer than the last checkpoint are
linked but not yet signed: take a checkpoint (`audit seal --checkpoint`) before exporting for an exhibit. The format
and the guarantees are described in [audit-chain.md](../operations/audit-chain.md#audit-exports).
