# RAW-31 LibRaw backend — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: opus
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Select official x64 package, pin SHA-256, include license texts — `2f5f7f9` — build: Release solution, 0 warnings/errors
- [x] 2. Add P/Invoke and SafeHandle based decoder with cancellation — `2af8a4f` — tests: 1 Native smoke test passed (CR2 via open_buffer, CR3 via open_wfile); full Release build 0 warnings/errors
- [x] 3. Probe availability and register backend in application composition — `8f4834d` — tests: 1 Native LibRaw probe + 1 app composition test passed
- [x] 4. Normalize output to Bgr32 at 96 DPI and set SourceKind = 2 — routing, source-kind cache key, cancellation, delayed zoom indicator, and RAW zoom integration coverage complete; ZoomDetailTests 25/25, DecodeOriginalTests 6/6; full Release build 0 warnings/errors. Decoder Bgr32/96 DPI and bounded output were verified by Native tests.
- [ ] 5. Decode corpus and record timing/memory; evaluate half_size  ← CURRENT: 23/23 files across 8 formats pass in 1:59; timings 0.447–20.120 s, mean ≈5.10 s. Concurrent 50 ms sampler peak private bytes 1,141,518,336 (~1.06 GiB) across the full corpus. Repeated Canon 7D sRAW test 200/200 passes in 1:33; private bytes after warmup 186,818,560, final 186,277,888 (−0.5 MiB), peak 215,646,208. Official C API exposes no half_size setter/accessor; safely evaluating it requires a separately built C++/C shim, not present in the pinned binary.

## Next action
Complete full Release build and relevant tests; record the lack of a safe half_size C setter as a protocol limitation. Lead license review remains before RAW-31 acceptance.

## Evidence / measurements
- Full decode routing: regression tests prove RawFullDecode.OnZoom selects the injected RAW decoder and SourceKind=2 dimensions key, while disabled mode uses the configured standard decoder.
- RAW zoom indicator: appears after 300 ms during a fake full decode and clears after completion; integration test proves the `.cr2` zoom takes the RAW full decoder path.
- Bounded RAW zoom integration: ZoomDetailTests 25/25 passed. Full Release solution build: 0 warnings, 0 errors.
- Corpus: 23/23 files across 8 formats passed in 1:59; full-resolution timings 0.447–20.120 s, mean ≈5.10 s; concurrent 50 ms sampler peak private bytes 1,141,518,336 (~1.06 GiB). Highest timings: Fujifilm X100V RAF 20.120 s, X-T2 RAF 16.936 s, and X-E2S RAF 10.842 s.
- 200 consecutive Canon 7D sRAW decodes passed in 1:33; after-warmup private bytes 186,818,560, final 186,277,888 (−540,672 bytes), peak 215,646,208.
- half_size review: official docs describe half-size output through `imgdata.params.half_size`, while the published pinned C API contains no half_size setter or output-params accessor. Do not write private struct offsets; a version-matched native shim would be needed for a controlled measurement.
- Bounded test run: DecodeOriginalTests 6/6 and ZoomDetailTests 25/25 passed. Full Release solution build: 0 warnings, 0 errors.
- Official package: LibRaw 0.22.2 Windows x64; `libraw.dll` expected SHA-256 `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD` (1,153,024 bytes).
- `tools/fetch-libraw.ps1` PASS; application output contains the DLL and both license texts; copied DLL hash matches the pin.
- CR2 and CR3 Native smoke test passed through file-path opening; the test now also exercises pinned-memory opening for CR2.
- `dotnet build PhotoReview.slnx -c Release`: 0 warnings, 0 errors. LibRaw Native tests: 3 passed (runtime probe, CR2 buffer + CR3 file decode, Bgr32/96 DPI, bounded-box scaling, orientation guard).
- Mutation check: removing the LibRaw provider caused the app composition test to fail (`Expected: True, Actual: False`); production registration restored.
- Mutation check: changing the output format to `Bgra32` caused the LibRaw Native test to fail (`Expected: Bgr32, Actual: Bgra32`); production output restored to `Bgr32`.

## Open problems
- Lead license review remains. A half_size runtime comparison requires adding a native shim or switching to LibRaw C++ API and is outside the current pinned C API integration.
