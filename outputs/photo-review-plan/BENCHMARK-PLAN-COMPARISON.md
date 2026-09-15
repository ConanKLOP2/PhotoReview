# So sánh plan và kết quả benchmark

| Hạng mục trong plan | Kết quả hiện tại | Đánh giá |
|---|---|---|
| Nhiều profile ổn định | Registry có các nhóm tốc độ, preview, cache, RAM, storage, action và logging | Đạt về cấu hình |
| Original tách khỏi speed ranking | `CorrectnessOnly` và ranking loại profile này | Đạt |
| Engine có cancellation/progress/logging | Có trong `BenchmarkEngine` | Đạt contract |
| Decode ảnh thật | `BenchmarkImageExecutor` dùng WPF BitmapImage, cache key và FileShare.Delete | Đạt |
| CLI chạy nhiều profile | Có list, benchmark, all và actions | Đạt chức năng |
| UI chọn nhiều profile | Có list chọn nhiều profile và chạy tuần tự | Đạt cơ bản |
| Move/Delete/Copy trong lúc đọc | Regression test dùng temp copy; benchmark action không đụng nguồn | Đạt an toàn |
| Bảng so sánh P50/P95/P99 | JSON có phase statistics; bảng markdown hiện ghi P50/P95/Max cho các profile đã đo | Đạt một phần; UI chưa có bảng ranking đầy đủ |
| Resource matrix | Có số liệu workers 2/4/8 và RAM/cache từ preload benchmark | Đạt baseline; chưa đủ HDD/network thực tế |
| Benchmark trên ảnh thật | Đã chạy 98 ảnh, 773 MB, nguồn read-only | Đạt |
| Report/export | JSON per profile và summary; UI mở report/result folder | Đạt JSON; CSV chưa có |
| DiagnosticsWindow integration | Metrics benchmark hiển thị trong BenchmarkWindow, viewer diagnostics giữ metrics riêng | Đạt cơ bản |
| Release validation | Test, publish và push `origin/master` pass | Đạt |

Kết luận: phần nền tảng và baseline thực tế đã có. Hai giới hạn còn lại cần ghi rõ khi sử dụng kết quả: bảng ranking đầy đủ chưa được render thành bảng UI, và CSV export chưa được triển khai. Các số liệu CLI đầu tiên chỉ có ý nghĩa cho những profile đã chạy trong bảng kết quả; không được suy diễn thành ranking cho toàn bộ registry.
