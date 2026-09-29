# RAW-31 LibRaw backend — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: opus
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Select official x64 package, pin SHA-256, include license texts — pending commit SHA — build: Release solution, 0 warnings/errors
- [ ] 2. Add P/Invoke and SafeHandle based decoder with cancellation
- [ ] 3. Probe availability and register backend in application composition
- [ ] 4. Normalize output to Bgr32 at 96 DPI and set SourceKind = 2
- [ ] 5. Decode corpus and record timing/memory; evaluate half_size

## Next action
Commit and push the verified packaging step. Then inspect LibRaw's official C API headers and implement the native interop in step 2.

## Evidence / measurements
- Official package: LibRaw 0.22.2 Windows x64; `libraw.dll` expected SHA-256 `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD` (1,153,024 bytes).
- `tools/fetch-libraw.ps1` PASS; application output contains the DLL and both license texts; copied DLL hash matches the pin.

## Open problems
- Steps 2–5 and lead license review remain.
