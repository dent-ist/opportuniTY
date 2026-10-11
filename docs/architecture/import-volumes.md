# Import volumes: natives and extracted text (E08-T04)

How the files a load file references reach object storage. Normative sources: ADR-011 (keys, registry, upload
staging), ADR-015 D12 (only the protected-content gateway serves content), ADR-009 R17 (hashes), ADR-007 R9/R10 and
Q-29 (indexed-text cap).

## Where volume files come from

| Path | Status | Use |
|---|---|---|
| **Server-side import share** (worker setting `Import:VolumeShareRoot`, read-only mount) | M1, implemented | Production volumes of any size. The operator (or a transfer tool) places `VOL001/{DATA,NATIVES,TEXT,IMAGES}` in the share. |
| Browser upload staging (`ws/{ws}/imports/{importId}/upload/{uploadId}`, presigned PUT, quarantined) | ADR-011 §5.7, later (`E08-T08`, `E08-T09`) | Small loads from the wizard; scanned before use. |

The DAT itself is still uploaded through `POST /workspaces/{id}/imports` and stored content-addressed under
`imports/{importId}/source/`. The import profile's `paths.volumeRoot` names the volume folder **inside** the share
(`matter-a/VOL001`, never an absolute path); without it the share itself is the volume. Only the import worker reads
files from the share; the API mounts it read-only too, but only to check that files exist during the pre-flight
(E08-T06; it resolves paths with the same rules and never opens a file). A load that maps `NativeLink`, or `TextLink`
without text-in-DAT mode, fails its preparation with `import-volume-unavailable` when the share is not configured or
the folder does not exist.

## Path rules (`Opportunity.Import.Volumes`)

1. `VolumePath` (lexical): `\` and `/` separators, leading `.\`, empty and `.` segments ignored. An absolute path is
   accepted only when `paths.stripPrefix` removes its leading part (`\\fileserver\exports\VOL001`, `D:\Prod\VOL001`;
   case-insensitive, either separator, at a segment boundary). Rejected: `..` (and any segment of only dots and spaces), a leading
   separator (root, UNC, `\\?\`, `\\.\`), `:` anywhere (drive letters, alternate data streams, URLs), wildcard and
   control characters, segments over 255 or paths over 4,096 characters.
2. `ImportVolume` (file system): walks the path one component at a time from the volume's real path and accepts only
   a regular file inside the volume. Symbolic links are never followed below the import share: a link anywhere on the
   path (volume folder or file path, pointing inside or outside) rejects it, so nothing a load file names can be
   redirected. Only the operator-configured share path itself may contain links. An absolute volume folder is accepted
   only inside the share. A component that does not exist exactly is matched case-insensitively when exactly one entry matches
   (volumes produced on Windows).

## Outcomes per row

| Situation | Default (`paths.missingFiles = flag`) | `paths.missingFiles = error` |
|---|---|---|
| Blank link | `NativeMissing` / `TextMissing` set (new documents), no issue | same |
| File not in the volume | flag set, warning `native-missing` / `text-missing` | row error, not loaded |
| Path rejected (traversal, absolute, any symbolic link) | row error `native-path-rejected` / `text-path-rejected` | same |
| DAT MD5 / SHA-1 / SHA-256 differs from the native | warning `hash-mismatch-md5` / `-sha1` / `-sha256`; computed value stored | same |
| Text with invalid byte sequences | `TextEncodingWarning`, warning `text-encoding` | same |

| Native over `Import:Limits:MaxNativeBytes` (default 4 GiB) | row error `native-too-large`, not loaded | same |
| Native bytes contradict the file name (e.g. a `.pdf` that is a ZIP, an executable under any non-program extension) | warning `native-type-mismatch`; the sniffed type is stored | same |
| Native infected, or not screenable (encrypted, oversize, scan timeout, archive limits) | warning `native-quarantined`; stored and registered `Quarantined` | same |
| OPT image infected or not screenable | warning `opt-image-quarantined`; stored `Quarantined`, the page has no image | same |

No outcome fails the chunk. An unreadable share (I/O error) or an unreachable scanner fails the chunk transiently, so
it is retried.

## Storage

- Natives: byte for byte under `ws/{ws}/docs/{documentId}/native/{sha256}`, content type sniffed from the bytes.
- Extracted text: decoded with its own encoding detection or `loadFile.textEncoding`, stored as UTF-8 (no BOM) under
  `ws/{ws}/docs/{documentId}/text/{sha256}`. `paths.textInLoadFile` stores the `TextLink` column's value itself.
- `TextLength` is the full length in UTF-16 code units; `TextTruncated` is set above the indexed-text cap
  (`Search:IndexedTextCap`, default 10M characters). The full text stays in storage; the projection indexes up to the
  cap and `texttruncated:true` is searchable.
- SHA-256 (plus MD5 and SHA-1 for natives) is computed before the upload. The chunk transaction registers each object in
  `stored_object` together with its document (ADR-011 §2.4) and points `native_object_id` / `text_object_id` at it.
- Retries: a new row's DocumentId is derived from (import batch, row number), and keys are content-addressed under it,
  so a retried chunk finds its objects (HEAD) and uploads nothing. An overlay stores files under the existing document
  and keeps the earlier objects registered (ADR-011 §4.2). Objects of rows that end up not loaded are orphans for the
  reconciler.

## Ingest hardening (E08-T09)

Normative: ADR-015 D15/D16 (and their 2026-10-11 amendments), threat model T-44, T-45, T-48.

- **Screening** (`Opportunity.Import.Scanning.IngestScreening`): after a native is stored (content-addressed, so a
  retry is free) and before the chunk registers it, a ZIP native (Office Open XML and OpenDocument included) is held to
  the archive limits, then the bytes go to the `IMalwareScanner` port. The ClamAV adapter (`ClamAvScanner`) streams them
  to clamd with `zINSTREAM`; `FOUND` is Infected, `Heuristics.Encrypted.*` and `Heuristics.Limits.Exceeded.*` and
  clamd's stream limit are Unscannable, a scan longer than `ScanTimeout` is Unscannable (`timeout`). OPT images are
  scanned the same way. Extracted text is not scanned: it is inert and stays searchable.
- **Quarantine:** Infected and Unscannable objects are registered with `state = 2` and their result
  (`stored_object.scan_verdict`, `scan_detail`, `scan_engine`, `scanned_at`, V0069). The render planner only reads
  committed natives, so a quarantined native is never rendered; the protected-content gateway serves it only as an
  audited download with `Document.ViewQuarantined` (plus `Document.DownloadNative`); `GET …/documents/{id}` says
  `native.quarantined`. Metadata and load-file text still load and stay searchable. Each chunk writes one
  `Import.MalwareDetected` audit event (object and document ids, verdict counts, signatures). Release from quarantine
  is not part of the MVP.
- **Report:** `natives.quarantined`, `natives.hashMismatch` (documents whose load-file MD5/SHA-1/SHA-256 differs; also
  audited per chunk as `Import.HashMismatch`) and `images.pagesQuarantined`, in the JSON report, `report.csv` and the
  `Import.Completed` audit event.
- **Configuration** (import worker; the API pre-flight reads the DAT limits too):

  | Setting | Default | Effect |
  |---|---|---|
  | `Import:Limits:MaxRecordBytes` / `MaxFieldCount` | 32 MiB / 2,048 | DAT record limits, frozen with the preparation |
  | `Import:Limits:MaxNativeBytes` | 4 GiB | larger natives fail their row |
  | `Import:Limits:MaxArchiveRatio` / `MaxArchiveEntries` / `MaxArchiveDepth` | 100 / 100,000 / 5 | ZIP limits (ratio once ≥ 16 MiB expanded) |
  | `Import:Limits:MaxNestedArchiveBytes` | 64 MiB | nested archives expanded in memory to inspect them |
  | `Import:MalwareScanning:Mode` | `Off` (Full compose: `ClamAv`) | `Off` or `ClamAv` |
  | `Import:MalwareScanning:Host` / `Port` | `clamav` / 3310 | clamd address |
  | `Import:MalwareScanning:MaxScanBytes` | 2 GiB | larger files are Unscannable without being sent (keep ≤ clamd `StreamMaxLength`) |
  | `Import:MalwareScanning:ScanTimeout` / `ConnectTimeout` | 00:10:00 / 00:00:10 | |
  | `Import:MalwareScanning:MaxSignatureAge` | 2.00:00:00 (48 h) | readiness fails with older signatures (0 disables) |

- **Output neutralization:** every platform-produced CSV/DAT cell goes through
  `Opportunity.Core.Text.FormulaNeutralizer` (a unit test refuses other copies of the rule).

## Shared pieces

The OPT image load (`E08-T05`) reuses `ImportVolume` (resolve), `VolumeObjectWriter.StoreFileAsync` with the `Image`
area (store), `ImportObject` (the stored file a row carries) and `StoredObjectSql.RegisterAsync` (registry rows in the
chunk transaction).

## Pre-flight, import report and error file (E08-T06)

- **Pre-flight** (`POST …/imports/preflight`, same multipart body as the start, `Import.Run` plus `Import.Overlay` for
  overlay modes): one streaming read with the importer's own parser, mapping and row builder. Checks: header and
  mapping, field count per row, required values (control number: `REQUIRED_FIELD_MISSING`), value coercion
  (`DATE_PARSE_FAILED`, unknown choices, hashes), duplicate control numbers within the file, collisions with the
  workspace through pluggable `IImportPreflightCheck`s (Append: `KEY_EXISTS`, `KEY_RETIRED`; overlay modes add
  `KEY_MISSING`), and file existence. Nothing is written but the pre-flight's own result (`import_preflight`, kept 48
  hours for its creator) and a `Import.PreflightRun` audit event.
- **Path rule:** the native/text/image paths of the first 10,000 rows that name files are all checked; after that every
  100th such row is checked and a `PATHS_SAMPLED` warning states how many were. Without `Import:VolumeShareRoot` on the
  API the paths are not checked (`PATHS_NOT_CHECKED` warning); a configured share without the volume folder is a
  `VOLUME_UNAVAILABLE` error.
- **Report:** computed from the batch, its documents, page sets and row issues; frozen on `import_batch.report` by the
  transaction that completes the import and summarized in its `Import.Completed` audit event. `GET …/imports/{id}`
  embeds it as `summary`; `…/report` and `…/report.csv` serve it through the gateway (`Import.ReportDownloaded`).
- **Error file** (`…/error-file`, gateway, audited): built on demand from the retained source DAT and the row errors —
  the header plus `ImportError`, then each failed row byte for byte in the source's delimiters, encoding and BOM. The
  mapping never loads an `ImportError` column, so the fixed file loads again unchanged. Rows over the record-size limit
  cannot be reproduced and appear only in the row errors.
- **Stop after N errors:** profile `stopAfterErrors`; once the committed chunks errored at least N rows the job fails
  with `import-error-limit` (committed rows stay loaded) and the report is frozen.
