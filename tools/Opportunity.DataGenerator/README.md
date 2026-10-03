# Opportunity.DataGenerator

Seeded, deterministic generator for **synthetic** eDiscovery corpora (E17-T01, baseline §18/§29). Every name,
address, word and value is invented: people are built from syllables and script samples, domains use the reserved
`.example` TLD, and text comes from a pseudo-word Zipf vocabulary. Never load real or "anonymized" client data
(test-strategy §7).

| Project | Role |
|---|---|
| `tools/Opportunity.DataGenerator` | CLI (`opportunity-datagen`) |
| `tools/Opportunity.DataGenerator.Corpus` | Generation model as a library (planner, chunk generator, text synthesizer, writers, statistics). Tooling only: `src/` must never reference it (enforced by `ToolingIsolationTests`). |
| `tests/Opportunity.DataGenerator.Tests` | Determinism, distribution, integrity, text/needle, streaming-memory, load-file volume and defect tests |

## Usage

```bash
dotnet run --project tools/Opportunity.DataGenerator -c Release -- generate --seed 42 --documents 1000000 --out ./corpus-1m

# options
--seed <n>            required; recorded in the manifest
--profile <file>      profile JSON (default: built-in enterprise-reference = profiles/enterprise-reference.json)
--documents <n>       override documentCount (becomes part of the profile hash)
--out <dir>           output directory (default ./corpus)
--threads <n>         worker threads (default: CPU count); output is identical for any value
--inline-text         embed extracted text in documents.jsonl (mean text is ~70 KB/doc: ~70 GB per 1M docs)
--no-ground-truth     skip ground-truth.jsonl

opportunity-datagen init-profile --out my-profile.json    # write the defaults to edit
opportunity-datagen profile-hash --profile my-profile.json # validate + print the canonical hash
```

### Outputs

| File | Content |
|---|---|
| `documents.jsonl` | One JSON object per document in load order: control number, family (`familyId`, `parentControlNumber`, `familySequence`, `attachmentDepth`, `begAttach`/`endAttach`), `custodian`, `allCustodians`, `duplicateCustodians`, `md5`/`sha256`, `duplicateGroupId`, `familyDuplicateGroupId`, `duplicateType` (`none`/`exactMd5`/`crossCustodian`/`withinFamily`), `isDuplicatePrimary`, `emailThreadId`, `nearDuplicateClusterId`, `textBytes`, `fields` (non-null metadata by catalog name), optional `text`. |
| `ground-truth.jsonl` | One line per (document, planted needle) and (document, proximity pair) with the exact occurrence count / word distance. |
| `corpus-manifest.json` | Seed, `profileHash`, generator name/version, output sizes + SHA-256, counts, distribution report (target vs actual with relative deviation), calibration, field catalog, needle summaries (docs with hits, occurrences, family-expanded docs) and the full effective profile. Contains no timestamps, host or thread count, so it is byte-identical across runs. |

`md5`/`sha256` are synthetic identity hashes of the simulated native file (shared by all copies of a document);
they are not hashes of bytes written here. Load-file volumes carry real hashes of the natives they write (see
[native hash rule](#native-hash-rule)).

## Determinism model

* Every entity draws from its own stream keyed by `hash(seed, streamTag, indices)` (SplitMix64 mixing feeding
  xoshiro256**), never from a shared sequential RNG.
* A cheap sequential **planner** decides the shape of candidate unit *i* (an email conversation of 1–50 message
  families, or a loose e-doc family, each with its whole-family duplicate copies) and includes it only if it fits
  the remaining document budget, so the corpus lands exactly on `documentCount` without cutting a family or a
  duplicate group.
* Candidates are grouped into fixed chunks (`unitsPerChunk`); a chunk is a pure function of (seed, profile,
  chunk plan), generated on any worker, then emitted strictly in order through a bounded window of
  `2 × threads` chunks. Copies of a family are shuffled within their chunk so duplicates are not adjacent.
* Memory is O(threads × chunk), independent of corpus size; text is never materialised (a `TextSpec` recipe
  is streamed by `TextSynthesizer` on demand, in 16 KB pieces).
* `GeneratorInfo.Version` must be bumped whenever output for an unchanged (seed, profile) changes;
  `Golden_output_is_stable_for_this_generator_version` pins a small corpus to catch accidental changes.
* Platform caveat: sampling uses `System.Math` log/exp/pow. Results are bit-identical on the same runtime and
  architecture (verified across thread counts and runs on linux-x64); a 1-ulp libm difference on another
  OS/CPU could in principle flip an integer-rounded draw (probability ~1e-12 per draw). Cross-machine identity
  should be confirmed by comparing `documents.jsonl` hashes from the manifest on a second machine.

## Profile (version 1) and defaults

JSON with comments and trailing commas allowed; unknown properties are rejected. Any omitted property takes its
default. The profile hash is SHA-256 of the canonical (defaults-filled) JSON. Defaults reproduce the §29
enterprise reference plus the E17-T01 practitioner defaults (decision Q-06).

| Setting | Default | Meaning |
|---|---|---|
| `documentCount` | 1,000,000 | Exact number of documents |
| `controlNumberPrefix` / `controlNumberDigits` | `OPP` / 10 | `OPP0000000001`… |
| `unitsPerChunk` | 256 | Parallel work item and duplicate-shuffle window (affects output) |
| `mix.emailShare` | 0.60 | Share of **families** (top-level items) whose parent is an email |
| `families.meanFamilySize` | 3.0 | Mean docs per family (parent + all descendants), standalone docs included |
| `families.maxAttachmentDepth` | 3 | Nested attachments (e.g. msg → zip → pdf) |
| `families.nestingProbability` | 0.15 | Later attachment nests under an earlier one |
| `families.edocMeanEmbeddedChildren` | 0.25 | Embedded objects of a non-container loose e-doc (geometric) |
| `families.emailContainerShare` / `edocContainerShare` | 0.004 / 0.006 | Families with a zip-like container |
| `families.containerMin/MaxChildren`, `containerSizeExponent` | 20 / 1000, 2.0 | Power-law container size: the long tail (> 200-member families) |
| `duplicates.rate` | 0.20 | Share of documents whose MD5 occurs earlier in load order |
| `duplicates.copiesWhenDuplicatedMean` | 1.5 | Extra whole-family copies when a family is duplicated |
| `duplicates.sameCustodianShare` | 0.15 | Copies held by the same custodian (`exactMd5`); the rest are `crossCustodian` |
| `duplicates.withinFamilyRate` | 0.05 | Family with ≥ 2 attachments carries one attachment twice |
| `threads.singleMessageShare` | 0.55 | Email units that are a lone message |
| `threads.min/maxLength`, `lengthExponent` | 2 / 50, 1.3 | Power-law conversation length |
| `threads.branchProbability`, `forwardProbability` | 0.2, 0.15 | Reply-tree branching, `FW:` vs `RE:` |
| `threads.meanReplyGapHours`, `maxQuotedShare` | 10, 0.5 | Reply timing; max share of text that is quoted prior message |
| `custodians.count`, `zipfExponent` | 250, 1.0 | Custodians and Zipf documents-per-custodian |
| `custodians.copyToParticipantProbability` | 0.7 | Cross-custodian copy goes to an internal participant |
| `people.internalCount` / `externalCount` / `externalDomainCount` | 2000 / 8000 / 300 | Invented people (first `custodians.count` internal people are custodians) |
| `people.accentedShare` / `cjkShare` / `rtlShare` | 0.08 / 0.05 / 0.05 | Name scripts |
| `recipients.maxRecipients`, `toExponent`, `copyExponent` | 500, 1.9, 1.7 | To/CC/BCC sizes (1–500, heavy-tailed) |
| `recipients.ccEmptyShare` / `bccEmptyShare` | 0.55 / 0.92 | |
| `dates.start` / `end`, `weekendWeight` | 2015-01-01 / 2024-12-31, 0.15 | Business-hours weighted |
| `dates.timeZones` | 11 fixed UTC offsets | Fixed offsets (no tz database, so output is machine-independent) |
| `dates.missingEdocDateShare` | 0.02 | E-docs with no creation date |
| `text.medianBytes` / `p99Bytes` | 5 KiB / 1 MiB | Log-normal extracted-text size (σ derived from the two) |
| `text.minBytes` / `maxBytes` | 64 / 128 MiB | Clamp; the natural tail gives ~340 docs ≥ 10 MiB and a few > 100 MiB per 1M |
| `text.vocabularySeed` / `vocabularySize` / `vocabularyZipfExponent` | 1 / 50,000 / 1.07 | Vocabulary is independent of the corpus seed so query workloads can rely on it |
| `text.minNeedleBodyBytes` | 2048 | Needles are planted in the first 64 body words of large-enough docs |
| `nearDuplicates.rate` / `meanClusterSize` / `wordChangeRate` | 0.03 / 4 / 0.02 | E-docs whose text is a perturbed variant of a cluster base text |
| `fields.extraCustomFields` | 0 | Extra `Custom001…` fields of cycling types |
| `fields.hazardousValueShare` / `unicodeValueShare` | 0.03 / 0.04 | Subject/title/file name/comments containing `þ`, `\u0014`, `®`, newlines, `\|`, `"`… or CJK/RTL/accented words |
| `needles` | 5 terms | `{term, docRate, maxOccurrences, scope: any/parentsOnly/attachmentsOnly}` incl. hyphen and apostrophe forms |
| `proximityPairs` | 1 pair | `{first, second, nearDistance, farDistance, nearDocRate, farDocRate}` — W/n true and false cases |

The standard field catalog has 31 fields: From, To, CC, BCC (multi-value), Subject, DateSent, DateReceived
(different offsets), TimeZone, MessageId, InReplyTo, ConversationIndex (hex; replies extend the parent by 5 bytes),
InclusiveEmail, Importance, FileName, FileExtension, FilePath (per custodian copy), FileSize, Author, Title,
DateCreated, DateLastModified, PageCount (1–2000), Language, HasHiddenContent, Confidentiality, Keywords
(multi-choice), Comments (long text), ProjectCode, Amount (decimal), RecordDate (date), SourceSystem.

### Interpretations to confirm (QA finding 11)

* "~1 parent : 3 family members" is read as **mean family size 3** (parent + descendants), and "60% email /
  40% e-docs" as the share of **families**. Because attachments are mostly e-docs, emails are ~27% of documents
  and attachments ~67%. Both readings are single settings (`families.meanFamilySize`, `mix.emailShare`).
* The duplicate rate counts documents a global MD5 de-duplication would suppress (all non-first occurrences).

### Measured at 1M (defaults, 4-core dev container, Release, `--threads 8`)

~20 s wall clock (≈ 48K docs/s) for metadata output; peak RSS ~340 MB at 3M documents vs ~220 MB at 200K
(server GC). Seeds 1, 2, 3, 42:

| Metric | Target | Actual |
|---|---|---|
| Mean family size | 3.000 | 2.973 – 3.015 (≤ 0.9%) |
| Email family share | 0.600 | 0.600 |
| Duplicate rate | 20.0% | 20.07 – 20.49% (≤ 0.5 pp) |
| Text p50 / p95 / p99 | 5,120 / 220,549 / 1,048,576 | 5,143 / 221,132 / 1,023,455 – 1,065,009 (≤ 2.4%) |
| Text max | ≤ 128 MiB | 128 MiB (clamp reached); 339 docs ≥ 10 MiB, 7 ≥ 100 MiB |

The duplicate rate and family mean are driven by heavy-tailed family sizes (containers up to 1,000 members), so
their seed-to-seed spread at 1M is about ±2% relative; the manifest reports the exact deviation for every run.

## Extension point

Implement `ICorpusSink` (receives `GeneratedChunk`s in load order on one thread; must stream) and pass it via
`CorpusRunOptions.ExtraSinks`, or via `CorpusRunOptions.SinkFactories` when the sink needs the run's
`GenerationContext` (vocabulary, catalog, seed). Use `TextSynthesizer.Write(doc.Content.Text, sink)` to stream
extracted text, and `FieldCatalog` for column order. The load-file volume writer below is such a sink.

## Load-file volumes with defect injection (E17-T02)

`generate --volumes` additionally writes production-style volumes that exercise the importer (E08-T01…T07) with
realistic structure and known defects. Code: `tools/Opportunity.DataGenerator.Corpus/Volumes` (`VolumeWriter`).

```bash
# 10K-document clean volume (E08-T03: must import with 0 errors)
opportunity-datagen generate --seed 42 --documents 10000 --out ./vol-10k --volumes

# Same corpus as UTF-16LE "vendor" DAT, US dates in New York winter time, 2% of every defect, 5% overlay
opportunity-datagen generate --seed 42 --documents 10000 --out ./vol-10k-defects --volumes \
  --dat-preset vendor --dat-encoding utf-16le --date-format us --time-zone-offset -05:00 \
  --defect-rate 0.02 --defect missingNative=0.05 --defect badDate=0.01 --overlay-rate 0.05
```

| Option | Default | Meaning |
|---|---|---|
| `--volumes` | off | Write volumes and the volume ground truth |
| `--dat-preset` | `concordance` | `concordance` (column U+0014, quote `þ`, newline `®`), `vendor` (column `¶`, quote `þ`, newline `®`), `csv` (RFC 4180) |
| `--dat-encoding` | `utf-8-bom` | `utf-8`, `utf-8-bom`, `utf-16le` (with BOM), `windows-1252` (unrepresentable characters become `?`) |
| `--text-encoding` | `utf-8` | Same choices, for extracted-text files, independent of the DAT |
| `--date-format` | `iso` | `iso` = `yyyy-MM-ddTHH:mm:ss±hh:mm` in the value's own offset; `us` = `MM/dd/yyyy hh:mm:ss tt`; `eu` = `dd/MM/yyyy HH:mm:ss` (date fields: date part only) |
| `--time-zone-offset` | `+00:00` | Zone that `us`/`eu` DateTime values are converted to (they carry no offset; it is the import's "source time zone", ADR-009 R22) |
| `--image-format` | `auto` | `auto` = single-page TIFF G4, JPG for image natives; `tiff`, `jpg`, `png`, `multipage-tiff` (one file and one OPT row per document) |
| `--volume-prefix`, `--docs-per-volume` | `VOL`, 0 | `VOL001`, `VOL002`… ; a new volume starts at a family boundary once the current one holds ≥ N documents (0 = one volume) |
| `--files-per-folder` | 1000 | Files per `IMAGES/IMG0001`, `NATIVES/NATIVE0001`, `TEXT/TEXT0001` folder; a document's pages are never split, so a document with more pages than the limit gets its own folder |
| `--no-natives`, `--no-text`, `--no-images` | off | Skip an artifact kind (its link column stays empty / no OPT rows) |
| `--native-text-chars` | 2000 | Characters of extracted text embedded in each native |
| `--overlay-rate` | 0 | Share of documents also listed in `DATA/<VOL>_OVERLAY.dat` |
| `--defect-rate` | 0 | Rate for every defect type; `--defect <type>=<rate>` (repeatable) overrides one type |

### Layout

```
<out>/VOL001/DATA/VOL001.dat            DAT (header row + one row per document, CRLF rows)
<out>/VOL001/DATA/VOL001.opt            Opticon OPT (ASCII)
<out>/VOL001/DATA/VOL001_OVERLAY.dat    overlay DAT (with --overlay-rate)
<out>/VOL001/IMAGES/IMG0001/OPP0000000001.tif, OPP0000000001.0002.tif …
<out>/VOL001/NATIVES/NATIVE0001/OPP0000000001.eml …
<out>/VOL001/TEXT/TEXT0001/OPP0000000001.txt …
<out>/volume-manifest.json   settings, DAT columns, per-volume counts + load-file SHA-256, per-defect target/eligible/injected/actual rate
<out>/volume-defects.jsonl   ground truth: one line per injected defect
<out>/volume-overlays.jsonl  ground truth: expected values after the overlay
```

Ground truth sits outside the volume folders so an importer pointed at `VOL001` never sees it. The DAT, OPT,
overlay, defects, overlays and manifest files are also listed (bytes + SHA-256) in `corpus-manifest.json`.

**DAT columns:** `ControlNumber, BegAttach, EndAttach, ParentID, GroupIdentifier, Custodian, AllCustodians,
DuplicateCustodians, DuplicateGroupID, EmailThreadGroup, MD5Hash, SHA256Hash, NativeLink, TextLink`, then the
field catalog in order. Families can therefore be rebuilt by range (A), pointer (B: `ParentID` = immediate parent)
or group (C: `GroupIdentifier` = parent control number) per ADR-009; standalone documents have
`BegAttach = EndAttach = ControlNumber`. Every value is qualified; a quote character inside a value is escaped by
doubling it; line breaks become `®` (CRLF, CR and LF each one `®`) except for `csv`, which keeps them literally
inside the quotes. As in any Concordance load, a literal `®` in a value is indistinguishable from an encoded line
break. Multi-value fields are joined with `; `; booleans are `Y`/`N`; links are volume-relative with `\`.

**OPT rows:** `ImageKey,Volume,Path,DocBreak,FolderBreak,BoxBreak,PageCount`, one row per page, `Y` and the page
count on the first row only. Page 1's key is the control number (E08-T05 matches the break row to the document);
page *n* ≥ 2 is `<ControlNumber>.<nnnn>`. Image files are named by the page key. Documents without a page count
(zip containers) have no images. Page counts come from the corpus `PageCount` field (1–2,000).

**Page images** are hand-encoded, blank and deterministic (no imaging library): bitonal CCITT G4 TIFF
2550 × 3300 at 300 DPI (~0.5 KB per page; multi-page TIFF has one IFD per page), baseline grayscale JPG and 1-bit
PNG 850 × 1100 at 100 DPI. Each carries its page key in the description/comment.

**Natives** are built from the document content and the first `--native-text-chars` of its extracted text:

| Corpus type | Native written |
|---|---|
| `msg` (all emails) | RFC 5322 `.eml`: From/To/Cc/Subject/Date/Message-ID/In-Reply-To (RFC 2047 for non-ASCII), base64 UTF-8 body. Written as `.eml` because a valid Outlook MSG (CFB) is not feasible here; `FileExtension` stays `msg` |
| `pdf` | One-page PDF 1.4 (Helvetica, ASCII text; other characters as `?`) |
| `docx`, `xlsx` | Minimal OOXML packages (stored ZIP; one paragraph / one row per text line) |
| `zip` | Stored ZIP with a `contents.txt` |
| `txt`, `csv`, `html` | The text excerpt (CSV as `Row,Text` rows, HTML in `<pre>`) |
| `jpg`, `png` | Blank 320 × 240 image |
| `pptx` (and any other type) | **Placeholder**: text file with the claimed extension starting `OPPORTUNITY SYNTHETIC PLACEHOLDER NATIVE` (a valid presentation needs master/layout/theme parts; counted as `placeholderNatives`) |

Every native embeds the document's content key, so different contents never produce identical bytes.

### Native hash rule

`MD5Hash` and `SHA256Hash` in the DAT are computed over the exact bytes written to `NATIVES`, and `FileSize` is
their length. Native bytes are a pure function of the document **content** (content key, file type, content
metadata, text excerpt) — never of the copy's placement (control number, custodian, path) — so every duplicate
copy gets byte-identical natives and the DAT hash groups are exactly the corpus duplicate groups (verified by
`A_clean_volume_round_trips_every_value_link_hash_and_page`). The `md5`/`sha256` in `documents.jsonl` stay
synthetic identity hashes. With `--no-natives` the synthetic hashes and the simulated `FileSize` are written.

### Defects

Each type has an eligibility rule; its rate is the share of eligible items that get the defect. Selection is
deterministic "jittered systematic" sampling (`DefectSchedule`): eligible items are cut into intervals of 1/rate
and one seeded random item is picked per interval, so `injected = eligible × rate ± 1` at any corpus size while
positions stay irregular. Types use independent streams and may co-occur on one row. Every injected defect is one
line in `volume-defects.jsonl` (`type`, `volume`, `controlNumber` as it appears in the DAT/OPT, plus the fields
below); the manifest lists `targetRate`, `eligible`, `injected` and `actualRate` per type.

| Type | Eligible | What is written | Extra ground-truth fields |
|---|---|---|---|
| `orphanAttachments` | families with ≥ 2 documents | parent's row, files and OPT rows withheld; attachments still point to it | `orphanCount`, `orphans` |
| `brokenFamilyRange` | families with ≥ 2 documents | every member's BegAttach/EndAttach is `truncated` (last member outside), `reversed`, or `prefixMismatch` (`X` + EndAttach) | `variant`, `begAttach`, `endAttach`, `expected…`, `familySize` |
| `duplicateControlNumber` | standalone documents after the first | ControlNumber and OPT keys repeat the previous standalone row's (files keep the original name) | `originalControlNumber`, `datRow` |
| `missingNative` | documents (natives on) | NativeLink written, file absent | `path` |
| `hashMismatch` | documents whose native was written | DAT MD5Hash/SHA256Hash differ from the file | `datMd5`, `actualMd5`, `datSha256`, `actualSha256`, `path` |
| `missingText` | documents (text on) | TextLink written, file absent | `path` |
| `textEncoding` | written text files that would differ: any for UTF-16LE volumes, otherwise those with non-ASCII characters | file written as Windows-1252 (UTF-8 for 1252 volumes), no BOM | `declaredEncoding`, `actualEncoding`, `path` |
| `missingImage` | imaged documents | one page file absent (the whole file for multi-page TIFF; `page` 0) | `path`, `page` |
| `optPageCountMismatch` | imaged documents | break-row PageCount off by 1–3 | `optLine`, `declaredPages`, `actualPages` |
| `badDate` | rows with a date value | one of DateSent/DateReceived/DateCreated/DateLastModified/RecordDate replaced by an unparseable value (`2019-02-30`, `13/32/2018`, `N/A`…) | `datRow`, `field`, `value`, `expected` |
| `unescapedQualifier` | every row | a raw quote character in the middle of Subject (else FileName), not doubled | `datRow`, `field` |
| `fieldCountMismatch` | every row | one field more or fewer than the header | `datRow`, `expectedFields`, `actualFields` |
| `datRowEncoding` | rows containing non-ASCII in UTF-8/1252 DATs (all Concordance rows: `þ`) | row encoded as Windows-1252 in a UTF-8 file, or UTF-8 in a 1252 file | `datRow`, `declaredEncoding`, `actualEncoding` |
| `overlayUnknownKey` | overlay rows | extra overlay row whose key (`<ControlNumber>X`) is not in the volume | `overlayRow` |

`datRow` is the 1-based data-row number in that volume's DAT (header excluded); `optLine` is the 1-based OPT line.
Overlay rows (`ControlNumber, Confidentiality, ProjectCode`) change Confidentiality to a different value and
blank ProjectCode (exercises the "blank values overwrite" option, E08-T07); `volume-overlays.jsonl` lists the new
and previous values per row. Only documents written under their own control number are overlay candidates.

### Determinism, memory and throughput

Volumes are byte-identical for the same (seed, profile, options, writer version) across runs and `--threads`
values (`Volumes_are_byte_identical_…`); defect choices are keyed by `hash(seed, type, index)`. Bump
`VolumeWriter.Version` when output for unchanged inputs changes. The writer holds one document (plus its native,
at most a few KB) at a time and keeps no per-corpus state, so memory is the corpus generator's bounded window.
Measured peak RSS (Release, 4 threads, multi-page TIFF, 1% defects, 5% overlay): 188 MB at 20K documents with
text, 233 MB at 100K documents without text (~810 docs/s). The ≤ 2 GB at 10M documents target follows from
this flat profile but was not run here (it needs roughly 0.7 TB of disk with text).
Throughput is dominated by file creation: page images (mean ~18 pages per document with default text sizes) are
millions of files at 1M documents; use `--image-format multipage-tiff` or `--no-images` for large metadata/text
benchmarks.
