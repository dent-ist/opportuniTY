# Render pipeline (E11-T02, E11-T03)

The render worker turns imported page images and PDF natives into browser-displayable page rasters for the viewer,
thumbnails, and the page geometry redaction coordinates are normalized against (ADR-012). This page covers what it
renders, when, where the output goes, how the renderer is sandboxed and which libraries it uses.

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

## Renderer and sandbox (E11-T03)

`IRenderer` is file-in, files-out: `RenderAsync(RenderRequest(inputPath, outputDirectory, pages, review))` yields one
`RenderedPage` at a time with the files it wrote. `RasterRenderer` does the work; PDFium is used without a form-fill
environment, so document JavaScript, XFA, actions, links and external streams never run or fetch anything.

The render worker never runs it in its own process. `SandboxedRenderer` (the default; `Render:Sandbox:Enabled=false`
is for development on platforms without the sandbox only) starts **one child process per document**
(`IRenderer.BeginDocument(workDirectory)` returns an `IRenderSession`; the executor renders all of a document's sources
through it and disposes it before the document's files are deleted). The child is `Opportunity.Rendering.Sandbox.dll`,
shipped next to the worker and started by the same dotnet host with a **clean environment** (only `DOTNET_*` runtime
switches and `TZ`: no connection strings, keys or tokens) and the document's work directory, limits and render
settings as its argument. It confines itself before it reads the document:

1. **Stage 1** (main thread): if started as root, switch to `Render:Sandbox:RunAsUser`/`RunAsGroup` (default 65534;
   the worker hands it the work directory, source and output directory); set `RLIMIT_CPU`, `RLIMIT_DATA`,
   `RLIMIT_FSIZE`, `RLIMIT_NOFILE` and `RLIMIT_CORE=0`; set `no_new_privs`; apply **Landlock** (read and execute only
   the dotnet host, the shared runtime and system libraries; read only the application directory and a few
   `/proc`/`/sys` files; read and write only the work directory; no TCP bind or connect with ABI 4+; signals and
   abstract Unix sockets scoped to the process with ABI 6+). Landlock binds the calling thread only, so the child then
   re-executes itself: every thread of the new image is born inside the Landlock domain.
2. **Stage 2** (`--confined`): verify the confinement (the root directory must be unreadable), load PDFium and Skia,
   install the **seccomp filter** on every thread (TSYNC): no `socket`, `socketpair` or `connect` of any family, no
   `execve`, no `fork`/`vfork`/non-thread `clone` (`clone3` answers `ENOSYS`, so glibc falls back to `clone`, whose
   flags are checked), no namespaces or mounts, no `ptrace`/`process_vm_*`, no signals to other processes, no BPF,
   perf, io_uring, keyrings or module loading; a foreign architecture (x32) is killed. It then answers `ready` with the
   controls in force, which the worker logs once and enforces (`RequireSeccomp`, default true; `RequireLandlock`,
   default false). Everything runs synchronously on the main thread.

The worker treats the child as untrusted:

| Control | Default (`Render:Sandbox:*`) | On breach |
|---|---|---|
| Wall clock per document process | `DocumentTimeout` 5 min | killed: `render-timeout` |
| No progress (a page or the end of a source) | `PageTimeout` 60 s | killed: `render-timeout` |
| Resident memory, sampled every `SampleInterval` (100 ms) | `MemoryBytes` 1 GiB; `RLIMIT_DATA` twice that; GC heap hard limit half | killed: `render-memory-limit` (also a SIGKILL from the kernel OOM killer) |
| CPU time | `CpuSeconds` 300 (`RLIMIT_CPU`) | SIGXCPU: `render-cpu-limit` |
| Largest file written | `MaxFileBytes` 256 MiB (`RLIMIT_FSIZE`) | the write fails: page error |
| Start-up | `StartupTimeout` 30 s | sandbox unavailable (the chunk is retried) |

Pages stream back over a line protocol (bounded lines, known error codes only). While the worker uploads a page's
files the child is **stopped** (SIGSTOP, every thread verified stopped), so the files cannot change under it, and each
file is checked first: a plain name directly in the output directory, a regular file (no link), within the size limit,
and a PNG whose header matches the reported dimensions; anything else kills the child (`render-sandbox-crashed`). The
child cannot reach object storage or PostgreSQL; the worker uploads.

**Limits and the retry ceiling.** A kill or crash (`RenderLimitException`) stops only that document's process; the
worker continues with the next document. While the chunk has attempts left, the executor fails the chunk as transient
(`TransientChunkException` with the code), so it is retried with backoff (a busy host can cause a timeout). On the
last attempt (`AttemptCount = MaxAttempts`, default 5) the document fails like any unrenderable source: a Failed
Rendered page set and a per-item failure with the code, while the rest of the chunk commits. A sandbox that cannot
start or apply its controls (`RenderSandboxUnavailableException`) is a deployment error, never a document failure: the
chunk is retried and finally parked as Failed. Cost: about 0.3–0.6 s of process start per document (two runtime
starts), and a limit hit re-renders the chunk's other documents on each retry (identical objects; nothing is
duplicated).

**Without Landlock** (kernels before 5.13, or Landlock missing from the kernel's LSM list) the child still has the user
switch (when the worker is root), the clean environment, the limits and the seccomp filter, but file access is confined
only by Unix permissions. The worker then makes itself non-dumpable (`ProtectWorkerWithoutLandlock`), so a renderer
running as the same user cannot read the worker's `/proc/<pid>/environ` or memory. Set `RequireLandlock=true` to
refuse to render instead.

### What the deployment must provide

The worker itself needs the network (PostgreSQL, RabbitMQ, object storage), so the render child's lack of network comes
from its seccomp filter and Landlock, not from the container. The deployment must keep:

- **Docker's default seccomp profile** (or a stricter one that still allows `seccomp`, `prctl` and the `landlock_*`
  calls); never `seccomp=unconfined`. Compose already runs the worker non-root (UID 1654) with `read_only: true`,
  `cap_drop: [ALL]`, `no-new-privileges:true`, a tmpfs at `/tmp` (the render temp directory) and a `pids_limit`.
- A kernel with **Landlock enabled** (5.13+, `lsm=` including `landlock`; Ubuntu 22.04+ and most current
  distributions) for path confinement.
- A container **memory limit** above `MemoryBytes` times the concurrently rendered documents plus the worker itself, so
  the kernel's OOM killer is only the last resort (reported as `render-memory-limit`).
- **Network policy** as defence in depth for the worker (Kubernetes once it is packaged, Q-41; firewall rules
  otherwise): egress only to PostgreSQL, RabbitMQ AMQP (not the management port), object storage and the OTLP
  collector; no internet; no OpenSearch unless the same container runs the indexing workers. In a Full profile, run the
  rendering worker type (`Workers__Enabled=rendering`) in its own container with that narrow egress.

### What the tests prove, and what they do not

`tests/Opportunity.UnitTests/Rendering/Sandbox` (Linux) runs the real child: output identical to the in-process
renderer; one process per document, torn down with the session; a probe that tries TCP to a loopback canary, UDP and
Unix sockets, reading `/etc`, writing outside the work directory, reading the worker's environment, signalling the
worker and starting `/bin/true` (all blocked; environment clean; seccomp and `no_new_privs` on; not root); a hostile
corpus generated in memory (`tools/Opportunity.DataGenerator.Corpus/Volumes/HostileFiles.cs`: PDF with JavaScript,
URI, Launch, SubmitForm and GoToR actions and XFA, PDF external stream, PDF and XML with XXE and billion laughs, SVG
with script, HTML, macro-enabled Office document, PDF/HTML, PNG/HTML and JPEG/ZIP polyglots, PNG, TIFF and Flate
decompression bombs, truncated PDF) that renders or fails safely with **no connection to the canary**; timeout, CPU and
memory kills followed by a normal document; a lying child (crafted protocol and path); a missing sandbox.
`tests/Opportunity.IntegrationTests/Rendering/RenderPipelineTests` adds the executor against PostgreSQL (a memory bomb
retries the chunk, then fails alone at the attempt ceiling while the other document commits) and a probe against the
PostgreSQL test container and public addresses. The seccomp program is checked for x64 and arm64 by a BPF interpreter.

Not verified by automated tests: the arm64 filter on real hardware; behaviour on kernels without Landlock (the tests
assert path confinement only when Landlock is reported); Kubernetes NetworkPolicy (no manifests yet); OpenSearch and
RabbitMQ management as probe targets (the filter denies every socket, whatever the address); AppArmor or SELinux
profiles (none are shipped; the container runtime's default applies). The chiseled worker image with compose's
hardening and Docker's default seccomp profile was checked by hand (the probe reported Landlock ABI 7 and seccomp, and
every attempt blocked), not in CI. Rasters are validated but not re-encoded by the worker (threat model T-42 asks for
re-encoding).

## Endorsing produced pages (E12-T04)

Producing a page means decoding the document's image, so it happens where every other decode happens: in the
document's sandboxed render process. `IRenderSession.EndorseAsync(EndorseRequest)` takes one page (a frame of a TIFF,
JPEG or PNG under the session's work directory, or a blank page for a slip sheet or placeholder with centred body
lines), the stamps (text per position: top or bottom, left, centre or right), the font size and margin in points and the
canvas option, and returns the encoded page (TIFF CCITT Group 4, JPEG or PNG) with its size, resolution and colour mode.
In the sandbox the child writes `endorsed.{tif,jpg,png}` into the request's output directory and answers `endorsed`;
the worker stops the child, checks the file (exact name, regular file, size limit, requested format, image header whose
dimensions match the report: PNG IHDR, JPEG SOF, TIFF IFD with compression 4), reads it, deletes it and resumes the
child. The production volume writer (E12-T05) calls it per page with the stamps `EndorsementPlanner` derives from the
frozen specification and each member's frozen designation.

Layout: font size and margin are converted to pixels at the page's resolution. With `expandCanvas` (the default) a band
is added above and/or below the page for the top and bottom stamps, so the image is never overwritten; otherwise each
stamp is drawn over the page on a white box. A row of stamps too wide for the page is drawn in a smaller font until it
fits. The page's pixels are copied, never resampled (non-square pixels are made square by repeating rows); producing at
another resolution than the page's own is E12-T05's decision.

Deterministic (Q-08, same inputs give the same bytes): the text uses an embedded font (Liberation Sans 2.1.5, SIL OFL
1.1, `src/Opportunity.Rendering/Fonts`, never a host font), aliased, into a coverage mask copied onto the page as pure
black on pure white, so no blending or SIMD-dependent arithmetic touches the result; TIFF G4 is thresholded at
mid-gray and written by LibTiff.NET (no timestamps), JPEG at quality 90 and PNG by Skia, the JFIF density set to the
page's resolution. `PageEndorser.Version` (pipeline version, Skia, LibTiff.NET, font name and hash) is recorded with
produced images; a unit test pins the SHA-256 of a generated page, so a library or font upgrade that changes the output
fails it until the pipeline version is bumped. The sandbox and in-process results are tested to be byte-identical.

### Burning redactions (E12-T05)

An endorsement request may carry the page's redactions (normalized rectangles, ADR-012 §2). They are burned into the
page's own pixels before the bands and stamps are added, in the same sandboxed call: a black box is filled black and a
labelled box white with its reason's label in black (aliased, embedded font), framed just outside the box. Integer
arithmetic only, so the output stays deterministic; the endorsed page reports where the source page sits in it
(`PageTopPx`, `PageHeightPx`). The production volume writer (docs/architecture/production-volumes.md) calls it through
`IProducedPageImager`, implemented here over the render session.

### Verifying the burn-in (E12-T06)

`IRenderSession.VerifyAsync(BurnInRequest)` checks a produced page in the same sandboxed process: the request names the
produced image (as read back from storage), the source page (file and frame), where the imager reported the page
(`PageTopPx`) and the redactions that had to be burned (normalized rectangles). The child answers `verified` with its
findings; the worker accepts only known codes, boxes of the request and bounded counts. `BurnInVerifier` decodes both
images exactly as the endorser does, maps each redaction to pixels of the decoded source with
`RedactionGeometry.BurnedPixels` (so it does not trust the imager's reported size) and finds:

- `HiddenData`: the file is not one plain image of its format (`ProducedImageContainer`: a TIFF with one IFD, only the
  tags the endorser writes, one strip and no unreferenced bytes; a JPEG with JFIF without thumbnail, tables and scans
  only and nothing after EOI; a PNG with critical and resolution chunks only and nothing after IEND);
- `ProducedUnreadable`, `SourceUnreadable`, `PageGeometry`: an image cannot be decoded, or the produced width or the
  reported row does not fit the source page;
- `PageMisaligned`: fewer than half of the source's ink pixels outside the boxes (and their frames) are found at the
  reported place, so the boxes would be checked in the wrong place;
- `BoxNotOpaque`: a pixel of a black box is not black, or a pixel of a labelled box is neither white fill nor black
  label (or the box is less than half white);
- `BoxShowsSource`: half or more of the box's source pixels that differ from the burned colour are found unchanged.

TIFF G4 and PNG are compared exactly (a TIFF G4 page against its source thresholded as the endorser thresholds it); JPEG
by luminance within 48 levels, because JPEG subsamples colour and bleeds it a pixel or two into a box's edge.
`BurnInVerifier.Version` is recorded in the volume manifest.

## Viewer delivery (E11-T03)

The protected-content gateway serves only derived renditions inline, and only as `image/png`, `image/jpeg` or
`image/webp` (page images, thumbnails) or `text/plain` (text). A native is always `application/octet-stream` with
`Content-Disposition: attachment`, streamed or presigned, so an HTML, SVG or script native is never served as an
active type from the app origin; anything else stored under a rendition is forced to an octet-stream attachment too.
Every content response carries `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox;
default-src 'none'`. The web app's CSP (`deploy/docker/web/security-headers.conf`: `script-src 'self'`, no
`'unsafe-inline'`) blocks inline script, event-handler attributes and `javascript:` URLs; `e2e/csp.spec.ts` shows it in
Chromium against the production build.

## Libraries and licenses

| Component | Use | License |
|---|---|---|
| PDFium (via `bblanchon.PDFium.Linux` / `.macOS` / `.Win32` 157.0.8086) | PDF rasterization | PDFium: BSD-3-Clause (with Apache-2.0 third-party parts); package and build scripts: Apache-2.0 (NuGet metadata) |
| SkiaSharp 4.153.1 and `SkiaSharp.NativeAssets.Linux.NoDependencies` | JPEG/PNG decoding, scaling, PNG encoding | SkiaSharp: MIT; Skia: BSD-3-Clause |
| BitMiracle.LibTiff.NET 2.4.660 | TIFF decoding (CCITT G3/G4, LZW, JPEG, …); TIFF G4 encoding of endorsed pages | BSD-3-Clause (license exception in `tools/ci/security/license-policy.json`: the package has only a license URL) |
| Liberation Sans 2.1.5 (`LiberationSans-Regular.ttf`, embedded resource) | Endorsement text (E12-T04) | SIL OFL 1.1; the license text ships with it (`Fonts/LICENSE-LiberationSans.txt`, also embedded) |

All are OSI-approved permissive licenses on the policy's allow-list; none is copyleft. Only Linux natives are
referenced (`bblanchon.PDFium.Linux`, `SkiaSharp.NativeAssets.Linux.NoDependencies`), and only by the projects that
render (`Worker.All`, `Worker.Rendering`, the sandbox entry point `Rendering.Sandbox`, unit and integration tests); `Opportunity.Rendering` keeps them private, so
the API and the other workers carry none. SkiaSharp's transitive Windows/macOS natives and other RIDs are trimmed from
RID-less builds by `Directory.Build.targets`. The worker image (published for one RID) contains `libpdfium.so` and
`libSkiaSharp.so` for that RID only; they need only glibc, libstdc++ and libgcc, which the chiseled .NET runtime image
already contains, so the Dockerfile is unchanged. PDF rendering is Linux-only (no PDFium for macOS/Windows is
referenced).
