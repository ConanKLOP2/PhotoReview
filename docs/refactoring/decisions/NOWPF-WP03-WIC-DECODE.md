---
id: NOWPF-WP03
order: 412
summary: |-
  WP-03 (2026-10-10): WicDirectDecoder, EmbeddedThumbnailReader, WicExifReader ghi/đọc qua WIC ra PixelBuffer + IPlatformImageCodec, parity từng byte, mutation 38/40, bench 24 MP trong ngưỡng 5 %; lệch thẻ: TryRead một tham số, thumbnail Pbgra32, ActualBackend=Wpf, WpfExifReader giữ; đề xuất v1.1 khoá ExifOrientation.IsTransposed/Normalize.
---

# NOWPF-WP03-WIC-DECODE - WIC decode, EXIF, thumbnail ra PixelBuffer (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md), thẻ WP-03 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Phụ thuộc: [NOWPF-WP02-PIXELBUFFER](NOWPF-WP02-PIXELBUFFER.md). Hợp đồng v1 không đổi (`ContractSurfaceTests` xanh, file duyệt không sửa).
PR #414. Số đo: [perf/2026-10-10-wp03-wic-decode.md](../perf/2026-10-10-wp03-wic-decode.md).

## 1. Phạm vi (đã làm)

| Phần | File | Ghi chú |
|---|---|---|
| Decode | `Imaging/Decoding/Wic/WicDirectDecoder.cs` | Nhận `IPlatformImageCodec` (không có mặc định). WIC `CopyPixels` một lần vào `PixelBuffer` đã đúng kích thước cuối (sau scale + orientation), rồi `codec.FromPixels`. Pipeline WIC (DCT scale, Fant/HighQualityCubic, ICC, format converter, FlipRotator) không đổi. |
| EXIF | `Imaging/Decoding/Wic/WicExifReader.cs` (mới) | Orientation và `ExifSummary` qua một `IWICMetadataQueryReader` của frame đang decode (không đọc thêm stream). PROPVARIANT -> kiểu managed như `BitmapMetadata.GetQuery` nên `ExifQueryInterpreter` dùng chung. Lỗi metadata -> orientation 1 / không EXIF, không bao giờ ném. |
| Thumbnail | `Imaging/Decoding/EmbeddedThumbnailReader.cs` | `IWICBitmapFrameDecode::GetThumbnail` + converter Pbgra32 + `PixelOps.ApplyOrientation`; chỉ đọc header, APP1 và thumbnail. |
| Orientation | `ExifOrientation.cs` (thuần), `ExifOrientationWpf.cs` (WPF, tạm) | Phần thuần: `IsTransposed`, `Normalize`, `ReadFromWic`. WP-06 dời `ExifOrientationWpf.cs` sang Imaging.Wpf. |
| Codec WPF | `Imaging/Decoding/WpfBitmapSourceCodec.cs` (mới, tạm) | `FromPixels` = `BitmapSource.Create` + `Freeze` + giải phóng buffer (đúng một copy vào MIL như cũ); `ToPixels` copy ra buffer sở hữu (Bgr32/Pbgra32, định dạng khác được chuyển). WP-06 dời. |
| Nối | `App/Composition/DecoderProviders.cs`, `ServiceFactories.cs`, `tools/PhotoReview.Benchmark.Cli/*` | Truyền `WpfBitmapSourceCodec.Instance`. |

Số copy: WIC -> `PixelBuffer` (1) -> `BitmapSource` (2) với codec WPF, như đường cũ; codec pixel chỉ 1 (buffer chính là kết quả).

## 2. Lệch khỏi thẻ

1. **`EmbeddedThumbnailReader.TryRead(path)` một tham số giữ lại**, trả về codec WPF mặc định. Người gọi duy nhất ngoài vùng của gói là
   `Caching/ThumbnailCache.cs` (vùng WP-04); đổi chữ ký sẽ chạm file của WP-04 và xung đột. Overload `TryRead(path, codec)` là đường mới;
   WP-04 tiêm codec/decoder thumbnail rồi xoá overload một tham số.
2. **Thumbnail xuất `Pbgra32`, không phải BGRA thẳng.** Bản WPF cũ trả `Bgra32`; thumbnail JPEG có A = 255 nên byte giống hệt, và
   `Pbgra32` là layout hợp đồng C-01 hỗ trợ (chỉ Bgr32/Pbgra32). Cache đĩa thumbnail vẫn ghi PNG cùng định dạng (test parity 40 ca).
3. **`ActualBackend = Wpf` cho thumbnail giữ nguyên** dù mã nay là WIC. Nhãn backend là một phần định danh cache; đổi sang `WicDirect`
   sẽ làm mọi thumbnail đã cache bị coi là khác khoá. Đổi nhãn chỉ khi WP-04 có migration khoá cache.
4. **`WpfExifReader` giữ lại** (không xoá): các test và đường `WpfBitmapImageDecoder` (WP-06 xoá/dời) còn dùng; `WicExifReader` là
   đường mới cho WIC Direct và thumbnail. Parity giữa hai đọc EXIF được test trên corpus có sẵn.
5. **Ba chỗ cứng hoá sau checklist review của lead (cùng PR):**
   (a) `catch (ArgumentException)` trong `WicDirectDecoder.Decode` nay là `when (ex is not ArgumentNullException)`: WIC E_INVALIDARG vẫn
   là lỗi dữ liệu (`InvalidDataException`), còn `ArgumentNullException` do mã của ta không bị gán nhãn dữ liệu hỏng;
   (b) buffer đích do `AllocateAndFill` giải phóng ngay khi `CopyPixels` ném (trước đó chỉ finalizer của `SafeHandle` dọn);
   (c) `EnsureOutputFits` từ chối `bufferLength > int.MaxValue` bằng `DecoderMemoryAdmissionException` trên mọi máy, thay vì để
   `checked(int)` ném `OverflowException` (chuỗi fallback coi là lỗi thử lại được và giao cho WPF).

## 3. Test và số đo

- `WicDirectDecoderPixelParityTests`: 30 JPEG/PNG/TIFF/WebP/HEIC (khi có codec) x 8 orientation x {full, DecodeBox} so từng byte với
  `BitmapSource` cũ dựng ngay trong test; WebP 32 ca. `EmbeddedThumbnailReaderWicParityTests`: 40 ca. `ExifOrientationPureTests`,
  `WpfBitmapSourceCodecTests`, `WicDirectDecoderCopyCountTests` (số copy, `NativePixelMemory.LiveCount`).
- `WicDirectDecoderWp03ReviewTests` (17 ca mới): buffer giải phóng ngay khi copy ném (không cần GC), lỗi COM của ICC -> `NotSupportedException`,
  `ArgumentNullException`/`ArgumentException`/OOM đi qua đúng loại, kích thước > `int.MaxValue` -> admission, EXIF/APP1 hỏng không làm
  `TryRead` ném.
- Mutation (tay, Release, filter `Wic|Exif|Orientation|Thumbnail|PixelBuffer|Decoder`, ~2000 ca mỗi lần): **38/40 bị giết**
  (bảng dưới). 2 sống sót, cả hai là lưới an toàn không chạm tới được bằng fixture tổng hợp:
  `T07` (bỏ `ArgumentException` khỏi danh sách `catch` của `TryRead`: EXIF/APP1 hỏng thử 11 kiểu, WIC không ném `ArgumentException` ở đường
  thumbnail vì `WicExifReader` đã nuốt lỗi metadata; giữ catch như lưới cho codec/driver khác) và `T08` (bỏ kiểm `frameCount == 0`:
  tương đương, `GetFrame(0)` ném COM/Argument và cùng bị catch -> null). `M14` (không giải phóng buffer khi copy lỗi) sống lúc đầu ->
  đã viết `AllocateAndFill_*` kiểm `LiveCount` không qua GC, giờ bị giết.

  | Nhóm | Đột biến (đã giết trừ ghi chú) |
  |---|---|
  | `WicDirectDecoder` | M01/M02 bảng orientation 6/5; M03 bỏ clamp `targetW<origW\|\|targetH<origH`; M04 stride x3; M05 nhãn layout luôn Pbgra32; M06 PBGRA -> BGRA thẳng; M07 `originalWidth` bỏ chuyển vị; M08 `isTransposed=false`; M09 bỏ `SourceOrientation`; M10 xoay chỉ khi >2; M11 bỏ `CopyPixels` cuối; M12 bỏ kiểm `width>int.MaxValue`; M13 24bppBGR không còn opaque; M14 (sống -> giết); M15 `FitStored` bỏ transposed; M16 `ArgumentNullException` bị gán nhãn dữ liệu hỏng; M17 bỏ chặn > int.MaxValue; M18 `AllocateAndFill` bỏ cờ ICC |
  | `ExifOrientation` | E01 `IsTransposed` 8->7; E02 `Normalize` -> 2; E03 chặn trên 8->9; E04 tag 274->275; E05 catch ném lại; E06 bỏ fallback `System.Photo.Orientation` |
  | `WicExifReader` | X01 gốc IFD JPEG->TIFF; X02 bỏ cờ `readOrientation`; X03 bỏ `ExifSummary`; X04 chuỗi UTF8->UTF16; X05 vector UI2 -> null; X06 RATIONAL đọc 32 bit |
  | `EmbeddedThumbnailReader` | T01 `transposed=false`; T02 không áp orientation; T03 trần kích thước x4; T04 backend Wpf->WicDirect; T05 `downscaled` false; T06 layout Bgr32; T07 và T08 sống (trên) |
  | `WpfBitmapSourceCodec` | C01 bỏ `Freeze`; C02 format luôn Bgr32 |
- Bench: xem file perf ở trên.

## 4. Đề xuất cho hợp đồng v1.1 (không làm trong v1, hợp đồng đóng băng)

`ExifOrientation.IsTransposed(int)` và `Normalize(int)` nay là API công khai thuần, được cả decoder lẫn thumbnail dùng
và có test biên (5..8, ngoài 1..8 -> 1). Đề xuất khoá hai hàm này vào `contracts.v1.txt` ở v1.1 để shell Win32 và `PixelOps` không tự
định nghĩa lại bảng chuyển vị. Kèm theo: chuyển `ReadFromWic` thành hợp đồng EXIF (WP-06 quyết định vị trí cuối).
