# RAW-31 LibRaw backend — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: opus
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Select official x64 package, pin SHA-256, include license texts — `2f5f7f9` — build: Release solution, 0 warnings/errors
- [x] 2. Add P/Invoke and SafeHandle based decoder with cancellation — `2af8a4f` — tests: 1 Native smoke test passed (CR2 via open_buffer, CR3 via open_wfile); full Release build 0 warnings/errors
- [x] 3. Probe availability and register backend in application composition — `8f4834d` — tests: 1 Native LibRaw probe + 1 app composition test passed
- [ ] 4. Normalize output to Bgr32 at 96 DPI and set SourceKind = 2  ← CURRENT: LibRaw is selected for RAW full decode only when RawSupportEnabled + RawFullDecode.OnZoom; the original cache/dimension key uses SourceKind=2, with cancellation passed into the native decoder.
- [ ] 5. Decode corpus and record timing/memory; evaluate half_size

## Next action
Finish the RAW-32 zoom indicator and integration test; then run every corpus sample, measure timing/peak private bytes, and do the 200-decode leak check.

## Evidence / measurements
- Full decode routing: regression tests prove RawFullDecode.OnZoom selects the injected RAW decoder and SourceKind=2 dimensions key, while disabled mode uses the configured standard decoder.
- Bounded test run: DecodeOriginalTests 6/6 passed. Full Release solution build: 0 warnings, 0 errors.
- Official package: LibRaw 0.22.2 Windows x64; `libraw.dll` expected SHA-256 `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD` (1,153,024 bytes).
- `tools/fetch-libraw.ps1` PASS; application output contains the DLL and both license texts; copied DLL hash matches the pin.
- CR2 and CR3 Native smoke test passed through file-path opening; the test now also exercises pinned-memory opening for CR2.
- `dotnet build PhotoReview.slnx -c Release`: 0 warnings, 0 errors. LibRaw Native tests: 3 passed (runtime probe, CR2 buffer + CR3 file decode, Bgr32/96 DPI, bounded-box scaling, orientation guard).
- Mutation check: removing the LibRaw provider caused the app composition test to fail (`Expected: True, Actual: False`); production registration restored.
- Mutation check: changing the output format to `Bgra32` caused the LibRaw Native test to fail (`Expected: Bgr32, Actual: Bgra32`); production output restored to `Bgr32`.

## Open problems
- Step 4 awaits RAW-32 zoom indicator/integration coverage. Step 5 corpus-wide decode, timing/peak memory, 200-decode stability, and half_size evaluation remain. Lead license review remains.
