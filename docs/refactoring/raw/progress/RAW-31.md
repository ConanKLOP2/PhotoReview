# RAW-31 LibRaw backend — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: opus
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Select official x64 package, pin SHA-256, include license texts — `2f5f7f9` — build: Release solution, 0 warnings/errors
- [x] 2. Add P/Invoke and SafeHandle based decoder with cancellation — `2af8a4f` — tests: 1 Native smoke test passed (CR2 via open_buffer, CR3 via open_wfile); full Release build 0 warnings/errors
- [x] 3. Probe availability and register backend in application composition — `8f4834d` — tests: 1 Native LibRaw probe + 1 app composition test passed
- [ ] 4. Normalize output to Bgr32 96 DPI and set SourceKind = 2  ← CURRENT: Bgr32/96 DPI output and direct bilinear downscale to requested box added; native quality/orientation tests pending; cache SourceKind 2 is owned by RAW-32 zoom integration
- [ ] 4. Normalize output to Bgr32 at 96 DPI and set SourceKind = 2
- [ ] 5. Decode corpus and record timing/memory; evaluate half_size

## Next action
Confirm the zoom path's SourceKind contract, then make LibRaw honor its decode box and verify orientation is applied exactly once while preserving 96-DPI Bgr32 output.

## Evidence / measurements
- Official package: LibRaw 0.22.2 Windows x64; `libraw.dll` expected SHA-256 `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD` (1,153,024 bytes).
- `tools/fetch-libraw.ps1` PASS; application output contains the DLL and both license texts; copied DLL hash matches the pin.
- CR2 and CR3 Native smoke test passed through file-path opening; the test now also exercises pinned-memory opening for CR2.
- `dotnet build PhotoReview.slnx -c Release`: 0 warnings, 0 errors after adding app registration. Imaging Native probe+decode tests: 2 passed; app provider registration test: 1 passed.
- Mutation check: removing the LibRaw provider caused the app composition test to fail (`Expected: True, Actual: False`); production registration restored.

## Open problems
- Steps 2–5 and lead license review remain.
