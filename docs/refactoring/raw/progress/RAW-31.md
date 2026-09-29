# RAW-31 LibRaw backend — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: opus
Last update: 2026-09-29 · State: READY FOR REVIEW

## Steps
- [x] 1. Select official x64 package, pin SHA-256, include license texts — `2f5f7f9` — build: Release solution, 0 warnings/errors
- [x] 2. Add P/Invoke and SafeHandle based decoder with cancellation — `2af8a4f` — tests: 1 Native smoke test passed (CR2 via open_buffer, CR3 via open_wfile); full Release build 0 warnings/errors
- [x] 3. Probe availability and register backend in application composition — `8f4834d` — tests: 1 Native LibRaw probe + 1 app composition test passed
- [x] 4. Normalize output to Bgr32 at 96 DPI and set SourceKind = 2 — routing, source-kind cache key, cancellation, delayed zoom indicator, and RAW zoom integration coverage complete; ZoomDetailTests 25/25, DecodeOriginalTests 6/6; full Release build 0 warnings/errors. Decoder Bgr32/96 DPI and bounded output were verified by Native tests.
- [x] 5. Decode corpus and record timing/memory; evaluate half_size — 23/23 samples across 8 formats pass in 1:59; concurrent private-bytes peak 1,141,518,336 (~1.06 GiB). 200 Canon 7D sRAW decodes pass in 1:33 with −540,672 bytes growth after warmup. half_size evaluated: documented parameter is not exposed through the pinned official C API, so no safe runtime toggle exists without a separate shim/rebuild.

## Next action
Final gate: publish Release and run `tools/verify-release.ps1` on that exact directory; then review PR #239. All numbered RAW-31 steps and lead license/source-package review are complete.

## Evidence / measurements
- Full decode routing: regression tests prove RawFullDecode.OnZoom selects the injected RAW decoder and SourceKind=2 dimensions key, while disabled mode uses the configured standard decoder.
- RAW zoom indicator: appears after 300 ms during a fake full decode and clears after completion; integration test proves the `.cr2` zoom takes the RAW full decoder path.
- Bounded RAW zoom integration: ZoomDetailTests 25/25 passed. Full Release solution build: 0 warnings, 0 errors.
- Corpus: 23/23 files across 8 formats passed in 1:59; full-resolution timings 0.447–20.120 s, mean ≈5.10 s; concurrent 50 ms sampler peak private bytes 1,141,518,336 (~1.06 GiB). Highest timings: Fujifilm X100V RAF 20.120 s, X-T2 RAF 16.936 s, and X-E2S RAF 10.842 s.
- 200 consecutive Canon 7D sRAW decodes passed in 1:33; after-warmup private bytes 186,818,560, final 186,277,888 (−540,672 bytes), peak 215,646,208.
- half_size review: official docs describe half-size output through `imgdata.params.half_size`, while the published pinned C API contains no half_size setter or output-params accessor. Do not write private struct offsets; a version-matched native shim would be needed for a controlled measurement.
- License review: inspected the exact official 0.22.2 package and both license texts. The Windows x64 archive contains complete source/build files; release output now ships that exact pinned archive (SHA-256 `AC64FA12BB00A7581332D4C6AB918C0533FB3F119D6B668D47A6875410DCA948`), both licenses, and `LibRaw-NOTICE.txt`. The library remains dynamically loaded from a replaceable adjacent DLL.
- `tools/fetch-libraw.ps1` validates both the DLL hash and source-archive hash and passes. Release project/build verifier now require the source archive and notice. Full Release solution build after these changes: 0 warnings, 0 errors.
- Bounded test run: DecodeOriginalTests 6/6 and ZoomDetailTests 25/25 passed. Full Release solution build: 0 warnings, 0 errors.
- Official package: LibRaw 0.22.2 Windows x64; `libraw.dll` expected SHA-256 `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD` (1,153,024 bytes).
- `tools/fetch-libraw.ps1` PASS; application output contains the DLL and both license texts; copied DLL hash matches the pin.
- CR2 and CR3 Native smoke test passed through file-path opening; the test now also exercises pinned-memory opening for CR2.
- `dotnet build PhotoReview.slnx -c Release`: 0 warnings, 0 errors. LibRaw Native tests: 3 passed (runtime probe, CR2 buffer + CR3 file decode, Bgr32/96 DPI, bounded-box scaling, orientation guard).
- Mutation check: removing the LibRaw provider caused the app composition test to fail (`Expected: True, Actual: False`); production registration restored.
- Mutation check: changing the output format to `Bgra32` caused the LibRaw Native test to fail (`Expected: Bgr32, Actual: Bgra32`); production output restored to `Bgr32`.

## Open problems
- No RAW-31 implementation blockers. Runtime half_size comparison is unavailable through this pinned C API; a future implementation would require a version-matched native shim or a C++ API migration.
