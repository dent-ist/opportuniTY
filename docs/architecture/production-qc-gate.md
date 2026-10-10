# Production QC gate, finalization and manifest (E12-T07)

Every finalization of a production runs the **QC gate** first, inside the finalization transaction, under the Bates
prefix lock and the exclusive privilege gate, over the members as they are at that moment (the state finalization
freezes). The same checks run on request (`POST …/productions/{id}/qc`, `Production.Create`) as the validation summary
before finalizing. Volume runs (production-volumes.md) follow finalization (Q-79); the gate applies the burn-in rules
(Q-80) before it.

## Checks

| Check | Severity | Override | Finds |
|---|---|---|---|
| `withheldWithoutPlaceholder` | blocking | no | members coded Privilege Status = Withhold that are not placeholders (Q-77) |
| `placeholderNoLongerWithheld` | blocking | no | withheld placeholders whose call changed after allocation (allocate again) |
| `redactWithoutRedactions` | blocking | yes | members coded Redact with no redactions in the production's Redaction Set |
| `redactedNative` | blocking | no | redacted members produced natively (burn-in `NativeShipped`, Q-22) |
| `redactionsNotProducible` | blocking | no | redactions on another page set or beyond the page set (burn-in `PageMissing`) |
| `renderFailure` | blocking | yes | image members with pages without a stored image, native members without a native (Technical Issue pages) |
| `pageCountChanged` | blocking | no | page counts changed since the Bates numbers were allocated |
| `batesOverlap` | blocking | no | members whose numbers lie in another production's live range of the prefix |
| `privilegeConflicts` | blocking | yes (`PrivilegeLog.Generate`) | members whose family mixes Withhold with other calls or whose duplicate group's calls differ (E13-T02) |
| `designationNotProducible` | blocking | no | unlisted designation levels, or designated members no endorsement stamps (E12-T04) |
| `inaccessible` | blocking | no | members the person finalizing may not produce (per-document re-check, Q-15) |
| `incompleteFamily` | warning | — | members whose family has live documents outside the production |
| `textMissing` | warning | — | members without extracted text when text is delivered (redacted and placeholder members excluded) |
| `blankConfidentiality` | warning | — | members without a designation when the production designates |

A failed blocking check refuses the finalization (409 with the run's report in `qc`; reason `PRIVILEGE_WITHHELD`,
`BATES_OVERLAP`, `PRIVILEGE_CONFLICTS`, `DESIGNATION_REFUSED`, `QC_FAILED` or `QC_WARNINGS`) unless it may be
overridden and the request gives `qcOverrides` with a reason (`privilegeConflictOverride` stays as the E13-T02 form).
Warnings need `acknowledgeWarnings`. Each override is audited (`Production.QcOverride`, and `Privilege.ConflictOverride`
for privilege conflicts), printed in the QC report and recorded in the manifest. Every run, passed or blocked, is
audited as `Production.QcRun`.

The specification option `withheldDocuments: placeholder` makes documents coded Withhold at allocation "Withheld"
placeholders (one Bates number, one DAT row); the default `block` keeps them and the gate refuses.

## Storage and the QC report

Each run is a `production_qc_run` row (canonical report JSON and its SHA-256) with its document-level exceptions in
`production_qc_exception` (V0058). Both are append-only for the application, preserved under a legal hold and kept by the
RetainRecords deletion profile. The passed finalization run is named in the production manifest (`qc`: run id, report
SHA-256, outcome, overrides with reasons, acknowledged warnings). `GET …/qc` returns the latest run (a finalized
production's is its finalization run), `GET …/qc/exceptions` pages the exceptions, and `GET …/qc/report?format=csv|pdf`
(gateway, audited `Production.Downloaded`) renders the report. Documents the caller may not view are never listed; they
are counted, as the Bates lookup does.

## Volume reconciliation and hashes

Before a volume run completes, its stored DAT and OPT are read back and reconciled: images = OPT rows =
Σ(ProdEnd − ProdBeg + 1) at page level, natives = DAT NativeLink values, text files = DAT TextLink values, DAT rows =
members. A run that does not reconcile fails (`Production.VerificationFailed`, `Check = Reconciliation`) and is never
delivered. `MANIFEST.json` records the reconciliation and `totals.fileListSha256`, the SHA-256 of `MANIFEST.csv` (path,
size and SHA-256 of every delivered file), as the volume-level hash. Re-runs stay byte-identical (Q-08).
