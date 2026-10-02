# ADR-012: Page model and versioned, non-destructive redactions

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-02 |
| **Owner (role)** | Backend |
| **Deciders** | Lead architect; contributing: Legal / Discovery Counsel, eDiscovery practitioner, Security & Compliance, UI/UX; product owner (amends §5 and relies on Q-08/Q-22) |
| **Tracking issue** | #32 (plan key `E02-T06`) |
| **Baseline sections** | [§5](../architecture/architecture-baseline.md#5-core-domain-and-document-model), [§12](../architecture/architecture-baseline.md#12-minimal-ingestion), [§13](../architecture/architecture-baseline.md#13-rendering-viewer-and-redactions), [§14](../architecture/architecture-baseline.md#14-production-and-review-workflow), [§22](../architecture/architecture-baseline.md#22-snapshot-semantics) |
| **Related** | ADR-011 (object keys), ADR-013 (audit), ADR-014 (retention); review findings A-13, A-14, §9.4, §9.5, §13.13; Q-08, Q-20, Q-21, Q-22, Q-36 |

## Context

§5 has no page entity. Yet OPT import (one row per page image, page count only on the break row), the viewer, the
redaction tool and production (page-level Bates, OPT output, image count reconciliation) all need per-page identity
(eDiscovery review §12; finding A-13). §13 says redactions are "non-destructive page-coordinate annotations with
type/reason, author and timestamps" and that production "burns" them, but it does not say what the coordinates are
relative to, what happens when a page is re-rendered, or how a production finds the redactions it used.

Binding product decisions:

- **Q-08** — productions are exactly reproducible as of their original run. Redactions are versioned (append-only),
  and the production records the version it used. Reproduction must not depend on archived output files.
- **Q-22** — rectangle-on-image only. Redacted documents are always imaged and never produced natively. Term-based and
  native redaction are post-MVP.
- **Q-21** — TIFF G4 300 DPI and JPG by file type, natives with slip sheets, text, DAT/OPT. PDF output is post-MVP.

Counsel's finding 4 is that burn-in is asserted, not verified. The most common real leak is shipping the original
extracted text of a redacted document; others are metadata, annotations and hidden layers. `E12-T06` must be able to
check this mechanically, which needs a defined model.

## Decision

### 1. Page model (amends §5)

```text
Document 1 ── * PageSet           (Imported from OPT | Rendered by a render worker)
PageSet  1 ── * Page              (logical page: ordinal + geometry)
Page     1 ── * PageImage         (rasters of that page: Original | Review | Production | Thumbnail)
Page     0..1 ─ WordLayer         (word boxes, when text coordinates are known)
```

1. **PageSet** — `(WorkspaceId, PageSetId)` PK; `DocumentId`; `Source` (`Imported` | `Rendered`); `ImportJobId`
   (Imported) or `RendererName`, `RendererVersion` and `RenderSettingsHash` (Rendered); `PageCount`; `Status`
   (`Pending` | `Ready` | `Incomplete` | `Failed`); `CreatedAt`.
2. `Document.ActivePageSetId` names the page set used by the viewer, redaction and production. Default: the Imported
   set if it is `Ready`, otherwise the newest `Ready` Rendered set. Changing it is governed by §3.5.
3. **Page** — `(WorkspaceId, PageSetId, Ordinal)` PK (ordinal 1-based, contiguous); `DocumentId`; `ImageKey` (the
   OPT image key, i.e. received page-level Bates, nullable for rendered sets); `WidthPt`, `HeightPt` (geometry in
   1/72-inch points at rotation 0); `Rotation` (0 | 90 | 180 | 270, clockwise, applied for upright display);
   `ColorMode` (`Bitonal` | `Gray` | `Color`); `SourceFrame` (frame index inside a multi-page TIFF, else 0);
   `ImageMissing` (bool). Composite FKs `(WorkspaceId, …)` throughout ([ADR-019](0019-layering-and-api-conventions.md), A-10).
4. **PageImage** — `(WorkspaceId, PageSetId, Ordinal, Purpose)` PK; `ObjectId` (ADR-011 registry); `WidthPx`,
   `HeightPx`, `DpiX`, `DpiY`; `Format` (TIFF-G4 | JPEG | PNG | WebP). Every raster of a page has the page's aspect
   ratio within 0.5 %; a raster that differs more is rejected. That invariant is what lets one set of normalized
   coordinates apply to every raster.
5. **WordLayer** — optional object (`p000001.words.json`, ADR-011) listing words with normalized boxes. A render worker
   writes it when the source has a text layer (PDF). OCR-derived layers are post-MVP.
6. OPT import (`E08-T05`) creates one Imported PageSet per document, one Page per OPT row (or per frame of a
   multi-page TIFF), `ImageKey` from column 1, the original raster as a `PageImage(Original)`, and sets
   `ImagesIncomplete` on the document when any `ImageMissing` is true. The render pipeline (`E11-T02`) creates
   Rendered PageSets and `Review`/`Thumbnail` rasters. Production rasters (`Production`, 300 DPI per Q-21) are derived
   per production run and are not part of the review page set.

### 2. Coordinates

1. Redaction geometry is a rectangle in **normalized page space**: origin top-left of the page **at rotation 0**, x to
   the right, y down, stored as integers in millionths of the page width/height (`0 ≤ x < x + w ≤ 1,000,000`, same for
   y). Integers avoid floating-point drift between client, server and burner.
2. Rotation is a display transform only. The client maps screen coordinates back to rotation-0 space before saving, so
   changing `Page.Rotation` never moves a redaction.
3. Because every raster of a page shares its aspect ratio (§1.4), a rectangle applies unchanged to the review PNG at
   any DPI and to the 300 DPI production TIFF. This satisfies `E11-T04` "redactions survive re-rendering at different
   resolutions".
4. Rasterization rounds **outward** (floor of the start, ceiling of the end, in device pixels). A burned box is never
   smaller than the drawn box.
5. Minimum size: 2 × 2 device pixels at the production resolution. Maximum: the full page (a full-page redaction is
   valid and common).

### 3. Redactions: append-only, versioned

1. Kind: **Rectangle only** (Q-22). Fill: `Black` (default) or `White` with a 1-pixel black border. An optional label
   (for example "Redacted – Privilege") is drawn inside the box, derived from the reason code.
2. Reason codes are a workspace picklist seeded with `AttorneyClient`, `WorkProduct`, `CommonInterest`, `PII`, `PHI`,
   `PersonalDataGdpr`, `TradeSecret`, `NonResponsive`, `Other`. Each code declares a category (`Privilege` | `Privacy` |
   `Other`); the privilege log (Q-20) uses the category.
3. Storage is **insert-only**. `RedactionRevision`: `(WorkspaceId, DocumentId, RedactionVersion, RedactionId)` PK;
   `Operation` (`Add` | `Modify` | `Remove`); `PageSetId`, `Ordinal`, `X`, `Y`, `W`, `H`; `Fill`; `ReasonCode`;
   `Note` (free text, ≤ 1,000 chars, never copied into audit); `ActorId`; `ActorType` (`Human` | `SystemRule` |
   `Model` reserved, aligned with `E04-T04`); `CreatedAt`. The application role has INSERT and SELECT only; UPDATE and
   DELETE are granted to the lifecycle role alone (ADR-014).
4. Each save is one transaction that (a) checks `If-Match` against `DocumentRedactionState.CurrentVersion`, (b)
   increments it by exactly 1, (c) inserts one revision row per added, modified or removed rectangle with that
   version, (d) writes `Redaction.Added`/`Modified`/`Removed` audit events (ADR-013), and (e) bumps `DocumentVersion`
   and writes the search outbox row so `HasRedactions`, `RedactionCount` and `RedactionReasons` stay searchable. A
   stale `If-Match` returns 409 with the current state; nothing is ever silently overwritten (`E11-T04`).
5. **State as of version V** = for each `RedactionId`, the revision with the highest `RedactionVersion ≤ V`, dropped if
   its operation is `Remove`. The current state is the same query at `CurrentVersion`. `DocumentRedactionState`
   (`CurrentVersion`, `ActiveCount`, `PageSetId`) is a cache of that query, maintained in the same transaction.
6. Redactions belong to one PageSet. They may be created only on pages whose PageSet is `Ready` and whose review
   raster exists (the UI shows "Redaction requires rendered images" otherwise).
7. **Changing the active PageSet** of a document with active redactions is an explicit, permission-checked "migrate
   redactions" action. It is allowed automatically only when page counts are equal and every page's aspect ratio
   matches within 0.5 %. It writes a new redaction version (Remove on the old set, Add on the new set) and flags the
   document `RedactionsNeedReview` until a reviewer confirms. Production QC blocks on that flag. Otherwise the change is
   refused.
8. Permissions: `Redaction.Apply` (add, modify own), `Redaction.Remove` (remove or modify anyone's). Both are
   protected operations under §24 and are re-authorized against PostgreSQL.

### 4. How productions use redactions (Q-08)

1. When a production specification is frozen (`E12-T02`), each member document's row records `PageSetId` and
   `RedactionVersion` (the document's `CurrentVersion` at freeze). The "redaction set version" of a production is
   exactly this per-document map, kept with the materialized snapshot.
2. A production run and any re-run read redactions **as of** the recorded version (§3.5) and pages from the recorded
   PageSet. Edits after the freeze never change that production. They are picked up only by a new production version.
3. Revisions, PageSets and Page rows referenced by a finalized production are retained as long as the production is
   reproducible. Under ADR-014's retain-records profile the outputs and manifest survive while reproduction capability
   ends with the purge of review data; the certificate says so.
4. A document whose coding says "Redact" but which has zero active redactions at freeze, or which has redactions but is
   marked for native production, fails a blocking QC check (`E12-T07`).

### 5. Burn-in rules (production output)

1. **Always imaged.** A document with ≥ 1 active redaction at its frozen version is produced only as images (TIFF G4
   or JPG per Q-21). Its native is never produced, not even with a slip sheet. If it cannot be imaged, finalization is
   blocked.
2. **Flattened pixels.** The burner draws the rectangles and labels into the production raster and writes a
   single-layer image. TIFF/JPG output has no layers, annotations or text layer by construction. Post-MVP PDF output
   (`E12-T09`) must be image-only: no text layer, annotations, optional content groups, embedded files, JavaScript or
   XMP, and a scrubbed Info dictionary.
3. **Metadata scrubbed.** Produced images carry only the tags needed to decode them (dimensions, compression,
   photometric, resolution, strip/tile layout). EXIF, XMP, IPTC, JPEG comment segments, `ImageDescription`, `Software`,
   `DateTime`, `Artist` and all private TIFF tags are stripped.
4. **Text is never the original.** For a redacted document the produced `.txt` is never derived from the original
   extracted text object. It is OCR of the burned production images when an OCR engine is configured; otherwise a
   fixed placeholder ("Text withheld: document contains redactions. Refer to images.") per document. DAT fields listed
   in the production's `FieldsBlankedWhenRedacted` setting (default empty; the QC report warns when `Subject` or
   `FileName` are produced for a document with a privilege-category redaction) are emitted blank.
5. **Verification** (`E12-T06`) runs after every production run and records results per document in the manifest:

| Check | Rule | On failure |
|---|---|---|
| V1 Pixels | Re-read each produced page; every device pixel inside each rounded rectangle, outside the label glyphs, equals the fill value | Block, no override |
| V2 Structure | No produced image has a text layer, annotation, extra layer or any tag outside the allow-list in §5.3 | Block, no override |
| V3 Text | Produced text for a redacted document is not the original text object (hash compare). When a WordLayer exists, no run of ≥ 3 consecutive words, and no single word of ≥ 6 characters, from inside a redaction box appears in the produced text unless it also appears outside every redaction box on that page | Block, no override |
| V4 Natives | No native file exists in the output for a redacted document | Block, no override |
| V5 Pages | Produced page count = PageSet `PageCount` = OPT rows = Bates span for the document | Block (override only with audited reason) |

   Thresholds in V3 are initial values; `E12-T06` may tighten them, never loosen them (same rule as Q-04 gates).

### 6. Viewer behaviour

Reviewers see active redactions as outlined semi-transparent overlays on the review raster, with a "production
preview" toggle that shows opaque boxes. The overlay is a client rendering; the review raster itself is never
modified. Restricting who may see text under a redaction is out of scope for the MVP.

## Consequences

- **Positive:** one page identity serves import, viewer, redaction, production and OPT round-trip. Normalized integer
  coordinates make redactions resolution- and rotation-independent. Append-only revisions give the Q-08 "as of"
  replay and a complete redaction history without a separate audit table. Burn-in becomes a set of checks that fail
  closed.
- **Negative / costs:** PageSet/Page/PageImage add rows (about 1 Page and 2–3 PageImages per page; a 10M-document,
  ~50M-page matter needs partitioning per ADR-005). Image-only output for redacted documents gives up searchable
  produced text unless OCR is configured; the placeholder text is a deliberate, defensible trade-off. V3 is heuristic
  without a word layer, so the hash comparison and the "never original text" rule carry most of the weight.
- **Follow-up work:** `E04-T02` (Page tables, `ActivePageSetId`), `E08-T05` (Imported PageSets), `E11-T02`
  (Rendered PageSets, WordLayer), `E11-T04` (redaction API/UI, If-Match, migrate action), `E12-T02` (freeze
  `PageSetId` + `RedactionVersion`), `E12-T05` (burner, text rule), `E12-T06` (V1–V5), `E12-T07` (QC flags),
  `E13-T03` (privilege log from reason categories), `E20-T04` (privacy reasons, pattern assist).
- **Verification:** property tests for coordinate round-trips across DPI and rotation; database permission test (app
  role cannot UPDATE/DELETE revisions); a replay test that edits redactions after freeze and re-runs the production
  byte-for-byte; a seeded-leak test suite (original text shipped, EXIF left in, native included) that must block
  finalization.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Page fields on Document (`PageCount`, image key list in JSONB) | Cannot carry per-page geometry, rotation, rasters or redaction anchors; OPT reconciliation and Bates-per-page need rows. |
| Coordinates in pixels of the review image | Breaks on re-render at another DPI and on rotation fixes; ties redactions to one raster. |
| Floating-point normalized coordinates | Rounding differences between browser, .NET and the imaging library could shrink a box by a pixel. Integers plus outward rounding cannot. |
| Mutable redaction rows plus a history table | Two sources of truth; "as of version" queries depend on triggers being correct. Insert-only revisions are the history. |
| Snapshot the full redaction set as a JSON blob per version | Simple replay, but quadratic storage on documents edited often, and harder per-redaction audit. |
| Allow native production of redacted spreadsheets | Requires native redaction tooling that Q-22 defers. |

## Baseline amendments

- *Proposed* — §5: add `PageSet`, `Page`, `PageImage` and `RedactionRevision` to the domain model, and
  `ActivePageSetId` to Document (resolves A-13).
- *Proposed* — §13: replace "Production burns redactions" with "Production burns redactions into image-only output and
  verifies the burn (ADR-012 §5); redacted documents are never produced natively (Q-22)".
- *Proposed* — §14: a production records `PageSetId` and `RedactionVersion` per document (part of A-14).

## Links

- Baseline: §5, §12, §13, §14, §22
- Review findings: [review-findings.md](../plan/review-findings.md) A-13, A-14, §8.3, §8.4, §9.4, §9.5, §13.13
- Reviews: [ediscovery.md](../plan/reviews/ediscovery.md) §12 OPT/page model, production outputs; [attorney.md](../plan/reviews/attorney.md) findings 4, 5, 9
- Decisions: [decisions.md](../plan/decisions.md) Q-08, Q-20, Q-21, Q-22, Q-36
