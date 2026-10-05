# ADR-009: Family, dedupe and thread identity, and date/time-zone policy

| Field | Value |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-10-02 |
| **Owner (role)** | Data (PostgreSQL) |
| **Deciders** | Lead architect, product owner (Q-09, Q-27, Q-28); contributing: eDiscovery Practitioner, UI/UX |
| **Tracking issue** | #30 (plan key `E02-T04`) |
| **Baseline sections** | [§5](../architecture/architecture-baseline.md#5-core-domain-and-document-model), §9, [§12](../architecture/architecture-baseline.md#12-minimal-ingestion), §14, §23, §32, §34 |
| **Related** | ADR-002 (snapshots), ADR-003 (field model), ADR-005, ADR-007, ADR-008; Q-09, Q-14, Q-27, Q-28; review findings §8.7–§8.11 |

## Context

§5 and §34 put families, duplicate groups and thread IDs in the model from day one and leave dedupe policy
configurable. The eDiscovery review lists what real loads need: workspace-unique, normalized control numbers;
received Bates separate from internal identity; three family encodings (range, pointer, group) with orphans and
overlaps; family-level dedupe with upstream email hashes; upstream thread IDs; and a date policy covering many input
formats, source time zones and a derived `documentDate`. Binding decisions: **Q-27** (ControlNumber immutable, unique on
a normalized form, optional import prefix, received Bates as typed fields), **Q-09** (honour upstream dedupe; optional
computed grouping is family-level, global, upstream hash else SHA-256; label, never suppress), **Q-14** (no automatic
family coding inheritance) and **Q-28** (matter display zone with per-user override; productions use their spec's
zone; en-US/en-GB). Families, duplicates and dates feed snapshot expansion (ADR-002), production and privilege logs, so
they must be **deterministic**: the same input set gives the same result regardless of load order or chunking.

## Decision

### 1. Document identity and ControlNumber (Q-27)

1. **R1** `DocumentId` is a platform-generated UUIDv7. It is the only id used in foreign keys, search documents,
   object keys and messages. It is never shown as a business identifier.
2. **R2** `ControlNumber` is the business identity. Normalization (`ControlNumberNorm`), applied after the optional
   per-import prefix is prepended:
   1. Unicode NFC; trim leading and trailing whitespace (including U+00A0 and U+FEFF);
   2. collapse internal runs of whitespace to one space; reject if empty, longer than 255 chars, or containing
      control characters (other punctuation is allowed; the query language escapes it, ADR-008);
   3. case-fold with `ToUpperInvariant` unless the workspace setting `ControlNumberCaseSensitive` is true.
      **Default: case-insensitive.** The setting is fixed once the workspace holds a document.
3. **R3** Unique `(WorkspaceId, ControlNumberNorm)`. The stored `ControlNumber` keeps the first-seen spelling.
4. **R4** **Immutable.** No API renames a control number. A `BEFORE UPDATE` trigger rejects changes to
   `ControlNumber`/`ControlNumberNorm`. A control number whose document is removed is retired
   (`RetiredControlNumber`) and cannot be reused, except when an import batch is rolled back before any coding,
   redaction, export or production touched its documents.
5. **R5** Natural sort: `ControlNumberSortKey` = `ControlNumberNorm` with every maximal digit run left-padded with
   zeros to 20 digits (runs longer than 20 are kept as is), compared with `COLLATE "C"`. So `ABC9` < `ABC10` <
   `ABC0011`. Every sort uses `DocumentId` as the final tie-breaker. The same key is projected for search sorting
   (ADR-007).
6. **R6** Received `BegBates`/`EndBates` and `BegAttach`/`EndAttach` are typed structural columns (ADR-003), normalized
   like R2 for comparison but **not** unique-constrained (received productions contain errors). The import report warns
   on duplicates and checks the range page count against OPT pages when both share a prefix. Bates numbers **we**
   assign belong to production records (`E12-T03`), never to `Document`.

### 2. Family reconstruction

Family source values are kept as imported (system fields `BegAttach`, `EndAttach`, `ParentIdRaw`, `AttachmentIdsRaw`,
`GroupIdentifierRaw`) so resolution can always be re-run. Resolution is a workspace-scoped job (`FamilyResolution`)
that runs after each import and after any overlay touching these fields. It is a **pure function of the workspace's
current documents**, not of load order.

| Mode | Input | Members | Parent |
|---|---|---|---|
| A — Range | `BegAttach`/`EndAttach` on every member | Docs whose `ControlNumberSortKey` is within [key(BegAttach), key(EndAttach)] **and** whose non-numeric prefix equals BegAttach's | Doc with `ControlNumberNorm = norm(BegAttach)` |
| B — Pointer | `ParentID` on children; `AttachmentIDs` (multi) on parents | Transitive closure of parent pointers | Doc with no parent pointer at the root; `AttachmentIDs` is cross-validated only |
| C — Group | Shared `GroupIdentifier`/`FamilyID` | Docs with an equal value | Doc with blank `ParentID` if exactly one exists, else lowest `ControlNumberSortKey` |

Rules:

1. **R7** `FamilyId` = `DocumentId` of the top-level parent. A standalone document is a family of one
   (`FamilyId = DocumentId`, `FamilySequence = 0`), so expansion and family sort need no special case.
2. **R8** `FamilySequence`: 0 for the parent, then 1..n over the other members in `ControlNumberSortKey` order. Nested
   attachments are flattened into the top-level family, and `ParentDocumentId` points to the **immediate** parent (from
   `ParentID` when present, otherwise the top-level parent).
3. **R9** Mode is chosen per import mapping (the preview suggests one). If a document has sources from several modes
   (e.g. a later overlay), precedence is **pointer > group > range**.
4. **R10** Conflicts are resolved deterministically **and** flagged (`FamilyStatus`), never silently:

| Condition | Resolution | `FamilyStatus` / report |
|---|---|---|
| Document claimed by two families (overlapping ranges, two groups) | Joins the candidate family whose parent has the lowest `ControlNumberSortKey` | `Conflict`; listed with both claims |
| Range whose parent (BegAttach) is not in the workspace | Members found form a provisional family; `FamilyId` = lowest-key member | `ParentMissing`; re-resolved when the parent arrives |
| Range spanning control numbers not in the workspace | Family built from present docs | `Gap` (warning) with missing count |
| Range with different prefixes in BegAttach/EndAttach | Range ignored; doc standalone | `InvalidRange` (error row in report) |
| Pointer cycle | Lowest-key doc in the cycle becomes the parent | `Conflict` |
| Pointer to unknown ControlNumber | Standalone, pointer kept | `ParentMissing` |

5. **R11** A changed `FamilyId`, `ParentDocumentId`, `FamilySequence` or `FamilyDate` bumps `DocumentVersion` and is
   indexed via `IndexChunkTask(kind=Family)`. Families spanning import chunks or volumes resolve in the post-pass, not
   per chunk.
6. **R12** Family membership never carries coding (Q-14). "Apply to family" is an explicit action (`E09-T05`).

### 3. Duplicates (Q-09)

1. **R13** **Upstream first.** A mapped upstream duplicate group value sets `DuplicateGroupId =
   UUIDv5(ns, WorkspaceId ‖ "upstream" ‖ value)`. `AllCustodians`, `DuplicateCustodians`, `AllPaths` and
   `DuplicatePaths` are imported verbatim as multi-value Keyword system fields and never recomputed.
2. **R14** **Optional computed grouping** (admin-run job, `E09-T04`) only touches documents **without** an upstream
   group, which are never rewritten. It is **family-level**: only top-level parents (and standalone documents) are
   compared. The key is the parent's upstream `DedupeHash`/email hash when present, otherwise the SHA-256 of its native.
   Documents with neither are not grouped. Scope is **global** across the workspace. Attachments inherit their parent's
   group, so a group is a set of families. `DuplicateGroupId = UUIDv5(ns, WorkspaceId ‖ hashKind ‖ hashValue)`, which
   is stable across re-runs and re-imports.
3. **R15** `DuplicateGroup(WorkspaceId, DuplicateGroupId, Source {Upstream, Computed}, HashKind {UpstreamGroup,
   UpstreamDedupeHash, UpstreamEmailHash, Sha256Native}, HashValue, PrimaryFamilyId, MemberCount)` records **which
   hash drove grouping**. Primary: earliest `FamilyDate`, then lowest `ControlNumberSortKey`
   (`IsDuplicatePrimary` on the parent and its members).
4. **R16** **Label, never suppress.** Duplicates appear in search, grid, review and production like any document.
   They are labelled (primary flag, group size, link to members) and nothing is hidden or auto-coded.
   Propagate-to-duplicates and suppression are P2.
5. **R17** Hashes are stored as `bytea` (MD5/SHA-1/SHA-256) and upstream hashes as lower-case hex text. A DAT hash that
   disagrees with the computed native hash is an import warning (`E08-T04`), and the computed value wins in the column.

### 4. Email threads

1. **R18** `EmailThreadId = UUIDv5(ns, WorkspaceId ‖ "thread" ‖ upstreamThreadGroup)` from a mapped upstream thread
   field (Relativity Email Thread Group, Nuix thread id). No threading engine in the MVP.
2. **R19** Supporting system fields: `ConversationIndex` (Keyword, upper-case hex), `ConversationTopic` (Text),
   `InclusiveEmail` (Boolean), `ThreadSortOrder` (Keyword, sortable). An import option (off by default) derives
   `EmailThreadId` from the first 22 bytes of `ConversationIndex` when no upstream group exists. The source used is
   recorded (`EmailThreadSource`).
3. **R20** Attachments carry no `EmailThreadId`; thread expansion composes with family expansion (`E09-T03`).

### 5. Dates and time zones (Q-28)

1. **R21** **Storage.** `DateTime` values are UTC instants (`timestamptz`; ISO `…Z` in JSONB). `Date`-precision values
   are calendar dates and are **never** time-zone converted. The original string, format and source zone are always
   kept (`MetadataRaw`, ADR-003 R9).
2. **R22** **Parsing.** Each import declares an ordered list of accepted formats per date field (`MM/dd/yyyy`,
   `dd/MM/yyyy`, `yyyy-MM-dd`, `yyyyMMdd`, ISO 8601 with offset; optional companion time column `HH:mm:ss` /
   `hh:mm tt`). It also declares a **source time zone** (IANA id), which is required when any DateTime field is mapped
   and values lack an offset. An explicit offset in a value wins over the source zone. Formats that are ambiguous
   against each other (e.g. both `MM/dd` and `dd/MM`) cannot be listed together. Two-digit years are rejected unless
   the format names a pivot.
3. **R23** **DST.** An ambiguous local time takes the earlier offset; a non-existent local time moves forward by the
   gap. Both are counted as warnings in the import report.
4. **R24** **Date-only input to a DateTime field** (`DateSent = 03/01/2025`, no time): stored as 00:00 in the source
   zone with `dateOnly: true` in raw. The UI and productions render such values as the source calendar date, without
   shifting, and show no time.
5. **R25** **Derived dates** (structural, recomputed on any input change):
   - `DocumentDate` = first non-null of `DateSent` → `DateReceived` (emails) → `DateLastModified` → `DateCreated`;
     `DocumentDateSource` records which one. An upstream `SortDate`/`DocumentDate` mapping, when present, is used as
     is (`Source = Upstream`).
   - `FamilyDate` = the top-level parent's `DocumentDate` (upstream `FamilyDate` when mapped). The default grid sort is
     `FamilyDate`, `FamilyId`, `FamilySequence`, which keeps families contiguous.
6. **R26** **Display.** `Workspace.DisplayTimeZone` (IANA, required at creation) with an optional per-user override.
   The API always returns UTC ISO 8601 plus `dateOnly` where relevant, and the client formats. MVP locales: en-US
   (`MM/dd/yyyy`, 12 h) and en-GB (`dd/MM/yyyy`, 24 h).
7. **R27** **Search.** Date literals in queries are interpreted in the executing user's effective display zone. A saved
   search, snapshot or search term report stores the zone it was run with, so it re-executes identically (ADR-008).
8. **R28** **Productions and privilege logs** render dates in the zone and format of the production specification
   (Q-28, Q-08), recorded in its frozen spec.

### 6. Expansion (E09-T03, #86) — implementation and interim budget (2026-10-05)

1. **R29 One definition.** `Opportunity.Core.Documents.RelationshipExpansion` (family, duplicates, thread) defines the
   steps: from the base set (hits, explicit IDs or a source snapshot's members) add (1) the base's families, (2) its
   duplicate groups, (3) its email threads, then (4) the families of what (2) and (3) added (R20: expanded emails bring
   their attachments). Each step adds only documents not already present, so a document keeps its first reason
   (family, duplicate, thread). Two implementations follow it: `Opportunity.Search.Querying.RelationshipExpansionQuery`
   (interactive search, over the projection under the page's reader) and `Opportunity.Data.Relationships.
   RelationshipExpansionSql` (snapshot freeze, over the authoritative PostgreSQL columns, before membership is fixed —
   ADR-002 §5.2.1). Other PostgreSQL set computations (e.g. a search-term report's "with family" counts) should reuse the
   latter with their own seed table.
2. **R30 Security.** Expanded members are authorized exactly like hits: the outer security filter and the Q-12 page
   post-filter for search, the freeze's per-member authorization for snapshots. Hidden members are omitted without a
   trace and are not counted (Q-11, Q-13, Q-52); keys are read only from documents the person may see.
3. **R31 API.** `expand { family, duplicates, thread }` on `POST …/searches` and `POST …/snapshots`; saved searches store
   `includeFamily`, `includeDuplicates`, `includeThread` and a run or freeze applies them unless it gives its own. A
   result page keeps `total` = base hits and adds `expand` and `expanded { family, duplicates, thread, total }`; each
   added row has `expandedBy` = `family` | `duplicate` | `thread`. An expanded search defaults to the `familyDate` sort
   (family date, family, family sequence: families contiguous, parent first), which is also a grid sort option.
4. **R32 Bounds.** An interactive expansion is limited to `OpenSearch:Search:MaxExpansionKeys` (250,000) keys of each
   kind, because every page sends the keys to OpenSearch; above it the search answers 400 on `expand` and the set is
   frozen instead (snapshots expand in PostgreSQL without that bound).

**Interim budget (Q-70: CI-class container, not reference hardware).** `tests/Opportunity.IntegrationTests/Search/
RelationshipExpansionBenchmark.cs` (`OPPORTUNITY_EXPANSION_BENCHMARK=1`), 100,000 documents in 25,000 families of
1 parent + 3 attachments, "Include family", 4 vCPU shared with other test runs:

| Query | Base hits | Expanded | First page (keys + page) | Next page | Snapshot without expansion | Snapshot with family |
|---|---|---|---|---|---|---|
| narrow (10% of families, parents) | 2,500 | 10,000 | 1.33 s | 0.36 s | 0.79 s | 1.89 s |
| broad (every family, first attachment) | 25,000 | 100,000 | 0.99 s | 1.02 s | 4.27 s | 14.57 s |

Recorded budget until the reference run (#149): interactive expanded pages ≤ 2 s and an expanded freeze ≤ 0.2 ms per
member at CI scale. The freeze time grows with the members authorized (the PDP pass over 100,000 members dominates),
not with the expansion SQL; 1M on reference hardware is open together with ADR-002 §6.1.

## Consequences

- **Positive:** identity is stable and collision-safe. Family, duplicate and thread ids are reproducible from the
  stored inputs, so a rebuild from PostgreSQL or a re-import gives identical ids. Conflicts surface as reportable
  statuses, not silent guesses. Date handling is explicit and auditable back to the raw string.
- **Negative / costs:** case-insensitive normalization rejects loads whose control numbers differ only by case (rare,
  reported). Family resolution is a separate post-pass job, so families are briefly incomplete after a chunk commits.
  Upstream and computed duplicate groups can coexist within one workspace.
- **Follow-up work:** `E04-T02` (columns, trigger, sort key), `E08-T02` (date parsing), `E09-T01` (families),
  `E09-T02` (upstream dup/thread), `E09-T03` (expansion), `E09-T04` (computed dedupe), `E15-T05` (locale display).
- **Verification:** golden tests per family mode and per conflict row above; a property test that family resolution
  gives identical results for any permutation of load order and chunk size; generator ground truth on the 1:3 family
  and 20%-duplicate corpora (`E17-T01`); DST fixtures for `America/New_York` and `Europe/London`.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Case-sensitive ControlNumber by default | Vendors mix case across volumes; a case-only difference is almost always an error. A per-workspace opt-in remains. |
| Renaming control numbers | Rejected by Q-27: breaks audit, productions and opposing-counsel references. |
| FamilyId as a new random UUID | Not reproducible on rebuild or re-import; a parent's DocumentId is already a stable family id. |
| Document-level (per-attachment) dedupe | Breaks families: an attachment suppressed in one family leaves another incomplete. Q-09 requires family level. |
| Custodial dedupe scope in MVP | Q-09 decides global scope. `E09-T04` mentions "Global or Custodial"; custodial is deferred to P2 (see below). |
| Converting date-only values to UTC midnight | Shifts the calendar date by one day for users west of UTC, a known source of privilege-log errors. |

## Baseline amendments

- *Proposed:* §5 — `FamilyStatus`, `DocumentDateSource`, `FamilyDate`, `DuplicateGroup` (with `Source`/`HashKind`)
  and `EmailThreadSource` join the model. Dedupe policy for the MVP is "honour upstream, optional global family-level,
  label only" (Q-09).
- **Product-owner note:** `E09-T04` lists a Custodial scope option. Under Q-09 this ADR ships Global only and defers
  Custodial. Confirm or amend `E09-T04`.

## Links

- Baseline: §5, §9, §12, §14, §23, §32, §34
- Review findings: [review-findings.md](../plan/review-findings.md) §8.7–§8.11, §13.11
- Decisions: [decisions.md](../plan/decisions.md) Q-08, Q-09, Q-14, Q-27, Q-28
- Related ADRs: [ADR-003](0003-metadata-and-custom-field-model.md), [ADR-005](0005-postgresql-partitioning.md), [ADR-008](0008-minimal-query-language.md)
