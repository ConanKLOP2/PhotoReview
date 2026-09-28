# RAW-01 Sample corpus, survey tool, WIC probe — progress
Branch: feat/raw-01-survey · PR: #238 · Agent model: sonnet
Last update: 2026-09-29 00:07 · State: IN PROGRESS

## Steps
- [x] 1. Build pinned list: >=2 bodies per format of Q-RAW-05 A (23 bodies across 8 formats) from raw.pixls.us (all CC0), added `tools/raw-samples.txt` and `.gitignore` entry — 9ea20e81
- [x] 2. `fetch-raw-samples.ps1`: download, verify SHA-256, idempotent, PS 5.1 compatible, self-test fails closed on hash mismatch
- [ ] 3. `--raw-survey <dir>`: parse format, size, orientation, embedded JPEGs, sensor size  ← CURRENT
- [ ] 4. WIC probe: `WicDirectDecoder` ReadInfo + full Decode, time, dimensions, compare with preview
- [ ] 5. Write `SURVEY.md`: per-format tables + summary analysis

## Next action (for whoever resumes)
Implement `--raw-survey <dir>` in `tools/PhotoReview.Benchmark.Cli/RawSurvey.cs` and wire into `Program.cs`.

## Evidence / measurements
- `fetch-raw-samples.ps1 -SelfTest`: PASS (correctly rejected mismatch hash 0000... and cleaned up temp files).
- `fetch-raw-samples.ps1 -Limit 1`: downloaded Canon EOS 350D sample (10.10 MB), verified SHA-256 `8cbb84e04d93b005fe082da9c954122a612b5281af00aa088d767850f343fd38`.
- Rerun verified idempotency (skipped file with matching SHA-256).

## Open problems
None.
