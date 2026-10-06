# Render pipeline (E11-T02)

The render worker turns imported page images and PDF natives into browser-displayable page rasters for the viewer,
thumbnails, and the page geometry redaction coordinates are normalized against (ADR-012). This page covers what it
renders, when, where the output goes and which libraries it uses. Sandboxing the renderer is E11-T03.

## When a document is rendered

1. An import job finishes (Completed, CompletedWithErrors, Failed or Cancelled: committed chunks stay, Q-34).
2. The render coordinator (`RenderCoordinatorService`, worker type `rendering`, every 10 s) lists the import's documents
   that need rasters. If there are none, it records the import as handled (`render_request`, V0041) and stops.
   Otherwise it creates one `Render` job (run as the import's initiator, client idempotency key
   `render-import-{importJobId}`), records it, and plans it: explicit-id chunks of at most 100 documents, 1,000 known
   pages and 512 MiB of natives (ADR-010 §6). An oversized document is a chunk of its own.
3. The dispatcher publishes the chunks to `render.chunks`; `JobChunkConsumer` runs `RenderChunkExecutor`. Retries,
   the attempt ceiling, parking and replay (`retry-failed`, `Job.Replay`) are the job engine's (ADR-010 §7). Render jobs
   appear in the job monitor as "Rendering"; they never affect search freshness.

A document needs rasters when:

| Active page set | What is rendered |
|---|---|
| Imported (OPT) | Each page with an original image gets a thumbnail, and a review PNG when the original is a TIFF (browsers cannot show TIFF). JPEG and PNG originals are shown as they are. The rasters are added to the **Imported set's own pages** (purposes `Review`, `Thumbnail`), frame `SourceFrame` of the original file, so a multi-page TIFF is split exactly along the Page rows and redactions keep one page identity. |
| None, or not Ready (Incomplete, Pending, Failed) | If the native is a PDF, TIFF, JPEG or PNG (sniffed type, committed, not quarantined): a **Rendered page set** with one page per PDF page or image frame, a review PNG and a thumbnail per page, and page geometry. |

Activation follows ADR-012 §1.2 and Q-68: a Ready Rendered set becomes active unless the active set is a Ready Imported
set; an Incomplete Imported set (or an older Rendered set) gives way. A Rendered set with some failed pages is
Incomplete (pages flagged `image_missing`) and only fills an empty slot. A source that cannot be rendered at all (not
readable, password-protected, more than 20,000 pages) gets a Failed set with no pages and a per-item job failure; the
chunk still commits. `images_incomplete` stays the import's OPT finding; the document API's `images.incomplete` now
follows the active set.

## Output

| Raster | Format | Size |
|---|---|---|
| Review (PDF page) | PNG, 8-bit gray when the page has no color | 150 DPI; scaled down to at most 4,000 px on the longer side and 16 MP |
| Review (page image) | PNG, 8-bit gray for bitonal/gray sources | source resolution, same bounds; non-square pixels (fax 204 × 98 DPI) resampled to square |
| Thumbnail | PNG | longer side 200 px (shorter side at least 100 px) |

Every raster keeps the page's aspect ratio within 0.5 % (ADR-012 §1.4); one that would not is not written.
Page geometry is the upright page in points (a PDF page's `/Rotate` is applied, rotation 0).

Keys (ADR-011): `ws/{ws}/docs/{doc}/rend/{renditionId}/p{ordinal:000000}.png` and `.thumb.png`. The rendition id and
the Rendered page set id are name-based UUIDs over the document, the source (native SHA-256 or Imported set), the
renderer name and version and the settings hash (`RenderIds`). A retried or repeated chunk therefore writes the same
keys (write-once objects accept identical bytes) and its inserts find the rows already there; a new library version
or new settings make a new rendition. Objects are registered in `stored_object` (area `Rendition`) and served by the
existing page-image and thumbnail routes of the protected-content gateway.

Memory and disk: the source is copied to a temp directory (`Render:TempDirectory`) through a hashing reader (a
mismatch fails the document and is audited as `Integrity.HashMismatch`); PDFium reads the PDF from that file on demand;
pages are rendered, encoded, uploaded and deleted one at a time. A page image frame above 50 MP fails its page (a color frame peaks at about 400 MB while it is decoded). The
lease is extended every 25 pages.

Not in this ticket: word layers from the PDF text layer (ADR-012 §1.5; no consumer yet), WebP, EXIF orientation of
photos, and rendering of other native types (E11-T05).

## Renderer and sandbox

`IRenderer` is file-in, files-out: `RenderAsync(RenderRequest(inputPath, outputDirectory, pages, review))` yields one
`RenderedPage` at a time with the files it wrote. `RasterRenderer` runs in process today. E11-T03 can run the same
renderer in a separate, network-less, resource-limited process with only the two paths mounted, behind the same
interface. PDFium is used without a form-fill environment, so document JavaScript and XFA never run.

## Libraries and licenses

| Component | Use | License |
|---|---|---|
| PDFium (via `bblanchon.PDFium.Linux` / `.macOS` / `.Win32` 157.0.8086) | PDF rasterization | PDFium: BSD-3-Clause (with Apache-2.0 third-party parts); package and build scripts: Apache-2.0 (NuGet metadata) |
| SkiaSharp 4.153.1 and `SkiaSharp.NativeAssets.Linux.NoDependencies` | JPEG/PNG decoding, scaling, PNG encoding | SkiaSharp: MIT; Skia: BSD-3-Clause |
| BitMiracle.LibTiff.NET 2.4.660 | TIFF decoding (CCITT G3/G4, LZW, JPEG, …) | BSD-3-Clause (license exception in `tools/ci/security/license-policy.json`: the package has only a license URL) |

All are OSI-approved permissive licenses on the policy's allow-list; none is copyleft. Only Linux natives are
referenced (`bblanchon.PDFium.Linux`, `SkiaSharp.NativeAssets.Linux.NoDependencies`), and only by the projects that
render (`Worker.All`, `Worker.Rendering`, unit and integration tests); `Opportunity.Rendering` keeps them private, so
the API and the other workers carry none. SkiaSharp's transitive Windows/macOS natives and other RIDs are trimmed from
RID-less builds by `Directory.Build.targets`. The worker image (published for one RID) contains `libpdfium.so` and
`libSkiaSharp.so` for that RID only; they need only glibc, libstdc++ and libgcc, which the chiseled .NET runtime image
already contains, so the Dockerfile is unchanged. PDF rendering is Linux-only (no PDFium for macOS/Windows is
referenced).
