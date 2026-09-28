# RAW-01 Sample corpus, survey tool, WIC probe — progress
Branch: feat/raw-01-survey · PR: #238 · Agent model: sonnet
Last update: 2026-09-29 00:18 · State: READY FOR REVIEW

## Steps
- [x] 1. Build pinned list: >=2 bodies per format of Q-RAW-05 A (23 bodies across 8 formats) from raw.pixls.us (all CC0), added `tools/raw-samples.txt` and `.gitignore` entry — 9ea20e81
- [x] 2. `fetch-raw-samples.ps1`: download, verify SHA-256, idempotent, PS 5.1 compatible, self-test fails closed on hash mismatch — e7ae7db2
- [x] 3. `--raw-survey <dir>`: parse format, size, orientation, embedded JPEGs, sensor size via marker scan
- [x] 4. WIC probe: `WicDirectDecoder` ReadInfo + full Decode, time, dimensions, compare with preview
- [x] 5. Write `SURVEY.md`: per-format tables + summary analysis

## Next action (for whoever resumes)
PR #238 is ready for lead/user review and merge. After merge, Wave 0 is complete (RAW-00 + RAW-01 gate met), proceed to Wave 1 (RAW-10).

## Evidence / measurements
- `fetch-raw-samples.ps1 -SelfTest`: PASS (rejected corrupted hash 0000... and cleaned up).
- All 23 samples downloaded and verified via SHA-256 into `tests/Fixtures/raw-corpus/`.
- Survey command executed: `tools/PhotoReview.Benchmark.Cli --raw-survey tests/Fixtures/raw-corpus --markdown docs/refactoring/raw/SURVEY.md`.
- Key findings in `SURVEY.md`:
  - Canon CR2 & CR3: 100% full-resolution preview embedded (EOS 350D 8MP, 7D 18MP, 5D IV 30MP, M50 24MP).
  - Nikon NEF: 100% full-resolution preview embedded (D40X 10MP, D800 36MP, Z 7 45MP).
  - Sony ARW: ONLY embeds 1616×1080 (1.7 MP) preview across NEX-6, A7R, A7 III. Empirically validates Q-RAW-02/03 necessity of LibRaw full decode for 100% zoom.
  - Fujifilm RAF: X-Trans II/III embed 1920×1280 preview; X-Trans IV (X100V) embeds 4416×2944.
  - Olympus ORF: embeds 3200×2400 preview.
  - Panasonic RW2: embeds 1920×1440 / 1920×1280 preview.
  - WIC Probe: Fails without Microsoft Raw Image Extension on CR2, CR3, NEF, ARW, ORF, RAF, RW2 (error 0x88982F50/0x88982F8B). Confirms Q-RAW-02 recommendation (LibRaw).
- Tests: `RawSurveyTests` (2 passed, mutation tested: broke SOF width -> test failed).
- Full solution build: 0 warnings, 0 errors. Full test filter: 4,225 passed, 0 failed.

## Open problems
None.
