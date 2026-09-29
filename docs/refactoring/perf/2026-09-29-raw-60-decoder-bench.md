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

The original 23-file corpus has no independent same-camera JPEGs, so the first benchmark only compared each RAW with its embedded preview. A follow-up used one paired camera sample from the [Warwick Image Forensics Dataset (WIFD)](https://github.com/CSCRC-SCREED/WIFD): Canon EOS 6D, Scene 1, AEB, ISO 100, sequence 15. The dataset README says images are organized into matching `cr2/` and `jpg/` folders and defines the shared basename fields; it also declares both data and code under MIT. The repository's [MIT license](https://github.com/CSCRC-SCREED/WIFD/blob/main/LICENSE) was reviewed. The [authors' paper](https://wrap.warwick.ac.uk/id/eprint/136576/7/WRAP-Warwick-image-forensics-dataset-device-fingerprinting-multimedia-forensics-Li-2020.pdf) should be cited when using the dataset. Only these two files were downloaded to a temporary directory; neither is added to this repository.

| Paired file | Dimensions | Size | SHA-256 |
|---|---:|---:|---|
| `canon_eos_6d_scene1_ISO100_1_15.cr2` | LibRaw output 5496×3670 | 25,710,698 bytes | `A9AEEE9852BFD7297FDAE1040AA857C69F4DA4A8A1B632E632F7BC91EA1878AD` |
| `canon_eos_6d_scene1_ISO100_1_15.jpg` | 5472×3648 | 3,907,807 bytes | `AEC710CFD6272C4A8FE1B412DB55F631C702981B0F795E088530524D190BE71D` |

Source paths: `dataset/canon_eos_6d/scene_1/AEB/ISO100/cr2/` and the matching `jpg/` directory. The filenames share the camera, scene, ISO, exposure, aperture, and sequence fields. At the selected target widths both decoders produced identical output dimensions:

| Target/output | RAW `RawPreview` P50 | Camera JPEG WPF P50 | RAW `LibRawFullDecode` P50 |
|---:|---:|---:|---:|
| 1920×1280 | 70.50 ms | 120.55 ms | — |
| 2560×1706 | 72.68 ms | 137.79 ms | — |
| 3840×2560 | 136.35 ms | 258.10 ms | — |
| Full sensor | — | 301.26 ms at 5472×3648 | 3457.24 ms at 5496×3670 |

Each value is the median of three measured iterations after warm OS file-cache activity; the WPF camera-JPEG run includes 3 iterations at widths 0/1920/2560/3840, while the RAW run used the `--decoder-bench --raw` harness at 3 iterations. This closes the missing paired-camera-JPEG smoke comparison for one CR2 capture, not a multi-camera or multi-scene conclusion. Output dimensions and elapsed time were compared; no visual or pixel-quality equivalence is claimed, and the JPEG/RAW full-resolution bounds differ slightly. The first benchmark remains a one-iteration corpus smoke run, with no per-file confidence intervals. WPF bitmap creation excludes the application's Dispatcher, presentation, and monitor refresh. All timings are specific to this workstation and OS codec availability and must not be compared with pre-AR02c legacy benchmarks.
