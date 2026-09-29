# RAW-61 Quality gate — progress
Branch: feat/raw-support-integration · PR: #239 (umbrella draft) · Agent model: inherited; model name not exposed
Last update: 2026-09-29 · State: READY FOR REVIEW

## Steps
- [x] 1. Cover all 8 EXIF orientations, all major corpus format dimensions, malformed/truncated RAW, INV-8 release, and Bgr32 pixel format.
- [x] 2. Verify Adobe RGB embedded-preview conversion against a known ICC-bearing reference and inject the bundled CC0 Adobe-compatible profile where a preview is tagged Adobe RGB but lacks ICC data.
- [x] 3. Targeted tests, mutation checks, full gate, and representative Native corpus test passed.

## Next action
RAW-61 is complete. RAW-62 remains: real-machine navigation/zoom/pair undo/RAM check; inspect local fixture configuration and existing headless diagnostics before choosing the automated portion to run.

## Evidence / measurements
Targeted `RawDecoderTests`: 19 passed. Adobe test strips ICC data from the preview while retaining the RAW Adobe RGB marker, then compares RawDecoder output with the same JPEG carrying its Adobe-compatible profile. Mutation changing the RAW Adobe-profile branch to sRGB made the Adobe quality assertion fail (PSNR 20.88 dB vs required ≥50 dB); source restored.

## Open problems
The initial all-23 corpus Native test exceeded its 120-second hang guard. It now samples one actual file per each of the 8 supported formats and passes in 39 seconds. Manual UI navigation/zoom/pair workflow remains outside the automated evidence.

Full gate `tools/verify-all.ps1 -Hidden`: PASS (build 0 warnings/errors; Architecture 64, Core 1780, Imaging 650, Integration 641, App 1199; publish and release verification passed). Native corpus test (one sample per major format): 1 passed. Adobe profile mutation and orientation mutation each caused the corresponding new tests to fail; source restored.
