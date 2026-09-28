# RAW container cheat-sheet (for RAW-11/12/13/15)

Working knowledge, not a spec. **Every statement marked (verify) must be confirmed against the RAW-01 corpus
and ExifTool's tag tables before the reader relies on it**; record the evidence (file, offset, value) in the
task's progress file. Readers must work from structure (tags, boxes, magic), never from fixed file offsets
except where the format itself defines a fixed header (RAF, CR3 `ftyp`).

Common rules for every reader:

- Input is hostile. All offsets/lengths are validated against `IRawHeaderSource.Length` with checked
  arithmetic; an IFD/box visited twice is a cycle → stop; honour `RawContainerLimits`.
- Read at most what is needed: IFD entries and small tag values from the header source; the preview itself is
  **not** read by the container reader, only its `(Offset, Length)`.
- Preview `Width/Height` may be unknown from tags; RAW-21 fills them by reading the JPEG SOF (≤ 64 KB from the
  preview start) only for the preview it is about to choose between, never for all.
- Orientation comes from the container's main IFD (tag 0x0112) or its CR3 equivalent; values 1–8, anything
  else → 1.
- `SensorWidth/Height` = the visible image size a RAW converter would output (Q-RAW-03), not the full
  sensor with masked borders.

## TIFF family (one shared walker — extract it from `ExifParser.TryParseTiff`, don't copy it)

| Format | Magic | Where the big JPEG preview is | Small previews / thumbs | Sensor size | Notes |
|---|---|---|---|---|---|
| **CR2** (Canon) | `II*\0` + `CR` at offset 8 | IFD0 `StripOffsets`(0x0111)/`StripByteCounts`(0x0117), JPEG, usually full size (verify) | IFD1 JPEG thumbnail 160×120 (`JPEGInterchangeFormat` 0x0201/0x0202); IFD2 uncompressed RGB | IFD3 raw strip, or EXIF `PixelXDimension/YDimension` (verify per body) | Orientation in IFD0 |
| **NEF** (Nikon) | `MM\0*` or `II*\0`, Make = NIKON | IFD0 `SubIFDs`(0x014A) → the SubIFD with `JPEGInterchangeFormat`/`Length` (0x0201/0x0202) = JpgFromRaw, full size on most modern bodies (verify) | IFD0 tiny thumb; MakerNote PreviewIFD ~640×424 | raw SubIFD `ImageWidth/Length` minus crop (verify) | MakerNote offsets are relative to the MakerNote's own TIFF header |
| **NRW** | as NEF | as NEF (verify) | | | reserved, out of Q-RAW-05 A |
| **ARW** (Sony) | `II*\0`, Make = SONY | IFD0 0x0201/0x0202 = PreviewImage, often 1616×1080 on older bodies, larger on newer (verify per body) | IFD1 thumbnail 160×120 | SR2/raw SubIFD size (verify) | Some ARW v1 have no big preview → full decode or small preview |
| **DNG** | `II*\0`/`MM\0*`, tag `DNGVersion` 0xC612 | IFD0 or any `SubIFDs` entry with `NewSubFileType`(0x00FE) = 1 and `Compression` = 7 (JPEG) or 6; pick by size | same rule, smaller | the raw IFD (`NewSubFileType` = 0) `DefaultCropSize` 0xC620 | Phone DNGs may have no JPEG preview (only raw) → full decode only |
| **ORF** (OM/Olympus) | `IIRO` / `IIRS` / `MMOR` (TIFF with a different magic) | MakerNote → CameraSettings IFD (0x2020) `PreviewImageStart`(0x0101)/`Length`(0x0102) (verify) | IFD1 thumbnail | raw IFD0 size minus crop (verify) | MakerNote begins with `OLYMPUS\0II\x03\0` and has its own byte order |
| **RW2** (Panasonic) | `IIU\0` (0x55 instead of 0x2A) | tag 0x002E `JpgFromRaw` in IFD0 = a complete JPEG, usually 1920×1440 (verify); **EXIF lives inside that JPEG's APP1**, not in IFD0 | — | IFD0 tags 0x0002/0x0003 (sensor) or 0x0006-0x0009 crop borders (verify) | Orientation also inside the embedded JPEG's EXIF |
| **PEF** (Pentax) | `MM\0*`, Make = PENTAX | MakerNote PreviewImageStart/Length (verify) | | | reserved, out of Q-RAW-05 A |

## CR3 (Canon, ISO base media file format — RAW-12)

- `ftyp` box with major brand `crx `. Walk boxes: 32-bit size, 4CC type, `size == 1` → 64-bit largesize,
  `size == 0` → to end of file. `uuid` boxes carry a 16-byte UUID after the type.
- `moov` → `uuid 85c0b687-820f-11e0-8111-f4ce462b6a48` (Canon) contains:
  `CMT1` (TIFF: IFD0 incl. Orientation, Make/Model), `CMT2` (TIFF: EXIF IFD), `CMT3` (MakerNote),
  `CMT4` (GPS), `THMB` (small JPEG, ~160×120, with a small header before the JPEG — verify offsets).
- top-level `uuid eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16` → `PRVW` box: a JPEG ~1620×1080 after a short
  header (verify header size, it carries width/height).
- `moov` → `trak` #1 → `mdia/minf/stbl` → `stsz` (sample size) + `co64`/`stco` (chunk offset) = the
  **full-size JPEG** in `mdat` (verify: track 1 is the JPEG track on all CR3 bodies in the corpus).
  Tracks 2–3 are CRX raw data (not ours), track 4 metadata.
- Each `CMTn` payload is a standalone TIFF structure → hand its span to the shared TIFF walker / ExifParser
  (`ExifBlock.IsTiffHeader = true`).
- Sensor size: from `CMT1`/`CMT2` EXIF or the CRX track's `CRAW` sample entry width/height (verify which
  equals the visible size).

## RAF (Fujifilm — RAW-13)

- Fixed header: magic `FUJIFILMCCD-RAW ` (16 bytes), then format/camera id strings; big-endian fields at
  offset 84 = embedded JPEG offset, 88 = JPEG length, 92 = CFA header offset, 96 = CFA header length,
  100 = CFA offset, 104 = CFA length (verify against the corpus).
- The embedded JPEG is a complete JPEG **with its own EXIF APP1** (orientation, camera, exposure) → EXIF via
  the existing JPEG path on the preview bytes (`ExifBlock` points inside the JPEG, `IsTiffHeader = false`).
- Preview size varies by body (from ~1920×1280 to full size — verify); sensor size from the CFA header
  records (tag 0x0100 raw image full size / 0x0111 crop — verify).

## Minimum test matrix per reader (synthetic, in-memory, CI)

1. happy path: one big preview + one thumb; correct `(Offset, Length, Kind)`, orientation 1..8, sensor size;
2. both byte orders (TIFF family);
3. preview tag missing → `Previews` empty, no throw;
4. truncated file at every structural boundary → `InvalidDataException` (never IndexOutOfRange/Overflow);
5. IFD/box cycle; count = 0xFFFF; offset/length pointing past EOF; negative-looking 32-bit values; 64-bit
   `largesize` overflow (CR3);
6. header-read accounting: the reader never asks `IRawHeaderSource` for more than `MaxHeaderBytes` in total
   and never reads the preview bytes themselves.
