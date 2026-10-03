# ADR-003: Metadata and custom-field model

| Field | Value |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-10-02 |
| **Owner (role)** | Data (PostgreSQL) |
| **Deciders** | Lead architect; contributing: eDiscovery Practitioner, Search (OpenSearch), UI/UX |
| **Tracking issue** | #30 (plan key `E02-T04`) |
| **Baseline sections** | [§5](../architecture/architecture-baseline.md#5-core-domain-and-document-model), [§6](../architecture/architecture-baseline.md#6-metadata-coding-and-postgresql-partitioning), §12, §23 |
| **Related** | ADR-004a (coding storage), ADR-005 (partitioning), ADR-007 (mapping), ADR-008 (query language), ADR-009 (identity and dates), ADR-019; Q-27, Q-28, Q-29, Q-31; review findings §8.1–§8.12 |

## Context

§6 rules out pure EAV at 100M-document scale and asks for typed structural columns plus `Metadata JSONB` for
imported custom metadata, with nine field types: Text, Keyword, Integer, Decimal, Date, Boolean, SingleChoice,
MultiChoice, User. It also says high-write coding stays separate from relatively static imported metadata.

The baseline leaves open: which fields are columns and which live in JSONB; how JSONB is keyed (display names make
renames rewrite 100M rows); how raw DAT strings coerce into each type; which JSONB indexes exist; how custom fields
reach OpenSearch (ADR-007) and the query language (ADR-008). The eDiscovery review adds multi-value fields
(`AllCustodians`), raw-string retention for dates, and a coercion preview at import (`E08-T02`). The UI review needs
per-field sort/filter/facet capability. `E04-T02` (schema) and `E04-T03` (field definitions) are blocked on this ADR.

## Decision

### 1. Three storage kinds behind one field catalogue

Every field a user can map, see, search or code is a `FieldDefinition` row. Its `Storage` says where values live:

| Storage | Where values live | Written by | Examples |
|---|---|---|---|
| `Column` | Typed column on `Document` | Import, family/dedupe jobs, system | ControlNumber, FileName, DateSent, Sha256 |
| `Metadata` | `Document.Metadata` JSONB | Import / overlay only | Custodian, From, To, Subject, AllCustodians, any vendor field |
| `Coding` | Coding current-state tables (ADR-004a) | Reviewers, bulk coding, audited overlay (Q-31) | Responsive, Privilege Basis, Issues |

1. **R1** A value is a **structural column** if platform logic reads it (identity, family, dedupe, thread, security,
   production, lifecycle, date derivation, natural sort) or §23 requires it. Everything else imported is `Metadata`.
   A new column needs a migration and an amendment to this table, not a runtime action.
2. **R2** Coding fields MUST NOT be stored in `Document.Metadata`. Imported metadata and coding never share a row
   version, so a review write never rewrites the large metadata tuple (§6, §27).
3. **R3** System fields are `FieldDefinition` rows seeded per workspace with **reserved ids 1–999** and
   `IsSystem = true`. They can be renamed (display name only) and hidden but not deleted or retyped. Custom field ids
   start at 1000.

Structural `Document` columns (initial set; `E04-T02` implements them):

| Group | Columns |
|---|---|
| Identity | `WorkspaceId uuid`, `DocumentId uuid` (UUIDv7), `ControlNumber text`, `ControlNumberNorm text`, `ControlNumberSortKey text COLLATE "C"`, `BegBates`, `EndBates`, `BegAttach`, `EndAttach` (received, text) — rules in ADR-009 |
| Relationships | `FamilyId`, `ParentDocumentId`, `FamilySequence int`, `FamilyStatus smallint`, `DuplicateGroupId`, `IsDuplicatePrimary bool`, `EmailThreadId` |
| Hashes | `Md5 bytea`, `Sha1 bytea`, `Sha256 bytea`, `UpstreamDedupeHash text` |
| File | `FileName`, `FileExtension`, `FileType`, `MimeType`, `FileSize bigint`, `PageCount int` |
| Dates | `DateSent`, `DateReceived`, `DateCreated`, `DateLastModified`, `DocumentDate`, `FamilyDate` (all `timestamptz`), `DocumentDateSource smallint` — ADR-009 §5 |
| Artifacts | `NativeObjectKey`, `TextObjectKey`, `TextLength bigint`, flags `TextTruncated`, `TextMissing`, `NativeMissing`, `ImagesIncomplete`, `TextEncodingWarning` |
| Values | `Metadata jsonb NOT NULL DEFAULT '{}'`, `MetadataRaw jsonb NULL` |
| Bookkeeping | `DocumentVersion bigint`, `FirstImportBatchId`, `CreatedAt`, `UpdatedAt` |

### 2. JSONB key format

1. **R4** `FieldId` is an `int` allocated from a per-workspace counter row; PK `(WorkspaceId, FieldId)`. Ids are never
   reused, including after deletion.
2. **R5** The JSONB key is `"f"` + decimal `FieldId` (e.g. `"f1017"`). Short keys matter: 30 fields × 100M rows with
   36-byte UUID keys would add ~100 GB before compression.
3. **R6** Display names, query aliases (ADR-008) and import aliases live only in `FieldDefinition`. **Renaming a field
   never touches `Document` rows.** Deleting a field is a soft delete: the definition is marked deleted, its values
   become invisible immediately, and a chunked background job removes the key from rows. The job bumps
   `DocumentVersion` and emits `IndexChunkTask(kind=Reindex)`.
4. **R7** Changing a field's type is rejected once any value exists (`E04-T03`). The supported path is "create new
   field + copy job" (post-MVP).

### 3. Canonical value representation and coercion

`Metadata` holds only **canonical** values. An absent key means "no value". JSON `null` and empty strings are never
stored. Multi-value is allowed on Text and Keyword (`IsMultiValue`) and is inherent to MultiChoice.

| Type | Canonical JSONB | Accepted import input (after trim) | Rejected / special |
|---|---|---|---|
| Text | string (array if multi) | any string; DAT newline marker (`®`) → `\n` per delimiter profile | > 100,000 chars per value → row error (configurable); control chars except `\t\n\r` stripped with warning |
| Keyword | string (array if multi), NFC-normalized, case preserved | any string; multi-value split on profile delimiter, trimmed, de-duplicated **case-insensitively** preserving first spelling, order kept | > 8,191 chars → row error (OpenSearch keyword limit is 32,766 bytes) |
| Integer | JSON number, int64 within ±(2^53−1) | `-?[0-9]+`, thousands separators `,` (en-US) or `.`/space (en-GB per import locale) removed | out of range → row error (use Keyword); decimals → row error |
| Decimal | JSON number (PG `numeric`, exact) | invariant or import-locale decimal; definition fixes precision ≤ 18 and scale ≤ 6; rounds half-even to scale | NaN/Infinity, more than 18 significant digits → row error |
| Date | string: `YYYY-MM-DD` (precision Date) or ISO 8601 UTC `YYYY-MM-DDTHH:mm:ss[.fff]Z` (precision DateTime) | per-import format list and source zone; separate time column merge (ADR-009 §5) | `00/00/0000`, `0000-00-00`, blank → absent (no error); unparseable → row error (option: absent + warning) |
| Boolean | `true` / `false` | Y/N, Yes/No, True/False, T/F, 1/0, case-insensitive | anything else → row error; blank → absent |
| SingleChoice | number: `ChoiceId` | choice name, case-insensitive, trimmed; option "create missing choices" (admin) | unknown name without create option → row error |
| MultiChoice | array of `ChoiceId`, ascending, unique | names split on profile delimiter; nested-value delimiter `\` reserved for post-MVP hierarchical choices | as SingleChoice per element |
| User | string: `UserId` (UUID) | user email or IdP subject; must be a workspace member | unknown → row error (option: absent + warning) |

4. **R8** Choice names are stored only in the `Choice` table (`(WorkspaceId, FieldId, ChoiceId)`, order, `IsActive`).
   Renaming a choice never rewrites documents. Used choices can only be deactivated.
5. **R9** When coercion changes the text of a value (any Date; Boolean `Y`→`true`; Integer `1,000`→`1000`), the
   original string is kept in `MetadataRaw` under the same key: `{"f1017": {"raw": "03/01/2025 09:05", "fmt":
   "MM/dd/yyyy HH:mm", "tz": "America/New_York", "batch": 42}}`. Raw values of structural date columns use the system
   field id as key. `MetadataRaw` is read by the viewer, overlay diffs and audit only; it is never searched.
6. **R10** Coercion is one pure function, `FieldValueCoercer.Coerce(definition, raw, importSettings) → Result`. The
   import preview (`E08-T02`), the import worker, the API write validator and overlay all call it. Golden tests cover
   every row of the table above.
7. **R11** Limits (configurable per installation): ≤ 1 MB canonical `Metadata` per document; ≤ 1,000 custom fields per
   workspace. The searchable subset is further bounded by ADR-007 slot budgets.

### 4. JSONB indexing policy

1. **R12** **No GIN index on `Metadata`** (neither `jsonb_ops` nor `jsonb_path_ops`). At 100M rows a GIN index
   amplifies every import and overlay write and would be bigger than the table. Interactive filtering, sorting and
   faceting on metadata is OpenSearch's job (§2.1, ADR-007).
2. **R13** PostgreSQL reads metadata by primary key only (viewer, projection builder, export, privilege log). Overlay
   keys are structural: `ControlNumberNorm` (default), `DocumentId` or received `BegBates`. Custom fields cannot be
   overlay keys in the MVP.
3. **R14** An expression index on one metadata key may be added later only by a migration with a measured need
   (benchmark or production issue). Per-workspace runtime DDL is forbidden.

### 5. Mapping to search

1. **R15** Every `Metadata` or `Coding` field with `IsSearchable = true` is assigned a **typed slot** in the search
   projection (`cf.<type>.<slot>`) recorded on the `FieldDefinition` (`SearchSlot`). The full rules are in ADR-007. The
   projection builder writes values from canonical JSONB; ChoiceIds are projected as keyword ids, and the planner
   (ADR-008) resolves names to ids.
2. **R16** `FieldDefinition` exposes **capability metadata** derived from type and slot kind: `sortable`,
   `filterable`, `rangeable`, `aggregatable`, `fullText`, `wildcard`, `highlightable`. The API serves it (`E07-T02`)
   and the grid uses it to enable sort and facet controls. Clients MUST NOT derive capabilities from the type alone.
3. **R17** Any change to a projected value (column or metadata) increments `DocumentVersion` in the same transaction
   (ADR-001, `E04-T02`).

## Consequences

- **Positive:** renames and choice edits are O(1). One coercion function gives identical preview, import and API
  behaviour. Coding writes never rewrite imported metadata. No GIN write amplification. Search capabilities are
  explicit, so the grid never offers a sort that OpenSearch cannot serve.
- **Negative / costs:** ad-hoc SQL over metadata needs a field-id lookup (`Metadata->>'f1017'`); support tooling
  provides a view per workspace that resolves names. Overlay by custom key is unavailable in the MVP. Decimal values
  beyond double precision are exact in PostgreSQL but approximate in search (ADR-007).
- **Follow-up work:** `E04-T02` (columns), `E04-T03` (FieldDefinition, Choice, coercer, capabilities), `E08-T02`
  (import preview), `E07-T02` (slots, projection), `E09-T01`/`E09-T02` (relationship columns).
- **Verification:** golden coercion tests for all nine types; a migration lint check that rejects GIN indexes on
  `Document.Metadata`; an integration test proving a rename issues no `UPDATE` on `Document` (statement capture); a
  property test that `Coerce` is idempotent on canonical output.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Pure EAV (`DocumentFieldValue` rows) | Explicitly rejected by §6: ~30 rows per document (3B rows at 100M), join-heavy projection building, high bloat. |
| One table per workspace with real columns | Per-workspace DDL at 1,000 workspaces, migration fan-out, catalog bloat. Conflicts with ADR-005 partition-ready rules. |
| JSONB keyed by display name | A rename rewrites every row and reindexes everything; names collide across vendors and casing. |
| JSONB keyed by UUID FieldDefinitionId | Correct but ~36 bytes per key per row; the int key has the same stability at a fraction of the size. |
| Coding in `Metadata` JSONB | Every review click rewrites a multi-KB tuple (no HOT, WAL growth). §6 requires separation. |
| GIN on `Metadata` for PostgreSQL-side filtering | Duplicates OpenSearch's role at large write cost; unproven need. |

## Baseline amendments

- *Proposed:* §5 — add `MetadataRaw`, the structural columns in §1 (notably received Bates, `FamilyDate`,
  `DocumentDateSource`, artifact flags) and `FieldDefinition.Storage` (`Column` / `Metadata` / `Coding`) to the
  document model.
- *Proposed:* §6 — the nine types gain two attributes: `IsMultiValue` (Text, Keyword) and `Precision` (Date:
  `Date` | `DateTime`).

## Links

- Baseline: §5, §6, §12, §23
- Review findings: [review-findings.md](../plan/review-findings.md) §8.5, §8.11, §8.12, §13.3
- Decisions: [decisions.md](../plan/decisions.md) Q-27, Q-28, Q-29, Q-31
- Related ADRs: ADR-004a, ADR-005, [ADR-007](0007-search-mapping-strategy.md), [ADR-009](0009-family-dedupe-thread-identity-and-dates.md)
