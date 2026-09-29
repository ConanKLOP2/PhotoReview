# RAW-30 WIC full-decode path — progress
Branch: feat/raw-support-integration · PR: #239 · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Detect WIC RAW decoder registration once per format and cache the result.
- [x] 2. Decode through WIC with container orientation; reject results equal to the embedded preview dimensions.
- [ ] 3. Record the per-format probe matrix using the RAW corpus Native test.

## Evidence
- `WicRawFullDecoder.IsCodecAvailable` uses the WIC decoder registry and caches both available and unavailable results by `RawFormat`.\r\n- Full decode passes container orientation as `SourceOrientation`; a result matching the largest embedded preview dimensions is rejected. DNG files without embedded JPEG previews remain eligible for WIC decode.
- Unit test verifies one probe call per format, including cached negative results.

## Next action
Add/run the corpus probe matrix and append the per-format availability results to `SURVEY.md`.

## Open problems
- The ignored RAW corpus is absent from this worktree, so Native verification will need to skip or use a separately provisioned corpus.
