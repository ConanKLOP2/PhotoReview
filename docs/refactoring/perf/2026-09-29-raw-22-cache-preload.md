# RAW-22 cache, preload and RAM estimate

## Method

Ran `RawPreloadBudgetSlowTests` with 200 temporary synthetic DNG files, each 512 KiB, containing a 640x480 JPEG preview. The test preloaded the folder through `PreviewImageService` and `PreloadScheduler`, counted source bytes read per path, and compared the logged folder estimate with decoded cache memory.

## Results

- Decoded cache: 200 × 640 × 480 × 4 = 245,760,000 bytes.
- RAM estimate: asserted within ±20% of measured decoded cache size.
- Source reads: at most two 64 KiB header blocks plus the embedded JPEG preview per source; no whole-file RAW read.
- Source byte cache: retained exactly one preview range per file (200 ranges).
- Test result: 1 passed.

This is a deterministic synthetic memory and I/O check. It does not measure switching/render latency or represent a real camera corpus. No real-machine performance claim is made.
