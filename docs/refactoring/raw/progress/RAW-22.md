# RAW-22 Cache, preload and RAM budget for RAW — progress
Branch: feat/raw-support-integration · PR: #239 · Agent model: Codex
Last update: 2026-09-29 · State: READY FOR REVIEW

## Steps
- [x] 1. Estimate decoded memory from per-file dimensions; keep parsed RAW metadata in a bounded LRU keyed by path, length and modification time.
- [x] 2. Cache selected preview byte ranges; estimate folder RAM from decoded dimensions and bypass whole-file source prefetch for RAW.
- [x] 3. Reuse parsed header data and exclude full RAW decode timings from slow-link decode EWMA.
- [x] 4. Add a Category=Slow synthetic 200-file preload test for estimate accuracy and header-plus-preview-only reads.

## Evidence / measurements
- Release solution build: 0 warnings, 0 errors. Standard solution filter `Category!=Manual&Category!=Native&Category!=Slow`: 4,278 passed, 0 failed.
- `RawPreloadBudgetSlowTests`: 1 passed. For 200 synthetic 640x480 files, decoded/cache size was 245,760,000 bytes and the logged RAM estimate was within ±20% (the test checks the measured range).
- Each 512 KiB source read at most two 64 KiB header blocks plus its embedded JPEG preview; the test also verifies the range cache retains exactly 200 preview ranges.
- Nine mutation checks were killed, covering range offsets, metadata caching, RAW RAM fallback, RAW prefetch/preread bypass, decode EWMA exclusion, source-kind cache keys and source-read metrics.
- Measurements are deterministic synthetic I/O and memory checks, not real-folder latency benchmarks.

## Next action
Review and merge the RAW integration PR after confirming overall RAW app wiring and remaining RAW wave tasks; then continue the next unimplemented task in `docs/refactoring/raw/TASKS.md`.

## Open problems
- PR #239 remains draft. The broader RAW integration still needs lead review; app-level decoder registration was not part of this task's verified scope.
- No real-machine RAW folder latency measurement was performed.
