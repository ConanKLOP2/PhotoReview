# RAW-60 Benchmark — progress
Branch: feat/raw-support-integration · PR: #239 (umbrella draft) · Agent model: inherited; model name not exposed
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [ ] 1. Add bounded `--decoder-bench --raw` measurements for cold header parse, preview decode at FHD/2K/4K, full LibRaw decode, and direct decode of the same embedded JPEG preview.
- [ ] 2. Add unit coverage for argument parsing and report/statistics behavior; mutation-check the tests.
- [ ] 3. Run corpus benchmark; record raw results and limits in a new perf fragment and add one PERF-STATUS bullet.

## Next action
Implement the RAW benchmark mode and CLI dispatch; then add focused tests before running the potentially long corpus benchmark.

## Evidence / measurements
Corpus currently contains RAW examples across CR2, CR3, NEF, ARW, DNG, RAF, ORF, and RW2; it does not contain same-camera standalone JPEGs. A defensible same-source comparison is the selected embedded JPEG decoded directly versus RAW preview routing at the same target box. This does not compare against full-resolution camera JPEG files.

## Open problems
None yet.
