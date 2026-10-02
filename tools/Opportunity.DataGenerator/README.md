# Opportunity.DataGenerator

Seeded, deterministic generator for **synthetic** eDiscovery corpora (E17-T01, baseline §18/§29). Every name,
address, word and value is invented: people are built from syllables and script samples, domains use the reserved
`.example` TLD, and text comes from a pseudo-word Zipf vocabulary. Never load real or "anonymized" client data
(test-strategy §7).

| Project | Role |
|---|---|
| `tools/Opportunity.DataGenerator` | CLI (`opportunity-datagen`) |
| `tools/Opportunity.DataGenerator.Corpus` | Generation model as a library (planner, chunk generator, text synthesizer, writers, statistics). Tooling only: `src/` must never reference it (enforced by `ToolingIsolationTests`). |
| `tests/Opportunity.DataGenerator.Tests` | Determinism, distribution, integrity, text/needle and streaming-memory tests |

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
they are not hashes of bytes written here.

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

## Extension point (E17-T02 load-file volumes)

Implement `ICorpusSink` (receives `GeneratedChunk`s in load order on one thread; must stream) and pass it via
`CorpusRunOptions.ExtraSinks`. Use `TextSynthesizer.Write(doc.Content.Text, sink)` to stream extracted text, and
`FieldCatalog` for DAT column order. DAT/OPT/TXT/NATIVES/IMAGES writers and defect injection are not part of
E17-T01.
