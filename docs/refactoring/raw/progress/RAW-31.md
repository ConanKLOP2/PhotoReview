# RAW-31 LibRaw backend — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: opus
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Select official x64 package, pin SHA-256, include license texts — `2f5f7f9` — build: Release solution, 0 warnings/errors
- [x] 2. Add P/Invoke and SafeHandle based decoder with cancellation — `2af8a4f` — tests: 1 Native smoke test passed (CR2 via open_buffer, CR3 via open_wfile); full Release build 0 warnings/errors
- [x] 3. Probe availability and register backend in application composition — `8f4834d` — tests: 1 Native LibRaw probe + 1 app composition test passed
- [x] 4. Normalize output to Bgr32 at 96 DPI and set SourceKind = 2 — routing, source-kind cache key, cancellation, delayed zoom indicator, and RAW zoom integration coverage complete; ZoomDetailTests 25/25, DecodeOriginalTests 6/6; full Release build 0 warnings/errors. Decoder Bgr32/96 DPI and bounded output were verified by Native tests.
- [ ] 5. Decode corpus and record timing/memory; evaluate half_size  ← CURRENT: all 23/23 corpus files across 8 formats decode; per-file timing recorded (0.443–19.572 s; mean ≈5.09 s; full run 1:58). Added 50 ms concurrent private-bytes sampler and a 200-decode Canon 7D sRAW stability test (32 MB growth ceiling); these tests still need to run with the corrected sampler.

## Next action
Run the full corpus and 200-decode Native tests with the concurrent private-bytes sampler; then evaluate whether LibRaw exposes a supported half_size C API setter.

## Evidence / measurements
- Full decode routing: regression tests prove RawFullDecode.OnZoom selects the injected RAW decoder and SourceKind=2 dimensions key, while disabled mode uses the configured standard decoder.
- RAW zoom indicator: appears after 300 ms during a fake full decode and clears after completion; integration test proves the `.cr2` zoom takes the RAW full decoder path.
- Bounded RAW zoom integration: ZoomDetailTests 25/25 passed. Full Release solution build: 0 warnings, 0 errors.
- Corpus: Native test decoded all 23/23 files across 8 formats. Full-resolution decode timings: 0.443–19.572 s, mean ≈5.09 s; total 1:58. Highest times were Fujifilm X100V RAF 19.572 s, X-T2 RAF 16.992 s, and X-E2S RAF 10.856 s. Per-file dimensions/timings were emitted by the test runner. Post-decode private-bytes sample peaked at 35.9 MB but does not include allocation peaks.
- Bounded test run: DecodeOriginalTests 6/6 passed. Full Release solution build: 0 warnings, 0 errors.
- Official package: LibRaw 0.22.2 Windows x64; `libraw.dll` expected SHA-256 `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD` (1,153,024 bytes).
- `tools/fetch-libraw.ps1` PASS; application output contains the DLL and both license texts; copied DLL hash matches the pin.
- CR2 and CR3 Native smoke test passed through file-path opening; the test now also exercises pinned-memory opening for CR2.
- `dotnet build PhotoReview.slnx -c Release`: 0 warnings, 0 errors. LibRaw Native tests: 3 passed (runtime probe, CR2 buffer + CR3 file decode, Bgr32/96 DPI, bounded-box scaling, orientation guard).
- Mutation check: removing the LibRaw provider caused the app composition test to fail (`Expected: True, Actual: False`); production registration restored.
- Mutation check: changing the output format to `Bgra32` caused the LibRaw Native test to fail (`Expected: Bgr32, Actual: Bgra32`); production output restored to `Bgr32`.

## Open problems
- Step 5 concurrent peak-memory sampling, 200-decode stability, and half_size evaluation remain. Lead license review remains.
