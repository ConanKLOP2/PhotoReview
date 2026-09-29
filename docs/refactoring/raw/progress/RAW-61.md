# RAW-61 Quality gate — progress
Branch: feat/raw-support-integration · PR: #239 (umbrella draft) · Agent model: inherited; model name not exposed
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [ ] 1. Cover all 8 EXIF orientations, per-format parse dimensions, malformed/truncated RAW, INV-8 release, and INV-9 pixel format.
- [ ] 2. Verify Adobe RGB embedded-preview conversion against a known ICC-bearing reference; repair conversion if the gate exposes a gap.
- [ ] 3. Run targeted tests, mutation checks, full gate, and update this handoff.

## Next action
Extend the RAW quality tests and inspect the current preview decode color path. Q-RAW-06 is decided as Adobe RGB to sRGB through a bundled CC0 ICC profile.

## Evidence / measurements
Existing tests cover parser routing and selected orientations, LibRaw Bgr32 output, JPEG handle release/corrupt inputs, and display profile conversion for ordinary JPEG. RAW pipeline lacks a full 8-orientation matrix and Adobe-compatible embedded preview profile.

## Open problems
None yet.
