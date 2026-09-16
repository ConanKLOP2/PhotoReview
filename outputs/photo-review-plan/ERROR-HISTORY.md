# Lịch sử lỗi và test chống hồi quy

Cập nhật: 2026-09-15

Tài liệu này ghi các lỗi thực tế đã gặp trong PhotoReview, nguyên nhân đã xác định và test bảo vệ tương ứng. Mỗi lỗi mới phải bổ sung một mục và ít nhất một test có thể chạy tự động.

| Mã | Lỗi đã gặp | Nguyên nhân | Test bảo vệ |
|---|---|---|---|
| E-001 | Mở file trực tiếp hiện cùng ảnh từ `index=0` rồi đổi sang `index=1`, có thể skip ảnh. | Catalog fallback được hiển thị trước, Explorer native order về muộn và reindex lại. | Contract Explorer reindex giữ `currentPath`; WPF UI probe mở file trực tiếp và kiểm tra index. |
| E-002 | Explorer snapshot về sau ghi đè thứ tự sau khi người dùng đã Next. | Kết quả async cũ không biết catalog đã có tương tác. | `Explorer snapshot cannot reindex after user catalog interaction`. |
| E-003 | Ảnh đã preload nhưng Next vẫn hiện “Đang tải”. | `ShowImageAsync` set trạng thái loading trước khi kiểm tra RAM cache; cache chỉ key theo path. | WPF `--ui-next-probe`: warm Next và 30 lượt warm navigation phải không chứa `Đang tải`. |
| E-004 | Next warm vẫn chậm hàng giây. | Đọc kích thước gốc bằng `BitmapCacheOption.OnLoad` khiến WIC decode source lần thứ hai. | WPF warm benchmark ghi median/P95/max; hiện 11/25/34 ms trên 30 ảnh thật. |
| E-005 | Preload không tận dụng RAM, chỉ chạy ít ảnh và bị ngắt liên tục. | Batch thực tế chỉ 2 task, yield quá thường xuyên, tổng bytes folder có sau lượt preload đầu. | `Full-folder preload prioritizes ...`; benchmark worker 2/4/8; log preload progress. |
| E-006 | Bitmap cũ được dùng sau resize, đổi mode hoặc file bị thay thế cùng path. | RAM cache/in-flight key chỉ có path. | `ImageCacheKeyTests`: phân biệt width, Original và fingerprint source. |
| E-007 | Clear cache/Move/Delete xong decode cũ tự nạp lại cache. | Decode đã bắt đầu không biết cache lifecycle đã đổi. | Cache epoch test và guard `cacheEpoch == _cacheEpoch`; action test kiểm tra eviction. |
| E-008 | Xóa/Move khi decoder đang đọc làm thao tác bị block hoặc viewer đen. | Đọc file và thao tác file tranh chấp handle/UI; kết quả đọc muộn có thể ghi đè UI. | Sequence tests Next → Move/Delete; source mở `FileShare.Delete`; generation guard không present stale bitmap. |
| E-009 | Delete/Move hoàn tất nhưng viewer Next hai lần, bỏ qua ảnh. | Completion action gọi điều hướng lần nữa sau khi đã advance trước filesystem operation. | `File action completion does not advance twice`; interleaved action sequence. |
| E-010 | Log bị chậm, thiếu dòng hoặc nhiều dòng xen lẫn. | Ghi log đồng bộ hoặc nhiều thread ghi trực tiếp cùng file. | Concurrent logging preserves every entry; line-delimited log test. |
| E-011 | Log `ShowImage failed FileNotFoundException` xuất hiện sau Move/Delete. | Async render vẫn dùng path đã bị xóa. | Stale-file path test và `ShowImage stale-file` handling. |
| E-012 | Retry filesystem có thể thực hiện sai nguồn/đích. | Recovery không kiểm fingerprint trước khi replay. | Recovery retry fingerprint validation và failed journal tests. |
| E-013 | Index hiển thị khác log gây hiểu nhầm. | Log dùng zero-based (`index=0` là ảnh 1/), UI dùng one-based. | Test counter và log path/currentIndex; tài liệu phải ghi rõ quy ước. |
| E-014 | Bật ghi cache đĩa cho `PreviewImageService` (PR-029) khiến test ghi file PNG thật vào `%LocalAppData%\PhotoReview\cache` của máy dev. | `GetDiskCachePath` hardcode `LocalApplicationData`, không có tham số override như `ThumbnailCache.diskDirectory`. | `PreviewImageServiceDiskCacheTests`/`DiskCacheStoreTests` luôn truyền `diskCacheDirectory` trỏ vào `TempRoot`; mọi nơi dựng `PreviewImageService` trong test (xUnit + console harness) đã cập nhật theo. |

## Bộ performance/regression test bổ sung

- `PerformanceTestHarness`: đo cold read, đọc song song, P50/P95/max, queue wait, working set và xuất JSON; ngưỡng là tương đối nên máy chậm chỉ sinh `WARN`.
- `FileActionConcurrencyTests`: giữ `FileStream` mở với `FileShare.Delete` rồi Move/Delete/Copy, kiểm tra thao tác hoàn tất và chuỗi xen kẽ.
- `CacheExplorerRegressionTests`: kiểm cache variant/fingerprint, preload order không trùng và Explorer snapshot thiếu/trùng/ngoài folder.
- `LocalUiNextProbe`: chạy WPF STA trên folder ảnh thật, warm 30 ảnh rồi điều hướng từng index; kiểm không loading, không sai path và đo P50/P95/max.

## Quy ước bắt buộc khi thêm lỗi mới

1. Ghi lại timestamp, path/folder, thao tác người dùng và log liên quan.
2. Xác định invariant bị vi phạm: path hiện tại, index, generation, cache identity hoặc filesystem state.
3. Thêm test tái hiện lỗi trước khi đánh dấu đã sửa.
4. Chạy `dotnet run --project PhotoReview.Tests -c Release` và cập nhật mã lỗi trong tài liệu này.
