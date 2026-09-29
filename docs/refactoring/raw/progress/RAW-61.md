# RAW-61 Quality gate — progress
Branch: feat/raw-support-integration · PR: #239 (umbrella draft) · Agent model: inherited; model name not exposed
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Cover all 8 EXIF orientations, all major corpus format dimensions, malformed/truncated RAW, INV-8 release, and Bgr32 pixel format.
- [x] 2. Verify Adobe RGB embedded-preview conversion against a known ICC-bearing reference and inject the bundled CC0 Adobe-compatible profile where a preview is tagged Adobe RGB but lacks ICC data.
- [ ] 3. Run targeted tests, mutation checks, full gate, Native corpus test, and update this handoff.

## Next action
Extend the RAW quality tests and inspect the current preview decode color path. Q-RAW-06 is decided as Adobe RGB to sRGB through a bundled CC0 ICC profile.

## Evidence / measurements
Targeted `RawDecoderTests`: 19 passed. Adobe test strips ICC data from the preview while retaining the RAW Adobe RGB marker, then compares RawDecoder output with the same JPEG carrying its Adobe-compatible profile. Mutation changing the RAW Adobe-profile branch to sRGB made the Adobe quality assertion fail (PSNR 20.88 dB vs required ≥50 dB); source restored.

## Open problems
The first isolated corpus Native test exceeded its 120-second hang guard while decoding all 23 RAW files in one test case. The quality gate now samples one actual file per each of the 8 supported formats; rerun this bounded corpus set.
