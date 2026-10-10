---
id: NOWPF-WP05
order: 542
summary: |-
  WP-05 (đợt 1b, 2026-10-10): TurboJpegDecoder(codec) và LibRawDecoder(codec) giải mã thẳng vào PixelBuffer (C-01) rồi IPlatformImageCodec.FromPixels (C-02); ctor không tham số giữ đường WPF. Không-fine-scale: byte-exact với WPF (8 orientation x 10 cỡ). Fine-scale: bộ lọc diện tích lệch WIC Fant MAE 0,26-1,12 / PSNR 40,6-51,6 dB trên 3 ảnh thật nhưng CHẬM hơn 30-90 % (26 MP): lead quyết (a) giữ + tối ưu hay (b) IWICBitmapScaler Fant trước WP-06. Mutation: 28 đột biến, sống sót là tương đương/không kiểm được (không có RAW corpus).
---

# NOWPF-WP05 - TurboJpeg và LibRaw ra PixelBuffer (2026-10-10)

Thẻ: [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md) mục WP-05. Hợp đồng v1 không đổi. Số đo: [perf/2026-10-10-wp05-native-pixelbuffer.md](../perf/2026-10-10-wp05-native-pixelbuffer.md).

## Đã làm

- `TurboJpegDecoder(IPlatformImageCodec)`: tj3Decompress8 ghi vào `PixelBuffer.Address`, fine-scale bằng `PixelAreaResampler` (internal), orientation bằng `PixelOps.ApplyOrientation`, trả `DecodedImage`. Ctor không tham số = đường WPF cũ.
- `LibRawDecoder(IPlatformImageCodec, stageObserver?)`: `BgraBuffer` bọc `PixelBuffer`; `HandOff` chuyển quyền sở hữu cho codec không copy. Ctor không tham số = đường WPF cũ.
- `ServiceFactories.CreateRawDecoder(..., IPlatformImageCodec? codec = null)` (một hunk). Imaging.Raw không đụng WPF: không cần sửa.

## QUYẾT ĐỊNH cần lead: fine-scale (a) giữ bộ lọc diện tích hay (b) WIC Fant

Đo trên 3 JPEG thật 25-33 MP (xem perf). Kích thước luôn trùng WIC; MAE 0,26-1,12, PSNR 40,6-51,6 dB (checkerboard giả lập: MAE <= 3,6).

| | (a) giữ `PixelAreaResampler` | (b) `IWICBitmapScaler` Fant qua WicInterop |
|---|---|---|
| Giống WPF | không từng byte; lệch <= ~1 mức xám TB | từng byte (đường WPF cũ dùng đúng Fant) |
| Tốc độ 26 MP có fine-scale | chậm hơn WIC 30-90 % (một luồng, int64) | bằng WPF cũ (SIMD của WIC) |
| Phụ thuộc | không WIC/WPF, thuần managed, xác định | COM WIC, cần InternalsVisibleTo/API công khai từ WP-03/06 cho TurboJpeg |
| Rủi ro | vượt ngưỡng bench 5 % của thẻ | thêm COM lifetime; vẫn không WPF |

Khuyến nghị: **(b)**, hoặc (a) kèm tối ưu SIMD/int32 và đo lại. Không đổi mặc định hiện tại (a) trong PR này; app chưa nối codec nên chưa có hồi quy trong app.

## Mutation (28 đột biến chọn lọc, `Imaging.Tests` filter mục tiêu, 650 ca)

- PixelAreaResampler (9): làm tròn nửa, trọng số, biên s1, upscale clamp, alpha, điều kiện emit, kênh, copy cùng cỡ, overlap dọc: **9/9 bị diệt**.
- TurboJpegDecoder (11): orientation, fine-scale (bỏ/`&&`), leak sau resize, codec ném, cờ downscaled, kích thước gốc theo transposed, leak khi decode lỗi, ByteCount, Orientation báo, stride: diệt 9; test lấp thêm: `CodecThrows_BufferIsFreed`, `FineScale_OnlyHeightAboveTarget...`.
  Sống sót: `targetH < origH` trong cờ downscaled và `||` -> `&&` của `needsFineScale`: tương đương thực tế (Fit giữ tỉ lệ nên trục kia cũng nhỏ đi; thử 3000x2001 không phân biệt được).
- LibRaw (8): TakePixels không null, Dispose rò, catch HandOff rò, thứ tự stage, ByteCount, dim gốc, downscaled, `image.Dispose()`: diệt 6 (sau test lấp `BgraBuffer_Dispose_FreesTheNativeMemoryExactlyOnce`, `HandOff_ReportsSourceReleased_...`).
  Không kiểm được: `image.Dispose()` trước HandOff và truyền `downscaled` trong `DecodeCore` (thân native, cần RAW corpus; máy này không có, test Native trả sớm).
- Lưu ý bẫy kiểm: `LibRawGateAndGuardMutationGapTests.FullDecodeQueuedWaiters...` (10 s) và `LibRawAdmissionSeamTests...ReleasesTheSlot` **chập chờn khi máy 100 % CPU**, làm rò slot cổng và rớt dây chuyền 4-6 test khác; không liên quan WP-05 (đột biến "bị diệt" chỉ bởi các test này đã chạy lại).

## Checklist review decoder native (lead, 2026-10-10)

1. Handle/pin sống lâu hơn con trỏ dùng nó: ĐÃ KIỂM. `DecodeCore` khai `cancellationState`, rồi `bufferPin`, rồi `raw` trong `using` lồng: thứ tự giải phóng raw (libraw_close) -> pin -> state. `CancellationState` không readonly (GCHandle), có test free-một-lần sẵn. Không đổi.
2. Kích thước bằng `long` rồi chặn trước cấp phát: ĐÃ KIỂM + ĐÃ THÊM TEST. TurboJpeg: `CalculateOutputBuffer` (long, InvalidDataException) rồi `EnsureOutputFits` trước `PixelBuffer.Allocate`; test mới `HugeClaimedSize_IsAdmissionRefusal_NotOverflow_AndAllocatesNothing` (đường codec). LibRaw: `ValidateTargetLength` (long) trước `BgraBuffer`; `checked(int)` trong BgraBuffer không với tới được.
3. PixelBuffer Dispose đúng một lần, HandOff không free hai lần: ĐÃ THÊM TEST (`BgraBuffer_Dispose_...Once`, `TakePixels_TransfersOwnershipOnce`, HandOff ném -> LiveCount về cũ; TurboJpeg codec ném -> LiveCount về cũ).
4. OOM không bị coi là dữ liệu hỏng: ĐÃ KIỂM. `PixelBuffer.Allocate` ném OutOfMemoryException (không InvalidData/COM), `IsRecoverablePreviewFailure` trả false; LibRaw bắt OOM thành lỗi tài nguyên (dòng ~220). TurboJpeg đường codec đổi OOM của bước resize/orientation sang `DecoderMemoryAdmissionException` như đường cũ. Không có test cho OOM thật (không giả lập được allocator).
5. ICC lỗi/có ICC rơi sang đường quản lý màu: ĐÃ THÊM TEST `IccProfile_FallsBackWithoutAllocating` (NotSupportedException trước mọi cấp phát pixel).
6. Comment cap header TurboJpeg: ĐÃ SỬA (HeaderReadCap 8 MB, ExtendedHeaderCap 64 MB nêu rõ ở hai chỗ).
7. `RawDecoder` 2-3 lần stat mỗi decode: CHỈ GHI NHẬN. Chưa có số đo nên không tối ưu; không đổi mã.

## Lệch thẻ / ghi chú

- Ctor không tham số còn tồn tại (328 tham chiếu test, app WPF); WP-06 xoá nhánh WPF (`FinishWithWpf`, `ToBitmap`) khi bỏ UseWPF.
- `DecoderProviders.cs` (vùng WP-03) chưa truyền codec cho TurboJpeg: WP-06/lead nối.
- Bench P50 tuyệt đối không tin được (máy 100 % CPU do agent khác); cần đo lại yên tĩnh. Đường WPF cũ nhánh vs base: không hồi quy nhất quán theo min.
