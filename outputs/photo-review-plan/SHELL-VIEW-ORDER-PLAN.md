# Kế hoạch lấy thứ tự folder theo Windows Explorer

Cập nhật: 2026-09-14

## Mục tiêu

Khi người dùng mở một folder đang xem trong Windows Explorer, PhotoReview cố gắng dùng cùng thứ tự item và hướng tăng/giảm mà Shell cung cấp. Với folder không có Explorer view state hoặc API không lấy được state, app giữ fallback an toàn hiện tại: sort tên kiểu Explorer.

## Phản biện và giới hạn

1. Đường dẫn folder không chứa thông tin view đang mở; phải truy vấn Windows Shell/Explorer state.
2. Explorer có thể không chạy, có nhiều cửa sổ cùng folder, hoặc view state chưa được lưu; adapter phải trả về `Unavailable`, không đoán.
3. Không được dùng COM trên UI thread; mọi Shell query phải chạy nền, có timeout/cancellation.
4. Không được enumerate/decode ảnh hai lần; adapter trả về thứ tự path đã xác thực, scanner dùng lại snapshot đó.
5. Shell item có thể là virtual item, link, permission-denied hoặc file đã biến mất; chỉ chấp nhận regular local files còn tồn tại và log lý do loại.
6. Windows Photos có integration nội bộ không công khai đầy đủ; không cam kết parity tuyệt đối nếu Explorer không expose view state. Fallback phải luôn hoạt động.
7. Thứ tự Explorer có thể thay đổi khi người dùng đổi view; mỗi lần mở folder lấy snapshot mới, không cache vô thời hạn.
8. Không truyền path đầy đủ ra ngoài máy; log nội bộ có thể ghi path để debug nhưng không telemetry/network.

## Thiết kế chốt

- `ExplorerOrderService`: abstraction `TryGetOrderAsync(folder, cancellationToken)`.
- `ShellExplorerOrderService`: Windows implementation, isolated interop/COM and HRESULT handling.
- `FolderScanService`: one snapshot, supported-extension filter, duplicate/path validation.
- `MainWindow`: request order asynchronously; show first item from fast fallback immediately; replace list only if Shell order result is valid and current folder/generation unchanged.
- Fallback: `ImageSortService.Sort(snapshot, "Name")`.
- Threshold 100: metadata/EXIF sorting only below 100 files; 100+ uses Explorer/Shell order or name fallback.

## Checklist triển khai

- [x] T01: tạo provider order độc lập.
- [x] T02: implement Shell provider với timeout, COM cleanup, exception logging.
- [x] T03: integrate snapshot/fallback vào LoadFolderAsync.
- [x] T04: không decode EXIF trước ảnh đầu tiên; chỉ sort metadata dưới 100 file.
- [ ] T05: bổ sung test provider giả lập, fallback, stale result và 100-file boundary.
- [x] T06: bổ sung log fields cho provider/result/fallback.
- [x] T07: cập nhật tài liệu, build Release và test.

## Tiêu chí chấp nhận

- Folder lớn hiển thị ảnh đầu tiên trước Shell/metadata work hoàn tất.
- Nếu Shell provider trả order hợp lệ, danh sách dùng đúng order đó.
- Nếu provider lỗi/timeout/không có Explorer state, app vẫn mở folder bằng name fallback.
- Không có COM object leak, UI freeze hoặc exception không ghi log.
- Test tự động bao phủ unavailable, timeout, invalid order, stale generation và boundary 99/100.
