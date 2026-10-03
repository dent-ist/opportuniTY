# E11 — Rendering, Viewer Backend & Redactions

**Labels:** `epic`, `role:backend`, `role:security`, `role:ui`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 5

## Goal
Make documents viewable through authorized endpoints, render imported PDFs/images safely in a sandbox, and store non-destructive, page-normalized redactions with a usable redaction tool.

## Baseline sections
§13, §24, §32 (VIEW DOCUMENT), §33

## Scope / out of scope
**In scope**
- Content API (metadata, chunked text, page images, native download)
- Render worker for imported PDFs/TIFFs
- Render sandbox and safe delivery
- Redaction API + tool
- Native rendering technology selection (deferred)

**Out of scope**
- OCR and native processing (deferred §1)
- Production burn-in (E12)

## Contributing roles
- **Roles:** Backend, Security & Compliance, UI/UX
- **Source reviews:** Backend/Architecture, Security & Compliance, UI/UX, eDiscovery Practitioner, Legal/Discovery Counsel
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] Every content request passes the authoritative gateway; a stale hit for a newly privileged document cannot be opened
- [ ] Malicious corpus renders or fails safely within limits with no outbound connections
- [ ] Redactions survive re-rendering at different resolutions

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E11-T01](#e11-t01) | Build document content API for metadata, chunked text, page images and natives | M1 | M | E05-T04, E08-T04 |
| [E11-T02](#e11-t02) | Build render worker pipeline for imported PDFs and images | M3 | M | E06-T05, E06-T02, E19-T01, E04-T02 |
| [E11-T03](#e11-t03) | Sandbox render workers and deliver safe renditions | M3 | L | E11-T02, E02-T08 |
| [E11-T04](#e11-t04) | Implement non-destructive redactions API and redaction tool | M3 | L | E11-T01, E11-T02, E02-T06, E16-T04, E14-T01 |
| [E11-T05](#e11-t05) | Select native rendering technology | M5 | L | E11-T03 |

---

### E11-T01

**Build document content API for metadata, chunked text, page images and natives**  
Labels: `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§13, §24, §32 VIEW DOCUMENT. UI finding 8: ~10 MB text cannot be loaded or highlighted in one piece; a chunked text API is needed.

#### Description
Endpoints for metadata (all fields with types), extracted text in chunks/ranges, page images and imported PDFs, and native download — every call through the protected-content gateway. Range requests for large natives; `TextTruncated` indicator.

#### Acceptance criteria
- [ ] A stale search hit for a newly privileged document cannot be opened (index worker paused)
- [ ] Text is served in bounded chunks; a 10 MB text document's first chunk returns in < 300 ms on the dev profile
- [ ] Range requests work for large natives; every access is audited (prefetch flagged as non-view)

#### Dependencies
- `E05-T04` — Build protected-content gateway and authoritative access service
- `E08-T04` — Link natives and extracted text into object storage

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, UI/UX, Security & Compliance, eDiscovery Practitioner
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Merges backend 'Document viewer API with authoritative re-check' and UI text-chunk API dependency.

---

### E11-T02

**Build render worker pipeline for imported PDFs and images**  
Labels: `role:backend`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§13: Render Worker → page images/PDF/thumbnails; native rendering technology chosen separately (§33). Browsers cannot display TIFF G4, so page images need conversion.

#### Description
`IRenderer` abstraction and render consumer generating browser-displayable page images (PNG/WebP) and thumbnails from imported PDFs and TIFF/JPG pages; chunked jobs; deterministic output keys; page dimensions recorded for redaction coordinates.

#### Acceptance criteria
- [ ] A 500-page PDF renders to page images + thumbnails idempotently on retry
- [ ] Multi-page TIFF is split into per-page images matching the Page rows
- [ ] The license of every rendering dependency is recorded and OSI-compatible

#### Dependencies
- `E06-T05` — Build idempotent consumer framework
- `E06-T02` — Implement Job and JobChunk state machine with leases
- `E19-T01` — Implement object storage abstraction and provider contract suite
- `E04-T02` — Create workspace, document and page core schema

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E11-T03

**Sandbox render workers and deliver safe renditions**  
Labels: `role:security`, `role:backend`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§13. Security finding 11: Office/PDF/image parsers have a history of RCE (LibreOffice, Ghostscript, ImageMagick, PDFium).

#### Description
Render processes run non-root with read-only rootfs, dropped capabilities, seccomp/AppArmor, no network egress (input/output via a broker process or mounted temp dir), CPU/memory/wall-clock limits, one document per process with teardown. Viewer serves only derived renditions with strict CSP; HTML natives are rendered to image/PDF and never served as `text/html` from the app origin.

#### Acceptance criteria
- [ ] Network-policy test: the render process cannot reach the internet, PG, OpenSearch or RabbitMQ management
- [ ] Malicious corpus (PDF JS, Office macros, XXE, SVG script, decompression bomb, polyglot) renders or fails safely within limits with no outbound connections
- [ ] Timeout/OOM kills only the single job; the chunk is marked failed with a retry ceiling
- [ ] Viewer CSP blocks inline script

#### Dependencies
- `E11-T02` — Build render worker pipeline for imported PDFs and images
- `E02-T08` — Produce STRIDE threat model and ADR-015 security architecture

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Backend
- **Source reviews:** Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
SEC-16. Proposed amendment — render sandboxing requirement added to §13.

---

### E11-T04

**Implement non-destructive redactions API and redaction tool**  
Labels: `role:backend`, `role:ui`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§13 non-destructive page-coordinate annotations; ADR-012; UI finding 15; legal: privacy redaction reason codes separate from privilege.

#### Description
API: page-normalized rectangles with type/reason (configurable picklist incl. privilege and privacy reasons such as PII, PHI, Personal Data – GDPR), author, timestamps, versioned redaction sets, optimistic concurrency, full audit; deleted redactions remain in history. UI: redaction mode on the page viewer (draw, move, resize, delete, undo, list), keyboard alternative (WCAG 2.5.7), production-preview toggle, disabled while page images are pending/failed.

#### Acceptance criteria
- [ ] Redactions survive re-rendering at different resolutions (normalized-coordinate test)
- [ ] Each redaction shows type, reason, author and timestamp; every change is audited
- [ ] A keyboard user can create and adjust a box with arrow keys
- [ ] A concurrent edit by another user prompts refresh; redactions are never silently lost
- [ ] Redaction mode shows 'Redaction requires rendered images' when renders are missing

#### Dependencies
- `E11-T01` — Build document content API for metadata, chunked text, page images and natives
- `E11-T02` — Build render worker pipeline for imported PDFs and images
- `E02-T06` — Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle
- `E16-T04` — Build viewer modes for text, metadata, native and page images
- `E14-T01` — Build append-only partitioned audit store and writer

#### Roles
- **Owner:** Backend
- **Contributing:** UI/UX
- **Source reviews:** Backend/Architecture, UI/UX, Legal/Discovery Counsel, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Q-22 (text/term-based and native redaction scope).

---

### E11-T05

**Select native rendering technology**  
Labels: `role:backend`, `role:security`, `P2`, `size:L`, `spike` · Milestone: M5 - Post-MVP / Deferred

#### Context
§13, §33 (final native rendering deferred). UI Q8: natives viewable only by download until then.

#### Description
Evaluate native-to-PDF/image engines (LibreOffice headless, Gotenberg, commercial) on fidelity, licensing, scalability and CVE history; implement the chosen `IRenderer` inside the sandbox; enable native-mode viewing and TIFF/JPG production imaging for natives.

#### Acceptance criteria
- [ ] Fidelity scorecard on a 200-document format test set; decision recorded as an ADR including a security/CVE criterion
- [ ] Chosen renderer passes the sandbox malicious-corpus tests

#### Dependencies
- `E11-T03` — Sandbox render workers and deliver safe renditions

#### Roles
- **Owner:** Backend
- **Contributing:** Security & Compliance
- **Source reviews:** Backend/Architecture, Security & Compliance, UI/UX
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / L

#### Notes
Q-36.
