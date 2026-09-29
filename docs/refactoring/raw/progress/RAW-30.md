# RAW-30 WIC full-decode path — progress
Branch: feat/raw-support-integration · PR: #239 · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Detect WIC RAW decoder registration once per format and cache the result.
- [ ] 2. Decode through WIC with container orientation; reject results equal to the embedded preview dimensions.
- [ ] 3. Record the per-format probe matrix using the RAW corpus Native test.

## Evidence
- `WicRawFullDecoder.IsCodecAvailable` uses the WIC decoder registry and caches both available and unavailable results by `RawFormat`.
- Unit test verifies one probe call per format, including cached negative results.

## Next action
Add the full-decode path and preview-only rejection, then add/run the corpus probe matrix.

## Open problems
- The ignored RAW corpus is absent from this worktree, so Native verification will need to skip or use a separately provisioned corpus.
