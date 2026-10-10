---
id: NOWPF-WP04
order: 422
summary: |-
  WP-04 (NO-WPF-EXEC-PLAN, 2026-10-10): cache đĩa (JPEG q95, PNG) encode/decode bằng WIC trên PixelBuffer; định dạng file không đổi (byte-identical, đọc chéo hai chiều). Codec WPF là MẶC ĐỊNH khi không truyền codec (WP-06 phải bắt buộc). JPEG chậm hơn 17-30 % do hai bản chép của cầu WPF (chủ dự án quyết); ba thay đổi hành vi nhỏ, không nguy hiểm.
---

# NOWPF-WP04-CACHE-WIC - cache đĩa bằng WIC (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md), thẻ WP-04 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Đo hiệu năng: [perf/2026-10-10-wp04-cache-wic.md](../perf/2026-10-10-wp04-cache-wic.md).

## 1. Đã làm

- `WicImageEncoder` (JPEG/PNG qua `IWICBitmapEncoder`, cùng vendor và tham số WPF đặt: không cache, 96 dpi, `ImageQuality = q/100`)
  và `WicCacheImageReader` (giải mã payload trong bộ nhớ thẳng vào `PixelBuffer`, một lần đọc file, kiểm kích thước header TRƯỚC khi cấp phát).
- `PreviewCacheFile.Read` trả `PixelBuffer`; ghi nhận `PixelBuffer` (nội bộ) hoặc `IDecodedImage` + `IPlatformImageCodec` (C-02).
  `DiskCacheStore.WriteAtomicallyAsync` và `ThumbnailCache` tương tự. `PreviewImageService` giữ `IDecodedImage` trong hàng đợi persist,
  chỉ `ToPixels` khi ghi thật.
- `WpfCacheImageCodec` (Imaging/Decoding): codec C-02 tạm của bản WPF (BitmapSource Freeze <-> PixelBuffer).
- Hết bộ nhớ (E_OUTOFMEMORY của WIC) được ném thành `InsufficientMemoryException`, KHÔNG phải "entry hỏng": entry cache không bị xoá và
  không rơi sang đường giải mã từ nguồn (`COMException` là lỗi entry theo `DiskCacheStore.IsCacheEntryFailure`).

## 2. Cần chủ dự án / WP-06 biết

1. **Codec WPF là mặc định khi không truyền codec.** `PreviewCacheFile.WriteAtomicallyAsync(IDecodedImage, ...)`,
   `ReadAsDecodedImage(path)`, `DiskCacheStore.WriteAtomicallyAsync(IDecodedImage, ...)`, `ThumbnailCache`/`PreviewImageService`
   (`platformCodec = null`) đều rơi về `WpfCacheImageCodec.Instance`. App WPF không phải đổi gì. WP-06 (tách `PhotoReview.Imaging.Wpf`)
   phải bỏ các overload/mặc định này và nhận codec bắt buộc qua DI.
2. **Ba thay đổi hành vi, đều an toàn với cache cũ** (không có trường hợp cache cũ bị hiểu sai: header, payload và khoá không đổi):
   - Entry JPEG mà `BitmapImage` cũ từ chối vì đoạn metadata/EXIF trong JPEG hỏng, nhưng ảnh quét pixel đầy đủ, giờ vẫn được dùng từ cache
     (WIC giải mã pixel, không dựng metadata). Entry cắt cụt vẫn bị loại bởi kiểm EOI (`FFD9`).
   - Ảnh `Rgba64`/`Prgba64`/float đục (mọi pixel alpha = 1) trước đây không bao giờ được persist ("không xác minh được đục"), nay chuyển
     Pbgra32 rồi quét alpha và được cache dưới dạng JPEG Bgr32. Ảnh có pixel trong suốt vẫn KHÔNG được cache (Q-R7 giữ nguyên).
   - PNG thumbnail có alpha đi qua Pbgra32 (premultiply rồi bỏ premultiply khi ghi) nên màu ở vùng alpha rất thấp lệch nhẹ so với bản WPF
     ghi thẳng Bgra32. Chỉ ảnh hưởng ảnh nguồn có alpha; thumbnail đọc lại là Pbgra32 như trước.
3. Thumbnail đọc từ đĩa nay có `ActualBackend = WicDirect`, `Orientation = 1` (WPF cũ: `Wpf`; PNG đã chứa pixel đã xoay). Không nơi nào
   trong App đọc `ActualBackend` của thumbnail; entry preview vẫn ghi đúng backend thật vào header.

4. **Hiệu năng JPEG chưa đạt tiêu chí thẻ (persist P50 không tệ hơn 10 %)**: ghi +28-30 %, đọc +17-21 % ở 24/12 MP (PNG ngang, thumbnail nhanh hơn 10 %), vì cầu WPF thêm hai bản chép toàn khung (chi tiết trong perf). Quy ra preview cỡ viewport: ~+4 ms trên luồng persist nền, ~+3 ms đọc. Hai lựa chọn: (a) chấp nhận, hai bản chép biến mất khi ảnh nền tảng là PixelBuffer (WP-06+); (b) lối tắt băng-dải trong cầu WPF, cần mở rộng C-02 đóng băng. Đề xuất (a).

## 3. Kiểm chứng

- `PreviewCacheCrossVersionTests`: file cũ (fixture commit `tests/Fixtures/PreviewCache`, sinh bằng mã trước WP-04) đọc bằng mã mới cùng pixel
  với đường WPF cũ; mã mới tái tạo từng byte file fixture; file mới đọc được bằng `BitmapImage`.
- `WicCacheImageIoTests`: chuyển BGRX->BGR (vector + đuôi), chất lượng JPEG, layout/alpha, hộp thu nhỏ, kích thước header, phân loại lỗi.
- Mutation: xem mục 5 của [perf](../perf/2026-10-10-wp04-cache-wic.md) (kết quả đột biến chọn lọc ở phần mới).
- Lỗi đã biết, có từ trước PR: ở cấu hình Debug `SourceBytesCache.GetOrRead` gọi `Debug.Fail` làm ~30 test của Imaging/App.Tests đỏ
  (ghi trong `docs/MUTATION-TESTING.md`); chạy Release thì xanh.
