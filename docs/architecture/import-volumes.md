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
(`matter-a/VOL001`, never an absolute path); without it the share itself is the volume. The API never reads the share;
only the import worker does. A load that maps `NativeLink`, or `TextLink` without text-in-DAT mode, fails its
preparation with `import-volume-unavailable` when the share is not configured or the folder does not exist.

## Path rules (`Opportunity.Import.Volumes`)

1. `VolumePath` (lexical): `\` and `/` separators, leading `.\`, empty and `.` segments ignored. An absolute path is
   accepted only when `paths.stripPrefix` removes its leading part (`\\fileserver\exports\VOL001`, `D:\Prod\VOL001`;
   case-insensitive, either separator, at a segment boundary). Rejected: `..` (and any all-dots segment), a leading
   separator (root, UNC, `\\?\`, `\\.\`), `:` anywhere (drive letters, alternate data streams, URLs), wildcard and
   control characters, segments over 255 or paths over 4,096 characters.
2. `ImportVolume` (file system): walks the path one component at a time from the volume's real path, expanding
   symbolic links itself, and accepts only a regular file whose real path is inside the volume. Links that stay inside
   are followed; links that leave it are rejected; loops and dangling links are missing. The returned path contains no
   links. A component that does not exist exactly is matched case-insensitively when exactly one entry matches
   (volumes produced on Windows).

## Outcomes per row

| Situation | Default (`paths.missingFiles = flag`) | `paths.missingFiles = error` |
|---|---|---|
| Blank link | `NativeMissing` / `TextMissing` set (new documents), no issue | same |
| File not in the volume | flag set, warning `native-missing` / `text-missing` | row error, not loaded |
| Path rejected (traversal, absolute, link outside) | row error `native-path-rejected` / `text-path-rejected` | same |
| DAT MD5 / SHA-1 / SHA-256 differs from the native | warning `hash-mismatch-md5` / `-sha1` / `-sha256`; computed value stored | same |
| Text with invalid byte sequences | `TextEncodingWarning`, warning `text-encoding` | same |

No outcome fails the chunk. An unreadable share (I/O error) fails the chunk transiently, so it is retried.

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

## Shared pieces

The OPT image load (`E08-T05`) reuses `ImportVolume` (resolve), `VolumeObjectWriter.StoreFileAsync` with the `Image`
area (store), `ImportObject` (the stored file a row carries) and `StoredObjectSql.RegisterAsync` (registry rows in the
chunk transaction).
