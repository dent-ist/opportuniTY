# Defensibility reports and the report pack

Reports counsel can rely on when review or production is challenged (E14-T05, #119; legal finding 10, baseline §22
and §28). They are built from what opportuniTY already records (the coding history, the production manifests and QC
reports frozen at finalization, the search term reports, the import and storage registries) rather than re-derived, and
they sit next to the [compliance reports](compliance-reports.md) (wall proof, access review, verifiable audit export).
Admin › Defensibility Reports in the web app; `Compliance.Report` (Workspace Admin, Auditor).

| Report | API (`/api/v1/workspaces/{ws}`) | Forms |
|---|---|---|
| Review population and coding by reviewer and date | `GET /defensibility/review-summary?asOf=&snapshotId=` | JSON, `/export` CSV |
| Production history | `GET /defensibility/production-history?asOf=` | JSON, `/export` CSV |
| Search term hit reports | `GET /defensibility/search-term-hits?asOf=` | JSON, `/export` CSV |
| Chain of custody of one document | `GET /documents/{id}/chain-of-custody?asOf=` | JSON, `/export` CSV |
| Report pack | `GET /defensibility/pack?asOf=&from=&snapshotId=&documentId=…` | ZIP |

Every generation writes `Audit.DefensibilityReportGenerated` (report, format, as-of point, snapshot, fingerprint)
before anything is returned. CSV, the chain of custody and the pack go through the protected-content gateway.

## As-of point, fingerprint and reproducibility

Each report states its **data-as-of point** (`statement.asOf`, UTC, microseconds): the requested `asOf`, or the moment
it was generated. A report reads only what was recorded at or before that point — coding events, imports, stored objects,
page sets and overlays by their time, productions by finalization (a void after the point is not applied), volume runs
and QC runs by completion — so **generating it again with the same `asOf` gives the same report**. The statement also
carries:

- `fingerprint`: SHA-256 of the report's canonical as-of content (identifiers, counts, hashes and times). Display names
  (they can change) and re-verification results (they are taken now) are not part of it. Two reports for the same point
  have the same fingerprint; a pack's fingerprint covers the fingerprints of the reports it holds.
- `snapshotId` when a frozen set scopes the report (the review summary's population), and `searchGeneration`, the
  workspace's search generation counter, when the report is as of now.
- `auditEventId`, the generation's audit event, which an [audit export](compliance-reports.md#audit-export) ties to the
  signed hash chain.

To reproduce a report, generate it again with the `asOf` its statement shows (the CSV prints it in ISO 8601). A point in
the future is refused. Limits: documents removed by a defensible deletion (ADR-014) are no longer counted; transactions
that were still in flight at the point are counted once they commit, so a point a few seconds in the past is the safest
choice for a report that must be reproduced.

## The reports

**Review population and coding.** The population is every document the workspace held at the point, or the members of a
Ready snapshot (`snapshotId`). *Reviewed* documents have at least one value change by a person (interactive coding, or a
mass edit they started) at or before the point; changes made only by system rules (propagation) are counted separately.
Coding is summarised per reviewer (documents changed, field changes by kind, first and last change) and per reviewer and
UTC day (at most 20,000 day rows in JSON and CSV; totals cover all). Counts cover every document, whoever can see it now;
reviewers are named by user ID and display name.

**Production history.** Every production finalized by the point: version, status as of the point, frozen set, finalizer,
first and last Bates number, documents and pages, the SHA-256 of its specification, its Bates assignments and its
finalization manifest, the finalization's QC result (each check's status and document count, and the SHA-256 of the
canonical QC report), and its completed volumes (counts, bytes, volume manifest SHA-256, burn-in verification).

**Search term hit reports.** Every Search Terms Report whose current run completed by the point, tied to the frozen set
(snapshot) and search generation it was counted over, whether the index was current during the run, and the **index lag
at execution**: the committed changes search did not reflect yet when the run started and the age of the oldest
(recorded for runs from V0066 on; earlier runs show none). The Search Terms Report's own CSV/XLSX shows the same lag.
A rerun replaces a report's counts, so the report as of an earlier point lists only runs that completed by then.

**Chain of custody.** For one document the caller may view (others answer 404, as a missing document): its identity and
the native's MD5, SHA-1 and SHA-256 computed while it was imported, the hash its load file supplied, each import that
created or changed it (import name, mode, load file name, the load file's SHA-256 recorded at upload, row number and
result), every stored object of the document (native, text, imported images, renditions; area, size, time, job) with
**the SHA-256 recorded when it was stored next to the SHA-256 of the bytes read back from object storage now**, its page
sets (imported or rendered, renderer and version, settings hash), overlays and the productions it was produced in. The
native and text are re-read first, then load files, then other objects, at most 100 per report; a mismatch or a missing
object is shown as such and raises `Integrity.HashMismatch` (ADR-011 §2.5).

## The pack

One ZIP as of one point: `review-summary.csv`, `production-history.csv`, `search-term-hits.csv`, one
`chain-of-custody/…csv` per `documentId` (at most 25), each produced production's `manifest.json` and `qc-report.json`
exactly as frozen at finalization, `compliance/ethical-wall-proof.csv` and `compliance/access-review.csv` for the period
`from` (default 30 days before the point) to the point and, for callers with `Audit.Export`, `audit/audit-trail.jsonl`:
the verifiable audit-chain export of that period (`audit verify-export`; search text only with `Audit.ReadSearchText`).
`manifest.json`, written last, lists every file with its size and SHA-256, every report's fingerprint, the pack's own
fingerprint and its audit event. The compliance reports and the audit export write their own audit events as usual.
