---
id: NOWPF-WP05
order: 542
summary: |-
  WP-05 (đợt 1b, 2026-10-10): TurboJpegDecoder(codec) và LibRawDecoder(codec) giải mã thẳng vào PixelBuffer (C-01) rồi IPlatformImageCodec.FromPixels (C-02); ctor không tham số giữ nguyên đường WPF (BitmapSource). Không-fine-scale: so từng byte với WPF trên 8 orientation x 10 kích thước; fine-scale dùng bộ lọc diện tích số nguyên, lệch WIC Fant vài mức (MAE <= 3,6). Chưa chạy mutation, bench P50 và test corpus RAW (không có corpus) - xem PR.
---

# NOWPF-WP05 - TurboJpeg và LibRaw ra PixelBuffer (2026-10-10)

Thẻ: [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md) mục WP-05. Hợp đồng v1 không đổi.

## Đã làm

- `TurboJpegDecoder(IPlatformImageCodec)`: tj3Decompress8 ghi vào `PixelBuffer.Address`, fine-scale bằng `PixelAreaResampler` (internal), orientation bằng `PixelOps.ApplyOrientation`, trả `DecodedImage`. Ctor không tham số = đường WPF cũ (một lần copy như trước).
- `LibRawDecoder(IPlatformImageCodec, stageObserver?)`: `BgraBuffer` bọc `PixelBuffer`; `HandOff` chuyển quyền sở hữu cho codec không copy. Ctor không tham số = đường WPF cũ.
- `ServiceFactories.CreateRawDecoder(..., IPlatformImageCodec? codec = null)` (một hunk).
- Imaging.Raw không đụng WPF (giải mã preview nhúng qua decoder chuẩn): không cần sửa.

## Lệch thẻ / quyết định cần lead

1. Fine-scale không so được từng byte với WPF/WIC Fant: đo trên fixture checkerboard (400x300, 640x480; 13 cỡ đích x orientation 1,6) kích thước luôn giống, MAE 0,26-3,6, PSNR 30,6-48,8 dB; không fine-scale thì MAE 0. Phương án: (a) giữ bộ lọc diện tích (hiện tại), (b) dùng `IWICBitmapScaler` Fant qua WicInterop của WP-03 để giống từng byte (cần InternalsVisibleTo hoặc API công khai từ WP-03/06).
2. Ctor không tham số còn tồn tại (328 tham chiếu test, app WPF); WP-06 xoá nhánh WPF (`FinishWithWpf`, `ToBitmap`) khi bỏ UseWPF.
3. DecoderProviders.cs (vùng WP-03) chưa truyền codec cho TurboJpeg: WP-06/lead nối.