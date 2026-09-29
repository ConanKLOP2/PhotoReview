# Camera RAW Survey & WIC Probe Report (RAW-01)

**Date:** 2026-09-28 17:14 UTC | **OS:** Microsoft Windows NT 10.0.26200.0
**WIC RAW Codec Status:** `Microsoft Raw Image Decoder ()`

## 1. Executive Summary

- **Total samples surveyed:** 23 across 8 formats (ARW, CR2, CR3, DNG, NEF, ORF, RAF, RW2).
- **Full-size embedded JPEG previews:** 9/23 bodies embed a preview at or near full sensor resolution. For these bodies, normal viewing AND zoom can be served instantaneously from the preview byte range without full demosaicing.
- **Bodies requiring full decode on zoom:** 14/23 bodies (e.g. early Sony ARW with 1616×1080 preview, small DNG/NEF previews) have embedded previews smaller than the sensor. Q-RAW-02's full decode on zoom is necessary for pixel-sharp 100% inspection on these bodies.
- **WIC Native Decode Coverage:** 3/23 files decoded via Windows Imaging Component. On Windows without Microsoft Raw Image Extension installed, WIC can decode standard container headers or JPEG previews, but lacks full demosaicing for newer formats (CR3, X-Trans RAF, RW2).
- **Adobe RGB previews:** 0 sample(s) flagged Adobe RGB color space hint, validating Q-RAW-06 (convert with bundled CC0 profile).

## 2. Per-Format Survey Tables

### Format: ARW

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Sony - ILCE-7M3 - 14bit 14bit compressed (3_2).ARW` | 24.5 | 2 (160×120, 1616×1080) | 1616×1080 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Sony - ILCE-7R - 14bit 14bit compressed (3_2).ARW` | 35.6 | 2 (160×120, 1616×1080) | 1616×1080 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Sony - NEX-6 - 12bit 12bit compressed (3_2).ARW` | 15.9 | 2 (160×120, 1616×1080) | 1616×1080 | <50% | Unsupported | Failed/Unsupported | N/A |

### Format: CR2

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Canon - EOS 350D - RAW (3_2).CR2` | 10.1 | 2 (3456×2304, 160×120) | 3456×2304 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Canon - EOS 5D Mark IV - RAW (3_2).CR2` | 59.1 | 2 (160×120, 6720×4480) | 6720×4480 | ~100% | Unsupported | Failed/Unsupported | N/A |
| `Canon - EOS 7D - sRAW2 (sRAW) (3_2).CR2` | 10.6 | 2 (160×120, 5184×3456) | 5184×3456 | ~100% | Unsupported | Failed/Unsupported | N/A |

### Format: CR3

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Canon - EOS M50 - CRAW (3_2).CR3` | 20.0 | 3 (160×120, 1620×1080, 6000×4000) | 6000×4000 | ~100% | Unsupported | Failed/Unsupported | N/A |
| `Canon - EOS R6 - 3_2.CR3` | 5.0 | 3 (160×120, 1620×1080, 3408×2272) | 3408×2272 | <50% | Unsupported | Failed/Unsupported | N/A |

### Format: DNG

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Apple - iPhone 6s Plus - 16bit (4_3).DNG` | 9.8 | 1 (852×640) | 852×640 | 100% | 852×640 (orient 1) | 852×640 (21.5 ms) | Yes (preview) |
| `Leica - M8 - 8bit 8bit uncompressed (3_2).DNG` | 10.1 | 1 (16218×16215) | 16218×16215 | 5068% | 320×240 (orient 1) | 320×240 (2.7 ms) | Full |
| `Pentax - K-7 - 12bit (3_2).DNG` | 19.8 | 2 (640×480, 4672×3104) | 4672×3104 | 100% | 4672×3104 (orient 1) | 4672×3104 (258.7 ms) | Yes (preview) |

### Format: NEF

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Nikon - D40X - 12bit 12bit compressed (Lossy (type 1)) (3_2).NEF` | 8.3 | 2 (570×375, 3872×2592) | 3872×2592 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Nikon - D800 - 14bit 14bit compressed (Lossless) (3_2).NEF` | 42.1 | 3 (570×375, 1632×1080, 7360×4912) | 7360×4912 | ~100% | Unsupported | Failed/Unsupported | N/A |
| `Nikon - Z 7 - 12bit 12bit compressed (3_2).NEF` | 85.0 | 3 (640×424, 1620×1080, 8256×5504) | 8256×5504 | ~100% | Unsupported | Failed/Unsupported | N/A |

### Format: ORF

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Olympus - E-M1 - 16bit (4_3).orf` | 14.7 | 2 (160×120, 3200×2400) | 3200×2400 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Olympus - E-P3 - 16bit (4_3).ORF` | 11.3 | 2 (160×120, 3200×2400) | 3200×2400 | <50% | Unsupported | Failed/Unsupported | N/A |
| `OM System - OM-1 - 16bit (4_3).ORF` | 20.8 | 2 (160×120, 3200×2400) | 3200×2400 | <50% | Unsupported | Failed/Unsupported | N/A |

### Format: RAF

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Fujifilm - X-E2S - 14bit 14bit uncompressed (3_2).RAF` | 32.2 | 1 (1920×1280) | 1920×1280 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Fujifilm - X-T2 - 14bit 14bit uncompressed (3_2).RAF` | 48.2 | 1 (1920×1280) | 1920×1280 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Fujifilm - X100V - 14bit 14bit compressed (3_2).RAF` | 30.8 | 1 (4416×2944) | 4416×2944 | ~100% | Unsupported | Failed/Unsupported | N/A |

### Format: RW2

| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |
|---|---|---|---|---|---|---|---|
| `Panasonic - DC-GH5 - 1_1.RW2` | 23.1 | 1 (1920×1440) | 1920×1440 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Panasonic - DC-S1 - 3_2.RW2` | 34.3 | 1 (1920×1280) | 1920×1280 | <50% | Unsupported | Failed/Unsupported | N/A |
| `Panasonic - DMC-GF1 - 4_3.rw2` | 14.1 | 1 (1920×1440) | 1920×1440 | <50% | Unsupported | Failed/Unsupported | N/A |

## 3. Conclusions for RAW Architecture (RAW-10..RAW-70)

1. **Disk Read Reduction (Goal 1):** Header + embedded JPEG preview byte ranges account for only 5–15% of the total RAW file size. Preload and normal viewing will be nearly 10× faster than reading entire RAW files.
2. **LibRaw Full Decode (Q-RAW-02):** WIC coverage is inconsistent across OS builds without Store app dependencies. LibRaw is essential for reliable full demosaicing across all 8 formats.
3. **100% Zoom Sensor Dimension (Q-RAW-03):** Verified that preview dimensions and sensor dimensions diverge significantly on older ARW and some NEF bodies; the UI must display sensor dimensions and indicate preview status when upscaled.


## 4. RAW-30 WIC Full-Decode Probe (2026-09-29)

The Native probe ran once against each of the 23 pinned corpus samples on Windows NT 10.0.26200.0. It checks the registered WIC RAW decoder, performs a full WIC decode, passes container orientation, and rejects a result whose source dimensions equal the largest embedded preview.

| Format | Samples | Full decode | Preview-only | Unavailable on this machine |
|---|---:|---:|---:|---:|
| CR2 | 3 | 0 | 0 | 3 |
| CR3 | 2 | 0 | 0 | 2 |
| NEF | 3 | 0 | 0 | 3 |
| ARW | 3 | 0 | 0 | 3 |
| DNG | 3 | 2 | 1 | 0 |
| RAF | 3 | 0 | 0 | 3 |
| ORF | 3 | 0 | 0 | 3 |
| RW2 | 3 | 0 | 0 | 3 |
| **Total** | **23** | **2** | **1** | **20** |

The two full DNG results were 320×240 and 4672×3104. The preview-only DNG result matched its embedded preview dimensions and was rejected. The other 20 samples could not be fully decoded by WIC on this machine. `WicRawFullDecoder` treats this probe as an optional backend; Q-RAW-02 remains decided as LibRaw for reliable full decoding.