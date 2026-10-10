# WP-03 - WIC decode ra PixelBuffer: bench decode 24 MP -> 1920 px (2026-10-10)

Mục tiêu (thẻ WP-03): `WicDirectDecoder` mới (WIC -> `PixelBuffer` -> codec WPF) không chậm hơn master quá 5 % ở decode 24 MP -> 1920 px.
Rủi ro đã nêu: `GC.AddMemoryPressure` cho buffer tạm 24 MP (mỗi `PixelBuffer.Allocate` đăng ký áp lực bộ nhớ native).

## Phương pháp

- Harness có sẵn: `PhotoReview.Benchmark.Cli --decoder-bench <folder> <out> WicDirect 1920 8` (Release, cache OS đã làm nóng, xen kẽ theo file).
- Bộ ảnh: 12 JPEG 4000x6000 (24 MP, copy từ F4) -> đầu ra 1920x2880; 8 lần lặp/file = 96 lần decode mỗi lượt chạy.
- Base = `origin/master` (f78beb41, `WicDirectDecoder` dựng `BitmapSource` trực tiếp), new = nhánh WP-03 (cùng `-c Release`).
  6 lượt, **xen kẽ base/new** từng lượt (cùng tiến trình bench, cùng thư mục ảnh). Codec new = `WpfBitmapSourceCodec` (đúng đường app).
- Máy: i7-9750H 6C/12T, 31.9 GB, NVMe. Lưu ý: máy đang chạy nhiều agent song song (tải 40-100 % CPU) nên có nhiễu; lượt 4 bị nhiễu nặng.

## Kết quả (median DecodeMs mỗi lượt, ms)

| Lượt | base | new | new so với base |
|---|---|---|---|
| 0 | 310.4 | 317.1 | +2.2 % |
| 1 | 306.0 | 313.2 | +2.3 % |
| 2 | 361.4 | 377.8 | +4.5 % |
| 3 | 358.0 | 368.0 | +2.8 % |
| 4 (nhiễu tải) | 344.3 | 284.0 | -17.5 % |
| 5 | 278.1 | 280.3 | +0.8 % |
| **Gộp 576 lần decode, median** | **315.7** | **310.0** | **-1.8 %** |
| Gộp bỏ lượt 4, median | 312.5 | 321.6 | +2.9 % |

P95 gộp từng lượt: base 303-483 ms, new 304-459 ms (trong nhiễu). Bộ nhớ cấp phát (managed) trung bình 15.2 MB mỗi decode ở cả hai.

## Kết luận

- Chênh lệch trong ngưỡng: +2.9 % (5 lượt yên) đến -1.8 % (gộp tất cả), dưới ngưỡng 5 % của thẻ. Không có hồi quy đáng kể.
- Năm lượt yên đều nghiêng nhẹ về phía new chậm hơn (+0.8 đến +4.5 %). Nguyên nhân khả dĩ: `PixelBuffer.Allocate` mới = `AlignedAlloc` 15.8 MB +
  `GC.AddMemoryPressure`/`RemoveMemoryPressure` mỗi decode (buffer tạm của WIC), và thêm một lớp gọi qua codec; đường cũ cũng có một buffer scratch
  native nên số copy không đổi (2). Không có dấu hiệu GC tăng: bộ nhớ managed cấp phát mỗi decode bằng nhau. Không đủ sức mạnh thống kê để tách
  +2-3 % khỏi nhiễu của máy đang tải; nếu cần số chắc hơn, chạy lại khi máy rảnh (`--decoder-bench`, >= 12 lượt).
- Với codec pixel (shell Win32) không có copy thứ hai nên sẽ nhanh hơn; số đo riêng thuộc WP-06/WP-05 (bench TurboJpeg).
