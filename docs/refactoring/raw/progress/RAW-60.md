# RAW-60 Benchmark — progress
Branch: feat/raw-support-integration · PR: #239 (umbrella draft) · Agent model: inherited; model name not exposed
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Add `--decoder-bench --raw` measurements for cold header parse, preview decode at FHD/2K/4K, full LibRaw decode, and direct decode of the same embedded JPEG preview — `db042a9d`.
- [x] 2. Add argument parsing unit tests; mutation-check default iterations by changing 3→4, confirmed test failure, then restored source.
- [ ] 3. Run corpus benchmark; record raw results and limits in a new perf fragment and add one PERF-STATUS bullet.

## Next action
Run the benchmark on `tests/Fixtures/raw-corpus` with one iteration to establish actual corpus behavior and duration; review CSV/JSON failures and report results.

## Evidence / measurements
`dotnet build tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release --no-restore`: 0 warnings/errors. Integration `BenchmarkCliArgumentsTests`: 54 passed. Mutation 3→4 made the default-iteration test fail (expected 3, actual 4).

Corpus currently contains RAW examples across CR2, CR3, NEF, ARW, DNG, RAF, ORF, and RW2; it does not contain same-camera standalone JPEGs. A defensible same-source comparison is the selected embedded JPEG decoded directly versus RAW preview routing at the same target box. This does not compare against full-resolution camera JPEG files.

## Open problems
None yet.
