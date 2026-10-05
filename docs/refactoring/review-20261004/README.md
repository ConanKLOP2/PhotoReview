# Whole-project function review (2026-10-04) - folder guide

Static review of every callable body in the repository, landed from the (still open, untouched) review branch of PR #284 and reconciled with `origin/master`. **Start with [WORK-REVIEW.md](WORK-REVIEW.md)**: its status table at the top gives the current state (FIXED / OPEN / ...) of every finding. Everything else here is supporting evidence.

## Files

| File | What it is |
|---|---|
| [WORK-REVIEW.md](WORK-REVIEW.md) | Findings R01..R44, APP-*, PW-01, test-oracle findings, with a status table reconciled to current master. |
| `functions.tsv.gz` | Per-callable ledger (16,799 rows), gzip of the original `functions.tsv` (7.0 MB raw, 0.6 MB gz; UTF-8, LF, header row, every field double-quoted). Columns: `Id, Path, Line, EndLine, Kind, Function, Status, Rationale`. Byte-exact: SHA-256 of the decompressed stream is `0233D08875E92D404DC3E5CE1AF84FFBA3B485D8CB39C8A9780C4085A5786479`. |
| `functions-issues.tsv` | Header plus only the 186 `ISSUE` rows of the ledger (a plain-text slice for browsing and diffs). |
| `current-master-delta.tsv` | 127 callable rows changed by #321-#324 (baseline `18aa6eb6` -> `3694dbb3`), 126 STATIC-ONLY and 1 ISSUE (R44, since fixed by #325). |
| `powershell-functions.tsv` | 52 named PowerShell functions in 15 scripts (all read statically). |
| `wave2-vs-baseline-disagreements.tsv` | The 123 rows where the two ledgers disagree on ISSUE vs not-ISSUE (columns: Path, Function, Wave2Verdict, BaselineStatus, Wave2Ledger), listed for review. |
| [validation.md](validation.md), [current-delta-20261004.md](current-delta-20261004.md), [app.md](app.md) | Original validation record, delta reconciliation notes and App baseline details from #284 (historical; copied as-is, so their "open"/"remains" wording is a snapshot, see WORK-REVIEW.md for current status). |
| [evidence/](evidence/README.md) | Repro drafts and probes (`.cs.txt`/`.ps1`/`.json`, never compiled by the build). |
| [wave2/](wave2/) | Independent second review ledgers written by other agents (already on master; not modified here). |

## How to read the gz

PowerShell:

```powershell
$in = [IO.File]::OpenRead('functions.tsv.gz')
$gz = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
$sr = New-Object IO.StreamReader($gz, [Text.Encoding]::UTF8)
$text = $sr.ReadToEnd(); $sr.Dispose()
$rows = $text | ConvertFrom-Csv -Delimiter "`t"          # objects with Id, Path, ..., Status
$rows | Group-Object Status | Select Name, Count
```

bash:

```bash
gzip -dc functions.tsv.gz | head -3
gzip -dc functions.tsv.gz | awk -F'\t' 'NR>1 {print $7}' | sort | uniq -c
```

## Verdict meaning

- `STATIC-ONLY`: a saved semantic **static source review** (read the body against its callers/tests, including inherited enclosing-function rationale for tiny helpers and lambdas). It is **not** a runtime PASS: no GUI, native decoder, real Recycle Bin, NAS or RAW corpus run backs it, and for most rows no test was run for the review.
- `ISSUE`: the callable is *affected by* a finding in WORK-REVIEW.md. The count of ISSUE rows is not a count of independent bugs, and (because the ledger is pinned to the baseline) many ISSUE rows point at code that has since been fixed. Use the WORK-REVIEW status table, not this column, for the current state.
- `UNREVIEWED`: none at the pinned baseline.

## Baseline and counts

- Ledger baseline: `18aa6eb64ba48666de620937211e0782dd6131c1` (#320); the audit began at `5d291076`. Line numbers in `functions.tsv` refer to that baseline.
- Row counts, recounted from the original file (and equal in the `.gz` after round trip): **16,799 data rows = 16,613 `STATIC-ONLY` + 186 `ISSUE` + 0 `UNREVIEWED`** (16,800 lines with the header). `functions-issues.tsv` has **186 data rows** (187 lines with the header), all ISSUE, which is the exact ISSUE count of the ledger.
- #284's text also quotes a current-#324 total (16,893 rows: 16,721 STATIC-ONLY, 172 ISSUE, 0 UNREVIEWED), obtained by applying "33 reviewed baseline-ID overrides" and the 127 delta rows to the baseline. The 33 overrides are **not** stored in any file of #284, so the 16,721/172 split could not be reproduced and is not used here. `functions.tsv` is the baseline ledger; `current-master-delta.tsv` holds the delta rows.
- Master has moved on since (many fixes through #326); this folder was reconciled against `origin/master` as stated in WORK-REVIEW.md.

## Relation to `wave2/`

`wave2/ledger-*.tsv` (1,613 rows, verdicts OK / ISSUE / NEEDS-EVIDENCE) were written independently by other agents on master `d24b830d`, with their own numbering. **The `F#####` ids of the two ledgers are not shared** (the same id is a different function in each), so do not join on Id. Join on `Path` + `Function` (signature text): 1,459 of the 1,613 wave2 rows match a row of `functions.tsv` that way; the rest were added, renamed or moved after the baseline. Verdicts were written independently and differ on many rows (123 of the 1,459 matched rows disagree on ISSUE vs not-ISSUE), because each review had different scope and the code changed in between. On a conflict, prefer the newer status in WORK-REVIEW.md (and the wave2 findings files for wave2-only findings); neither ledger overrides the other automatically.

**How to treat the 123 disagreements** (listed in wave2-vs-baseline-disagreements.tsv): 53 are wave2 ISSUE vs baseline STATIC-ONLY (wave2 found something the baseline pass did not; examples: DragDropInputService.Parse, ExplorerReason.Format, SessionWriter, FileLog, PerfAnalysis report code, RawSurvey/DecoderBenchmark), 64 are wave2 OK vs baseline ISSUE and 6 wave2 NEEDS-EVIDENCE vs baseline ISSUE. The OK-vs-ISSUE rows are mostly FileActionService (20), UndoService (14), RecoveryRetryService (8) and OperationJournal (8): the baseline pass flagged them for R01/R15/R17/R18, which master fixed afterwards (wave2 reviewed a later master) or, for the R01a-c residuals, wave2 judged OK on the assumption that nothing changes after the creation proof, which WORK-REVIEW.md still lists as OPEN. Rule: take the status from the newest source: [WORK-REVIEW.md](WORK-REVIEW.md) for #284 findings, the wave2 indings-*.md for wave2 findings; if both are silent, the row is simply unresolved and should be re-read before relying on either verdict.

Note: three files copied from #284 (pp.md, current-delta-20261004.md, and the historical section of WORK-REVIEW.md) had their link to unctions.tsv changed to unctions.tsv.gz (the 7 MB raw file is not committed); nothing else in them was edited.
