# RAW-01 Sample corpus, survey tool, WIC probe — progress
Branch: feat/raw-01-survey · PR: not yet · Agent model: sonnet
Last update: 2026-09-29 00:03 · State: IN PROGRESS

## Steps
- [x] 1. Build pinned list: >=2 bodies per format of Q-RAW-05 A (23 bodies across 8 formats) from raw.pixls.us (all CC0), added `tools/raw-samples.txt` and `.gitignore` entry
- [ ] 2. `fetch-raw-samples.ps1`: download, verify SHA-256, idempotent, PS 5.1 compatible  ← CURRENT
- [ ] 3. `--raw-survey <dir>`: parse format, size, orientation, embedded JPEGs, sensor size
- [ ] 4. WIC probe: `WicDirectDecoder` ReadInfo + full Decode, time, dimensions, compare with preview
- [ ] 5. Write `SURVEY.md`: per-format tables + summary analysis

## Next action (for whoever resumes)
Implement `tools/fetch-raw-samples.ps1` with SHA-256 validation and fail-closed logic, and test with self-test on 1-file list with wrong hash.

## Evidence / measurements
Selected 23 samples across CR2 (3), CR3 (2), NEF (3), ARW (3), DNG (3), RAF (3), ORF (3), RW2 (3), all verified CC0 license on raw.pixls.us.

## Open problems
None so far.
