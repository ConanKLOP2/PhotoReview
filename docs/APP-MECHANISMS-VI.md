# PhotoReview — cơ chế quan trọng và đo tốc độ load ảnh

Tài liệu này ghi lại các bất biến kỹ thuật cần giữ khi sửa pipeline. Mô tả được đối chiếu với source hiện có; con số hiệu năng chưa được xem là đã chứng minh nếu chưa có runtime trace/benchmark. Contract tests không thay thế GUI acceptance hoặc số đo P95.

## Pipeline và mốc đo

```text
Chọn folder/file
  -> enumerate file top-level + lọc định dạng
  -> bắt đầu truy vấn thứ tự Explorer
  -> trình bày danh sách theo fallback sort
  -> ảnh đầu tiên: thumbnail (Preview mode) -> adaptive/original decode -> UI present
  -> preload nền theo thứ tự lân cận/toàn folder nếu ngưỡng cho phép
  -> áp dụng snapshot Explorer hợp lệ và đồng bộ selection/order
```

Đo riêng: T0 thao tác chọn → T1 catalog sẵn sàng → T2 ảnh đầu tiên nhìn thấy → T3 ảnh adaptive/chất lượng cao hơn; với chuyển ảnh, đo keypress → present. Không gộp thành một con số “LoadFolder”, và không để Explorer, compare/hash hay preload chặn T2 nếu thay đổi thiết kế.

### Chế độ load và cache

| Chế độ | Hành vi khi cache RAM miss | Lưu ý |
|---|---|---|
| Fast | Decode adaptive theo viewport/DPI rồi present. | Bỏ pha thumbnail riêng. |
| Preview (mặc định) | Có thể present thumbnail trước rồi thay bằng adaptive preview. | Có thể phải đọc/decode nguồn thêm lần nữa. |
| Original | Decode không giới hạn `DecodePixelWidth`. | Tốn thời gian/RAM hơn; pixel gốc chỉ được đảm bảo khi bitmap đúng key được decode/present. |

Preview được decode trên worker, dùng `BitmapCacheOption.OnLoad`, đóng stream sau decode và `Freeze()` trước khi dùng ngoài UI thread. Adaptive bitmap có thể được persist bất đồng bộ vào disk cache; hàng đợi bị giới hạn, ghi atomically, prune có quota, và Original không ghi bitmap full-resolution này. RAM LRU và in-flight dedup dùng `ImageCacheKey`, gồm full path chuẩn hóa, length, `LastWriteUtcTicks`, mode Original, target width, backend và orientation. Đây là correctness boundary: không rút key về path-only; kiểm tra source fingerprint sau decode để không công bố pixel cũ dưới identity mới.

Thumbnail có RAM/disk cache và quota riêng. Hủy waiter không nên làm hỏng tác vụ dùng chung; clear/evict phải vô hiệu hóa kết quả cũ để decode/persist đang chạy không hồi sinh entry đã xóa. Disk cache là tối ưu best-effort: lỗi/hỏng cache phải quay về decode nguồn, không làm mất khả năng mở ảnh.

`SourceBytesCache` là cache LRU byte nguồn theo path/length/mtime, có dedup in-flight và quota 16 GiB. Cache này mặc định tắt. Khi bật, preview, hash, preload và thumbnail JPEG có thể dùng lại byte đã đọc; PNG/WIC thumbnail giữ đường stream fallback tương thích.

### Cài đặt liên quan

| Setting | Mặc định | Hành vi |
|---|---|---|
| `DecoderBackend` | `WicDirect` | Chọn `Wpf`, `WicDirect` hoặc `TurboJpeg`. WIC/TurboJPEG fallback về WPF khi gặp lỗi codec được hỗ trợ. Đổi backend khi đang xem sẽ clear cache liên quan và trình diễn lại ảnh hiện tại. |
| `ScalingQuality` | `HighQuality` | `HighQuality` ưu tiên chất lượng scale; `Linear` ưu tiên độ mượt khi zoom/chuyển khung. Không thay đổi pixel decode hoặc cache identity. |
| `UseSourceBytesCache` | `false` | Bật cache byte nguồn trong RAM. Đây là feature flag; chỉ bật sau khi đo source-open, working set và độ trễ trên folder thật. |

### Đổi mode, resize và cache lifecycle

Cache key gắn với source version, backend và quality/target width. Khi đổi mode, backend, resize hoặc DPI đổi, caller phải tạo key hiện hành; không dùng bitmap của key khác như thể đó là cùng chất lượng. Cache clear/evict và thao tác Move/Delete cần tăng epoch hoặc tương đương, dọn entry liên quan và ngăn in-flight cũ ghi lại cache. Source có thể đổi trong lúc decode nên cần xác nhận fingerprint trước khi cache/present.

## An toàn thao tác file và concurrency

- Delete đưa file vào Recycle Bin, không xóa vĩnh viễn. Journal là nguồn recovery cho thao tác Move/Copy/Delete; fingerprint/revalidation trước retry/undo ngăn tác động nhầm lên file đã thay đổi.
- Cancellation và generation guard ngăn kết quả folder/ảnh cũ cập nhật UI mới. Hủy không nhất thiết dừng được decoder native đã bắt đầu; kết quả cũ vẫn phải bị loại khỏi presentation/cache theo lifecycle.
- File handle đọc ảnh cần cho phép `FileShare.ReadWrite | FileShare.Delete`, để Move/Delete không bị khóa bởi decode đang chạy. Giữ `SequentialScan` cho đọc tuần tự.
- Preload là best-effort: giới hạn worker, kiểm tra memory headroom, ưu tiên ảnh gần vị trí hiện tại, bỏ hoặc hủy việc không còn hữu ích; không đổi responsiveness lấy working set vượt kiểm soát.
- Explorer COM snapshot ảnh hưởng final ordering. Snapshot không khả dụng, không hợp lệ hoặc timeout phải giữ fallback hoạt động và không làm UI treo.

## Giới hạn diễn giải và benchmark

`ReviewMetrics` cung cấp counter/tổng thời gian cho cache, disk hit, source open/read/bytes, decode, queue wait, presentation, stat, session write và decoder fallback. Đây chưa phải trace đầy đủ theo từng stage hay key-to-present histogram. Không dùng metric tổng để khẳng định P95 hoặc “đã đọc đúng một lần”.

```powershell
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-list-profiles
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-all 'C:\duong-dan\folder-anh' 'C:\duong-dan\ket-qua'
```

Khi so sánh, giữ nguyên fixture, build, máy, power/storage state, mode, viewport/DPI, preload và trạng thái Explorer. Tách cold/warm cache, ghi median/P95/max, source opens/bytes, RAM peak, cache hit và cấu hình. Thay một biến mỗi lượt.

## Kiểm tra sau thay đổi

```powershell
.\tools\verify-all.ps1
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

Các lệnh xác nhận contract và artifact; chúng không thay cho runtime benchmark hoặc GUI acceptance.
