---
id: NOWPF-WP02
order: 282
summary: |-
  WP-02 (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): thực thi hợp đồng v1 C-01 (PixelBuffer native căn 64 byte qua SafeHandle), C-02 (PixelBufferImageCodec, PixelLease, DecodedImage) và C-03 (PixelOps.ApplyOrientation khớp từng byte WPF TransformedBitmap trên 8 orientation; IsFullyOpaque/HasAlpha khớp quyết định cache của PreviewCacheFile trên 6 định dạng) + helper test PixelAssert; chưa nối vào app (WPF không đổi hành vi), hợp đồng không đổi; lệch thẻ: PixelAssert ở TestSupport.Windows, LiveCount luôn bật trên lớp nội bộ NativePixelMemory.
---

# NOWPF-WP02-PIXELBUFFER - PixelBuffer, codec, PixelOps (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) mục 5 (C-01..C-03), thẻ WP-02 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Khung hợp đồng: [NOWPF-WP01-SCAFFOLD](NOWPF-WP01-SCAFFOLD.md). `ContractSurfaceTests` xanh, file duyệt `contracts.v1.txt` không đổi.

## 1. Đã làm

| Phần | File | Ghi chú |
|---|---|---|
| C-01 | `src/PhotoReview.Imaging/Pixels/PixelBuffer.cs`, `NativePixelMemory.cs` (mới, internal) | `NativeMemory.AlignedAlloc` căn 64 byte + `GC.AddMemoryPressure`; giải phóng một lần ở Dispose hoặc finalizer critical của `SafeHandle`. Nội dung sau `Allocate` chưa khởi tạo. `Width > int.MaxValue/4` hoặc layout lạ -> `ArgumentOutOfRangeException`. `Address`/`GetRow`/`TryGetSpan` sau Dispose -> `ObjectDisposedException`; hình học vẫn đọc được. |
| C-02 | `Decoding/PixelBufferImageCodec.cs`, `PixelLease.cs`, `DecodedImage.cs` | Codec `"pixels"`: `FromPixels` trả nguyên buffer, `ToPixels` mượn (`Owned=false`); kiểu lạ -> `ArgumentException`, buffer đã giải phóng -> `ObjectDisposedException`. `DecodedImage` kiểm tham số (ảnh khác null, kích thước >= 1, ước lượng >= 0, kích thước phải khớp khi ảnh là `PixelBuffer`); `OriginalWidth/Height <= 0` -> kích thước pixel như `WpfDecodedImage`. |
| C-03 | `Pixels/PixelOps.cs` | Orientation 2..4 theo dòng (`CopyTo` + `Span.Reverse`), 5..8 chuyển vị theo ô 32x32. Nếu ném thì src không bị Dispose. `IsFullyOpaque` quét từng dòng, vector hoá, dừng sớm. |
| Csproj | `PhotoReview.Imaging.csproj` | `AllowUnsafeBlocks` (AlignedAlloc trả `void*`, PixelOps đọc qua con trỏ). |
| Test | `tests/PhotoReview.Imaging.Tests/Pixels/*.cs` (5 file, 193 ca) | Xem mục 3. |
| Helper | `tests/PhotoReview.TestSupport.Windows/PixelAssert.cs` | `CreatePattern`, `Clone`, `ToBitmapSource`, `FromBitmapSource`, `Equal` (từng byte, báo pixel/kênh lệch đầu tiên), `MeanAbsoluteError`, `Psnr` - cho WP-03..05. |

Chưa nối vào app: không file nào ngoài test dùng kiểu mới (WP-03/04/05 nối).

## 2. Lệch khỏi thẻ (cần lead biết)

1. **`PixelAssert` ở `TestSupport.Windows`, không ở `TestSupport`.** `TestSupport` là `net10.0` (Core.Tests dùng) nên không tham chiếu được
   `PhotoReview.Imaging` (`net10.0-windows`); cầu nối `BitmapSource` cũng cần WPF. Namespace `PhotoReview.TestSupport.Windows`.
2. **`LiveCount` không nằm trên `PixelBuffer` và không Debug-only.** `ContractSurface` tính cả thành viên `internal` nên thêm
   `PixelBuffer.LiveCount` sẽ đổi hợp đồng (và `#if DEBUG` làm bề mặt khác giữa Debug/Release). Bộ đếm ở lớp nội bộ
   `NativePixelMemory.LiveCount`, luôn bật (một `Interlocked` mỗi cấp/giải phóng) để test finalizer chạy được ở Release.
3. **Ca > 2 GB** dựng `PixelBuffer` giả bằng `[UnsafeAccessor]` tới constructor private + khối native chưa cấp phát; không cấp phát thật.
4. **Mutation:** thẻ ghi Stryker do sub-agent haiku chạy; lần này agent chính tự chạy Stryker (số ở mục 3).
5. **Lint TEST-02** không nhận `PixelAssert.Equal(...)` là assertion (regex cần `\bAssert`); test chỉ dùng helper phải có thêm một
   `Assert.*`. Gợi ý cho lead (v1.1 của luật test, không phải hợp đồng): thêm `PixelAssert\.` vào `AssertionToken`.

## 3. Test và số đo

- `PixelBufferTests` + `PixelBufferLifetimeTests` (collection `GlobalState`): hình học, căn lề, Id tăng, tham số sai, dòng liền nhau,
  `TryGetSpan` biên 2^31 (7 kích thước giả), Dispose hai lần, dùng sau Dispose, `LiveCount` +1/-1 đúng một lần, finalizer trả 16 buffer bị bỏ.
- `PixelOpsOrientationTests`: 96 ca so **từng byte** (kể cả byte X của Bgr32) với `ExifOrientation.Apply` (WPF `TransformedBitmap`) - 8
  orientation x {1x1, 1x7, 7x1, 2x3, 33x31, 70x45} x {Bgr32, Pbgra32}; vị trí pixel đã biết; quyền sở hữu; property (seed cố định qua
  `PropertyRunner`): `o` rồi nghịch đảo `o` = gốc, `6∘6 = 3`, `6^4 = id`.
- `PixelOpsAlphaTests`: `IsFullyOpaque`/`HasAlpha` khớp `PreviewCacheFile.IsFullyOpaque(BitmapSource)` trên Bgr24, Bgr32, Gray8, Bgra32,
  Pbgra32, Indexed8 (có/không pixel trong suốt); một pixel A=254 ở mọi vị trí (đuôi vector, dòng cuối).
  Ngữ nghĩa khác tên: `PreviewCacheFile.HasAlpha` = "định dạng có thể mang alpha"; `PixelOps.HasAlpha` = "có pixel trong suốt thật".
- `PixelBufferImageCodecTests`, `PixelAssertTests` (helper bắt được một byte lệch, sai kích thước/layout).
- Đo nhanh (một lần, máy đang chạy ~9 agent, chỉ để định hướng; 6000x4000 Bgr32, median 7 lần, gồm cấp buffer đích):
  `PixelOps.ApplyOrientation` o=3/5/6/8 = 112/164/228/158 ms; `ExifOrientation.Apply` (WPF) = 302/267/242/308 ms. Gate thật là
  bench TurboJpeg của WP-05.
- Mutation (Stryker 5.0, Basic, Release, filter chuẩn, `--mutate` 6 file của gói): xem PR (điền khi chạy xong).
