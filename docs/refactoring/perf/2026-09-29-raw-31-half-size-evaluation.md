# RAW-31 LibRaw `half_size` evaluation

## Method

Used the official pinned LibRaw 0.22.2 Windows x64 archive already present at `native/libraw/LibRaw-0.22.2-Win64.zip` and its bundled sample executables:

- `bin/dcraw_half.exe`, built from the official `samples/dcraw_half.c`, which sets `params.half_size = 1` before unpack/process.
- `bin/dcraw_emu.exe`, the official full-size dcraw emulator sample.

Both executables decoded the same WIFD Canon EOS 6D CR2 sample used in the [RAW-60 paired JPEG follow-up](2026-09-29-raw-60-decoder-bench.md). Each mode ran in three fresh processes after the first run warmed the OS file cache. Elapsed time was measured per process; private bytes were sampled every 25 ms and report the highest observed process value. The executable emits PPM, so output dimensions and PPM file sizes were read from its header and output file.

## Results

| Mode | Median time (3 runs) | Peak sampled private bytes | PPM dimensions | PPM bytes |
|---|---:|---:|---:|---:|
| LibRaw half-size | 879.40 ms | 84,623,360 (80.7 MiB) | 2748×1835 | 15,127,757 |
| LibRaw full-size | 3425.68 ms | 212,295,680 (202.5 MiB) | 5496×3670 | 60,510,977 |

On this one CR2, half-size completed about 3.9× faster and used about 60% less peak private memory, while producing one quarter as many pixels. The output dimensions are exactly half the full-size dimensions on each axis.

## Zoom-path conclusion

Do not enable `half_size` in PhotoReview's sensor-resolution zoom path. ADR 0008 defines 100% zoom as one displayed pixel per source pixel; a half-size decode cannot supply those pixels and would blur or upscale when the user requests native detail. Normal review already uses the embedded preview, so this result does not justify adding a lower-resolution decode path. The experiment evaluates LibRaw's option, but it is a single CR2 run on its bundled sample programs, not a per-format application benchmark. No production behavior changed.
