# RAW-60 decoder benchmark

## Method

Ran `--decoder-bench --raw tests/Fixtures/raw-corpus <outDir> 1` on the Windows workstation. The 23-file corpus contains CR2 (3), CR3 (2), DNG (3), RAF (3), NEF (3), ORF (3), RW2 (3), and ARW (3). One iteration per operation was used for an initial corpus pass; results are medians across files, not repeat-run P50s per file.

- **HeaderParse:** new `SourceRawHeaderSource` and reader registry per sample; parse container metadata and preview table without decoding.
- **RawPreview:** new `RawDecoder` per sample; container parse, embedded preview range read and WPF/WIC decode into a bitmap for 1920, 2560, or 3840-pixel target width. This is bitmap-ready time, not actual UI first-paint/render latency.
- **EmbeddedJpegDirect:** reads and decodes the exact embedded JPEG range chosen above at the same target width. It is a same-source comparison, not a standalone camera JPEG comparison.
- **LibRawFullDecode:** full sensor demosaic at native output size.

Reports were written to a temporary directory outside the repository (`raw-decoder-bench.csv` and `.json`); the CSV has per-file samples and the JSON includes the grouped summary and failures.

## Results

| Operation | Target | Successful / attempted | Median ms |
|---|---:|---:|---:|
| Header parse | — | 23 / 23 | 11.59 |
| RAW preview bitmap | 1920 | 19 / 22 | 73.32 |
| Embedded JPEG direct | 1920 | 19 / 22 | 61.59 |
| RAW preview bitmap | 2560 | 19 / 22 | 74.59 |
| Embedded JPEG direct | 2560 | 19 / 22 | 78.60 |
| RAW preview bitmap | 3840 | 19 / 22 | 65.29 |
| Embedded JPEG direct | 3840 | 19 / 22 | 64.65 |
| LibRaw full decode | native | 22 / 22 | 3945.99 |

One Leica M8 DNG has no supported embedded preview in the current RAW parser. All three ORF files parsed, but WPF/WIC failed to instantiate their embedded JPEG previews (`NotSupportedException: No imaging component suitable to complete this operation was found`). LibRaw full decode succeeded for all 22 files with a preview; it also succeeded for the Leica DNG. LibRaw emitted `Unsupported color conversion request` diagnostics during two attempts, although their decoded operations reported success; those samples should be examined if color quality is the subject of a later task.

## Limits

The folder has no independent same-camera JPEG files, so this does not satisfy a full-resolution RAW-vs-camera-JPEG comparison. The direct-JPEG baseline is the embedded preview itself. One iteration is a smoke benchmark and gives no per-file repeatability or confidence interval. WPF bitmap creation does not include the application's Dispatcher, presentation, or monitor refresh. Times are specific to this workstation, OS codec availability, and the present corpus and must not be compared with pre-AR02c legacy benchmarks.
