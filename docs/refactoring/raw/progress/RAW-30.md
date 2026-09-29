# RAW-30 WIC full-decode path — progress
Branch: feat/raw-support-integration · PR: #239 · Agent model: Codex
Last update: 2026-09-29 · State: READY FOR REVIEW

## Steps
- [x] 1. Detect WIC RAW decoder registration once per format and cache the result.
- [x] 2. Decode through WIC with container orientation; reject results equal to the embedded preview dimensions.
- [x] 3. Record the per-format probe matrix using the RAW corpus Native test.

## Evidence
- `WicRawFullDecoder.IsCodecAvailable` probes WIC registry registration and caches available/unavailable probe results by `RawFormat`.
- Full decode passes container orientation as `SourceOrientation`; a result matching the largest embedded preview dimensions is rejected. DNG files without embedded JPEG previews remain eligible for WIC decode.
- Unit tests: 5 passed. Native corpus matrix: 1 passed over all 23 samples and 8 formats.
- Full standard solution filter: 4,284 passed, 0 failed. Release solution build: 0 warnings, 0 errors.
- The sample corpus was fetched and SHA-256 verified by `tools/fetch-raw-samples.ps1`; files are ignored and excluded from commits.
- Mutation checks killed 3 broken variants: uncached availability probes, omitted orientation override, and disabled preview-size rejection.

## Next action
Continue with RAW-31 (LibRaw, required by Q-RAW-02=A), then RAW-32 zoom integration.

## Open problems
- WIC could not full-decode 20 of the 23 samples on this machine; LibRaw remains required per Q-RAW-02.
