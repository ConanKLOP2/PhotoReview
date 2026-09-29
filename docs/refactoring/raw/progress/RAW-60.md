# RAW-60 Benchmark — progress
Branch: feat/raw-support-integration · PR: #239 (umbrella draft) · Agent model: inherited; model name not exposed
Last update: 2026-09-29 · State: READY FOR REVIEW

## Steps
- [x] 1. Add `--decoder-bench --raw` measurements for cold header parse, preview decode at FHD/2K/4K, full LibRaw decode, and direct decode of the same embedded JPEG preview — `db042a9d`.
- [x] 2. Add argument parsing unit tests; mutation-check default iterations by changing 3→4, confirmed test failure, then restored source.
- [x] 3. Run one-iteration corpus benchmark, record results and limits in perf fragment + index bullet — `2026-09-29-raw-60-decoder-bench.md`.

## Next action
RAW-60 is complete. Continue with RAW-61 quality-gate test coverage after verifying this commit is on the feature branch.

## Evidence / measurements
`dotnet build tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release --no-restore`: 0 warnings/errors. Integration `BenchmarkCliArgumentsTests`: 54 passed. Mutation 3→4 made the default-iteration test fail (expected 3, actual 4). Corpus run: 23 RAW files, 23/23 header parse; 19/22 preview decode/direct embedded JPEG at all widths; 22/22 LibRaw decode. Medians and caveats are in `docs/refactoring/perf/2026-09-29-raw-60-decoder-bench.md`.

`tools/verify-all.ps1 -Hidden`: PASS — Release build 0 warnings/errors; Architecture 64, Core 1780, Imaging 642, Integration 641, App 1199 passed; release publish and verification passed. Docs budget and link checks passed. Translation check has the existing unused Vietnamese key warning `status.noSupportedImagesButSubfolders.one`.

Corpus currently contains RAW examples across CR2, CR3, NEF, ARW, DNG, RAF, ORF, and RW2; it does not contain same-camera standalone JPEGs. A defensible same-source comparison is the selected embedded JPEG decoded directly versus RAW preview routing at the same target box. This does not compare against full-resolution camera JPEG files.

## Open problems
None yet.
