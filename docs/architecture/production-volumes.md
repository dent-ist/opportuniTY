# Production volumes (E12-T05)

A finalized production is written to a load-file volume by a **volume run**: `POST …/productions/{id}/volumes`
(`Production.Create`, Idempotency-Key, 202 with the run and its job). The run is a Production job with
`ProductionVolumeChunk` chunks that the **rendering worker** executes, because producing a page means decoding the
document's image, and that happens only in the document's render sandbox (rendering.md, "Endorsing produced pages").
The same worker plans each run and assembles its load files and manifest. Runs are listed to every `Production.Create`
holder (`GET …/volumes`, `GET …/volumes/{volumeId}`); the files are listed (`…/files`) and downloaded through the
protected-content gateway (`…/files/{fileId}/content` with Range, `…/package` as a streamed ZIP) by the run's
initiator only, with a `Production.Downloaded` audit event before the first byte, as for exports.

## What a run reads: only frozen state (Q-08)

Finalization freezes, besides the Bates numbers and designations (E12-T03/T04), each member's **page set** and its
**redaction version** in the production's Redaction Set (`specification.redactionSetId`, else the workspace's Default
set; V0052). A run reads nothing else that can change: pages of the frozen page set, redactions as of the frozen version
(ADR-012 §3.5), the frozen designation legend, the frozen specification. Every run of the same production on the same
software therefore writes **byte-identical files** and the same `MANIFEST.json` (it holds no run identifiers or times),
so the manifest SHA-256 of two runs shows reproducibility directly. A crash mid-chunk is resumed like every chunked job
(ADR-010): object keys carry the chunk's lease token, only the committed attempt is registered, and its bytes are the
same.

Productions finalized before V0052 have no frozen page sets; a run of one fails with `ProductionNotFrozenForVolumes`
(create a new version).

## Per member

| Output | Images (one per Bates page) | Native | Text |
|---|---|---|---|
| Image | each page of the frozen page set, redactions burned in, endorsements stamped; a page that cannot be decoded (or has no image) becomes a **Technical Issue** page | — | extracted text; for a redacted document the fixed text "Text withheld: document contains redactions. Refer to images." (ADR-012 §5.4; no OCR engine yet) |
| Native | one slip sheet "Document Produced in Native Format" + Bates + designation (a Technical Issue page when no native is stored) | `<ProdBegBates>.<ext>` | extracted text |
| Placeholder | one **Withheld** page ("Withheld – Privileged") | — | the placeholder text |

- Every generated page (slip sheet, placeholder, technical issue) consumes exactly one Bates number, carries the
  endorsements like every page and has a DAT row. Texts are configurable (`specification.placeholders`) and may use
  `{bates}`, `{confidentiality}` and `{production}`.
- Images are TIFF CCITT Group 4, or JPEG for the colour file types (`images.colorFileTypes`, by file type, Q-21).
  A produced page keeps its source image's pixels and resolution (never resampled, so a burned box is exactly where it
  was drawn); generated pages are US Letter at `images.dpi`. Rendered page sets are produced from their review raster
  (150 DPI) until re-rendering at the production resolution exists (follow-up).
- A redacted document is never produced natively (Q-22): a run fails (`RedactedNative`) rather than ship it, as it does
  when a member's redactions lie on another page set than the frozen one, or when the run's initiator can no longer
  access a member (Q-15: a production cannot drop a member from its Bates numbering). E12-T07's QC gate is meant to
  catch these before finalization.
- Every member is checked before its chunk commits: images = OPT rows = Bates span, and the designation QC (E12-T04:
  every page carries the legend, the DAT value is the same legend).

## Layout and load files

`<VOL>/IMAGES/IMG0001/<page Bates>.tif|jpg`, `<VOL>/NATIVES/NATIVE0001/<ProdBegBates>.<ext>`,
`<VOL>/TEXT/TEXT0001/<ProdBegBates>.txt`, `<VOL>/DATA/<VOL>.dat|.opt`, with `MANIFEST.json`/`MANIFEST.csv` next to
the volume. The volume defaults to `<Bates prefix>_VOL001`; folders hold at most `maxFilesPerFolder` files (images by
Bates offset, natives and text by production order). The DAT uses the export builder's conventions (E12-T01: delimiter
preset, UTF-8 with byte-order mark, CRLF, formula neutralization, configured path separator) with the production's date
format and time zone; its default columns are ProdBegBates, ProdEndBates, ProdBegAttach, ProdEndAttach, Custodian,
AllCustodians, FileName, FileExtension, DateSent, DateCreated, DateLastModified, From, To, CC, BCC, Subject, MD5Hash,
Confidentiality, Redacted, PageCount, NativeLink and TextLink (metadata fields the workspace does not have are left
out). The OPT has one row per image: the page Bates as image key, `Y` and the page count on each document's first row.

## For the burn-in verification (E12-T06)

Each run keeps, with its files but never delivered, `_verification/pages.csv`: one row per produced image with its page
Bates, member, path, SHA-256, size, where the source page sits in the image (`PageTopPx`, `PageHeightPx`), the kind
(`Page`, `TechnicalIssue`, `SlipSheet`, `Placeholder`), the source page set and page, and every burned box in image
pixels (`x y w h type`, from `RedactionGeometry.BurnedPixels`, the function the burner uses). The manifest records its
SHA-256. Burning (rendering.md) fills a black box black to the last device pixel (rounded outward) and a labelled box
white with its label in black, framed just outside the box, so every pixel inside a box is fill or glyph.
