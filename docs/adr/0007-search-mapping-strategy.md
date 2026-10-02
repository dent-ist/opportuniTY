# ADR-007: Search mapping strategy

| Field | Value |
|---|---|
| **Status** | Proposed (the `text` offsets/stored-field choice in R11 is confirmed by the `E18` index-size measurement; everything else is decided) |
| **Date** | 2026-10-02 |
| **Owner (role)** | Search (OpenSearch) |
| **Deciders** | Lead architect; contributing: eDiscovery Practitioner, UI/UX, Security & Compliance |
| **Tracking issue** | #31 (plan key `E02-T05`) |
| **Baseline sections** | [§8](../architecture/architecture-baseline.md#8-opensearch-architecture), [§23](../architecture/architecture-baseline.md#23-search-projection-baseline), §6, §25, §31.5 |
| **Related** | ADR-001, ADR-003, ADR-004b (coding representation), ADR-006, ADR-008, ADR-009; Q-12, Q-29, Q-32; review findings §1.9, §8.12, §13.2, §13.3 |

## Context

§8 says to avoid arbitrary dynamic mappings and to benchmark strict mappings, `flattened`, typed custom-field
containers and dedicated indexes. §23 lists the structural and security fields the mapping must hold from day one.
ADR-006 puts up to 1,000 workspaces into a small pool of **shared** indexes. If each workspace's ~30 custom fields
became its own mapped field, a shared index would carry tens of thousands of fields (mapping explosion; the default
`total_fields.limit` is 1,000). OpenSearch's equivalent of `flattened` is `flat_object`, which treats every leaf as an
untyped keyword string: no numeric or date ranges, no sorting, no aggregations. The UI needs to know per field whether
it can sort, filter or facet (UI findings 2–3). Q-29 caps indexed text at 10M characters with `TextTruncated`. The
eDiscovery review flags `index.highlight.max_analyzed_offset` (1M characters) as a silent highlighting cliff. The
coding representation is **not** decided here (ADR-004b).

## Decision

### 1. Strictness and versioning

1. **R1** Root mapping `dynamic: "strict"`. Every container below it is also strict, except the single overflow
   container (R6). An unknown field fails the write (test in `E07-T02`).
2. **R2** The mapping is a versioned template in the repository (`src/Opportunity.Search/Mappings/projection.v{G}.json`)
   bootstrapped by the migrator. `G` is the `ProjectionGeneration`. Any change to an existing field, analyzer or slot
   budget is a new `G`, rolled out by the ADR-006 reindex protocol. **Creating or deleting a custom field never changes
   the mapping.**

### 2. Typed slot containers for custom fields

3. **R3** Custom and system `Metadata`-storage fields (ADR-003) are projected into **pre-declared, typed slots** under
   the §23 `metadata` object: `metadata.<kind>.s<NNN>`. A slot means nothing outside its workspace. The workspace
   filter (ADR-006 R7) keeps one workspace's `s001` (e.g. Custodian) apart from another's `s001` (e.g. Issue).

| Kind | Field types (ADR-003) | OpenSearch type | Slots | Subfields |
|---|---|---|---|---|
| `txt` | Text (prose) | `text`, analyzer `opp_text` | 100 | `.kw` keyword, normalizer `opp_kw`, `ignore_above: 256` (sort only) |
| `idt` | Text (identifier-like: From/To/CC/BCC, paths) | `text`, analyzer `opp_ident` | 50 | `.kw` as above |
| `kw` | Keyword (single or multi) | `keyword`, normalizer `opp_kw`, `ignore_above: 8191` | 300 | — |
| `int` | Integer | `long` | 100 | — |
| `dec` | Decimal | `double` | 50 | — |
| `dt` | Date (both precisions) | `date`, `strict_date_optional_time` | 100 | — |
| `bool` | Boolean | `boolean` | 100 | — |
| `ch` | SingleChoice, MultiChoice | `keyword` (ChoiceIds) | 200 | — |
| `usr` | User | `keyword` (UserIds) | 50 | — |

4. **R4** Slots are allocated per workspace on field creation (lowest free slot of the kind, stored as
   `FieldDefinition.SearchSlot`). A deleted field's slot enters `Draining` and is reused only after the purge job
   (ADR-003 R6) has re-projected every document that held it.
5. **R5** A `Text` field picks `txt` or `idt` with `FieldDefinition.TextAnalysis` (`Prose` default; `Identifier`
   for addresses, paths and codes). Date-precision values are indexed as UTC midnight of the calendar date and compared
   without zone conversion (ADR-009 R21, ADR-008).
6. **R6** **Overflow.** When a workspace has used every slot of a kind, a new searchable field of that kind goes to
   `metadataOverflow` (`flat_object`, key `f<FieldId>`) with reduced capabilities (exact and prefix match only). The UI
   shows the reduced capabilities. This is the only use of `flat_object`; `flattened`-style storage is not the primary
   container. The slot budgets above are per index generation and can be raised by a new `G`.
7. **R7** Decimal is `double` in search: values beyond 15–16 significant digits are approximate in filters and sorts.
   PostgreSQL stays exact for display and export. `scaled_float` is not used because a fixed scale per slot would not
   fit per-workspace field definitions.

### 3. Structural and §23 fields

| Field | Type / analysis | QL name (ADR-008) | Sort | Filter | Range | Agg | Notes |
|---|---|---|---|---|---|---|---|
| `workspaceId` | keyword | — (injected only) | — | — | — | — | ADR-006 R7 |
| `documentId` | keyword | — | tie-break | id lookup | — | — | |
| `controlNumber` | keyword, `opp_kw`; `.sort` keyword = natural sort key (ADR-009 R5) | `controlnumber` | ✓ (`.sort`) | ✓, trailing `*` | ✓ (natural, on `.sort`) | — | |
| `begBates`, `endBates` | as `controlNumber` | `begbates`, `endbates` | ✓ | ✓ | ✓ | — | Received Bates |
| `familyId` | keyword | `familyid` | — | ✓ | — | ✓ | Expansion key |
| `parentDocumentId` | keyword | — | — | ✓ | — | — | |
| `familySequence` | integer | `familysequence` | ✓ | ✓ | ✓ | — | |
| `familyStatus` | keyword | `familystatus` | — | ✓ | — | ✓ | ADR-009 R10 |
| `duplicateGroupId` | keyword | `duplicategroup` | — | ✓ | — | ✓ | |
| `isDuplicatePrimary` | boolean | `duplicateprimary` | ✓ | ✓ | — | ✓ | |
| `emailThreadId` | keyword | `threadid` | ✓ | ✓ | — | ✓ | Groupable column |
| `securityTags` | keyword | — (injected only) | — | — | — | — | Never user-addressable |
| `fileName` | text `opp_ident`; `.kw` keyword `opp_kw` (1,024); `.wc` `wildcard` | `filename` | ✓ (`.kw`) | ✓ | — | — | `.wc` serves leading/infix wildcards (`wildcard` type needs OpenSearch ≥ 2.15; pinned per A-20) |
| `fileExtension` | keyword `opp_kw` | `extension` | ✓ | ✓ | — | ✓ | |
| `fileType`, `mimeType` | keyword | `filetype`, `mimetype` | ✓ | ✓ | — | ✓ | |
| `fileSize`, `pageCount` | long, integer | `filesize`, `pagecount` | ✓ | ✓ | ✓ | ✓ | |
| `documentDate`, `familyDate` | date | `date`, `familydate` | ✓ | ✓ | ✓ | ✓ (histogram) | ADR-009 R25 |
| `dateSent`, `dateReceived`, `dateCreated`, `dateLastModified` | date | `datesent`, … | ✓ | ✓ | ✓ | ✓ | |
| `md5`, `sha1`, `sha256` | keyword (lower-case hex) | `md5`, `sha1`, `sha256` | — | ✓ | — | — | |
| `metadata` | object of slots (R3) | field query alias | per kind | per kind | per kind | per kind | |
| `metadataOverflow` | `flat_object` | field query alias | — | exact/prefix | — | — | R6 |
| `text` | text `opp_text`, `index_options: offsets`, `store: true`, excluded from `_source` | default field | — | ✓ | — | — | R9–R12 |
| `textTruncated`, `textMissing`, `nativeMissing`, `imagesIncomplete` | boolean | `texttruncated`, … | ✓ | ✓ | — | ✓ | |
| `textLength` | long (full length) | `textlength` | ✓ | ✓ | ✓ | — | |
| `projectionVersion` | long (= `DocumentVersion`, ADR-001) | — | — | — | — | — | Diagnostic |
| `coding` | **owned by ADR-004b**; interim Candidate A reuses the slot kinds of R3 under `coding.<kind>.s<NNN>` behind `IProjectionBuilder` | field query alias | per kind | per kind | per kind | per kind | Replaceable |

Search responses return only grid fields (`_source` filtering). `text` is never returned; snippets come only from
highlighting, after the Q-12 page post-filter.

### 4. Capability metadata (served to the UI)

8. **R8** `GET /api/v1/workspaces/{workspaceId}/fields` returns, per field: `fieldId`, `displayName`, `queryName`,
   `type`, `storage`, `multiValue` and `capabilities {sortable, filterable, rangeable, aggregatable, fullText,
   wildcard, leadingWildcard, highlightable, exists}`. The values come from this table:

| Kind | sortable | filterable | rangeable | aggregatable | fullText | wildcard (trailing) | leadingWildcard | highlightable |
|---|---|---|---|---|---|---|---|---|
| `txt`, `idt` | ✓ (first 256 chars) | ✓ | — | — | ✓ | ✓ | — | ✓ |
| `kw` | ✓ (multi: min value) | ✓ | — | ✓ | — | ✓ | — | — |
| `int`, `dec`, `dt` | ✓ | ✓ | ✓ | ✓ | — | — | — | — |
| `bool` | ✓ | ✓ | — | ✓ | — | — | — | — |
| `ch`, `usr` | — | ✓ | — | ✓ | — | — | — | — |
| overflow | — | exact/prefix | — | — | — | ✓ | — | — |

Choice and User fields are not sortable in the MVP: their sort order would need projected display order or names,
which would force a reindex on every choice reorder or user rename.

### 5. Analyzers and normalizers

| Name | Definition | Purpose |
|---|---|---|
| `opp_text` (index) | `standard` tokenizer (`max_token_length: 255`) → `lowercase` → `asciifolding(preserve_original: true)` | Extracted text, prose fields |
| `opp_text_search` (search) | `standard` → `lowercase` → `asciifolding` | Accent- and case-insensitive matching; no stemming, no stop words, no synonyms (literal legal search; root expansion arrives with `!`, post-MVP) |
| `opp_ident` | `pattern` tokenizer on `[^\p{L}\p{N}]+` → `lowercase` → `asciifolding` | `john.smith@acme.com` → `john`, `smith`, `acme`, `com`; `Q3_Budget-final.xlsx` → `q3`, `budget`, `final`, `xlsx` |
| `opp_kw` (normalizer) | `lowercase` → `asciifolding` | Case- and accent-insensitive keyword equality |

Stemming, language-specific analyzers and diacritic-sensitive search are deferred. Adding them later is a new `G`.

### 6. Text cap, truncation and highlighting (Q-29)

9. **R9** The projection indexes at most **10,000,000 characters** of extracted text (`Search:IndexedTextCap`,
   installation-configurable, maximum 50,000,000). The cut is made at the last whitespace within the final 1,000
   characters before the cap, otherwise at the cap, never inside a UTF-16 surrogate pair. The worker range-reads only
   what it needs from object storage.
10. **R10** Truncation sets `textTruncated = true`, and `textLength` holds the full length. Full text stays in object
    storage for the viewer and productions. `texttruncated:true` is searchable. Search term reports state how many
    in-scope documents were truncated (`E07-T10`), and the viewer shows a banner (`E16`).
11. **R11** `text` is indexed with `index_options: offsets`, so the unified highlighter reads offsets from postings
    and **is not limited by `index.highlight.max_analyzed_offset`**. Hits anywhere in the indexed portion are
    highlightable. `text` is `store: true` and excluded from `_source`, which keeps `_source` small and prevents full
    text from being returned by accident. *Confirmation pending:* `E18` measures the index-size cost of offsets. If it
    exceeds +25% of the index without offsets, the fallback is no offsets plus `max_analyzed_offset` raised to the text
    cap, with the slower highlighting accepted.
12. **R12** Other text slots have values ≤ 100,000 characters (ADR-003), so `max_analyzed_offset` (left at 1,000,000)
    is never reached. Grid snippets: `fragment_size: 150`, `number_of_fragments: 3`, with the highlight query built
    from the AST leaves (ADR-008 §6). Each request also sets a query-level `max_analyzed_offset`, so a stray long value
    degrades to "no snippet" rather than an error.
13. **R13** Per-document request bytes are bounded by the cap (≤ ~40 MB JSON at 10M characters). The chunk worker
    splits `_bulk` requests by bytes (`E07-T04`), well below `http.max_content_length` (100 MB).

## Consequences

- **Positive:** a fixed, small mapping in shared indexes regardless of workspace count. No mapping updates on field
  creation. Typed ranges, sorts and facets for every slot kind. Explicit capabilities for the UI. No silent
  highlighting cliff inside the indexed text.
- **Negative / costs:** slot budgets cap a workspace's searchable fields per kind (overflow degrades capabilities).
  Slots are opaque in raw OpenSearch, so debugging needs the FieldDefinition map. Offsets and stored text increase
  index size (measured in `E18`). Decimal precision in search is that of a double.
- **Follow-up work:** `E07-T02` (mapping, projection builder, capability API), `E04-T03` (slot allocation),
  `E07-T07` (planner uses capabilities), `E16` grid, `E18` (index-size measurement).
- **Verification:** a mapping test asserts every field in §3 with its type; a strict-mapping rejection test; a
  capability-table test generated from the mapping; a highlighting test on a 5M-character document with a hit near the
  end; a truncation test at the cap boundary with multi-byte text.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| One mapped field per custom field (`cf_<workspace>_<field>`) | Mapping explosion in shared indexes; every field creation is a cluster-state update. |
| `flat_object` as the main container | No numeric/date ranges, sorting or aggregations, so the grid loses sort and facets. Kept only as overflow. |
| Nested `{fieldId, value}` pairs (`nested` type) | Every query becomes a nested query; sorting and aggregations are awkward and slow; doc count multiplies. |
| `text` in `_source` | Same storage as stored field, but every `_source` fetch risks returning full text and gets slow. |
| No offsets, raise `max_analyzed_offset` to the cap | Re-analysing up to 10M characters per highlighted hit is slow and memory-heavy; kept as fallback (R11). |
| Stemming by default | Legal search expects literal terms; stemming inflates hit counts in search term reports in ways opposing counsel challenge. |

## Baseline amendments

- *Proposed:* §23 — add `fileExtension`, `familyDate`, `familyStatus`, `isDuplicatePrimary`, `begBates`/`endBates`,
  hashes, `textTruncated`, `textLength` and artifact flags to the required projection fields. `metadata` is defined as
  the typed slot container.
- *Proposed:* §8 — "`flattened` where appropriate" becomes "`flat_object` only as an overflow container".

## Links

- Baseline: §6, §8, §23, §25, §31.5
- Review findings: [review-findings.md](../plan/review-findings.md) §1.9, §8.12, §13.2, §13.3
- Decisions: [decisions.md](../plan/decisions.md) Q-12, Q-29, Q-32
- Related ADRs: [ADR-003](0003-metadata-and-custom-field-model.md), [ADR-006](0006-opensearch-index-strategy.md), [ADR-008](0008-minimal-query-language.md), [ADR-009](0009-family-dedupe-thread-identity-and-dates.md)
