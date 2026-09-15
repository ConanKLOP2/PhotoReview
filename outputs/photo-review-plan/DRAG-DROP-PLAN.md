# PhotoReview — Kế hoạch kéo-thả ảnh/thư mục

Đối chiếu runtime 2026-09-15: `DragDropInputService.Parse`, `ImageFileTypes` và Window drag handlers đã có. Đây là design/acceptance plan; cam kết giữ session cũ khi load lỗi, mixed Unicode input và overlapping-load cancellation chưa có test hành vi đầy đủ. Trạng thái hiện hành xem [DRAG-DROP-TASKS.md](DRAG-DROP-TASKS.md).

## 1. Mục tiêu

Cho phép người dùng kéo ảnh hoặc thư mục từ Windows Explorer vào cửa sổ PhotoReview để mở review nhanh, không làm thay đổi dữ liệu nguồn.

## 2. Phạm vi

### Có trong phạm vi

- Kéo một thư mục: mở thư mục và tải danh sách ảnh theo sort hiện tại.
- Kéo một ảnh: mở thư mục cha và chọn đúng ảnh đó.
- Kéo nhiều ảnh: chọn thư mục chung; ảnh đầu tiên hợp lệ được chọn.
- Hiển thị trạng thái kéo hợp lệ/không hợp lệ.
- Bỏ qua file không hỗ trợ và báo kết quả rõ ràng.
- Giữ nguyên cache, preload, Explorer ordering, session và undo hiện tại.
- Không copy, move, rename hoặc xóa file do thao tác kéo-thả.

### Ngoài phạm vi

- Import/copy ảnh vào thư mục dự án.
- Mở nhiều session cùng lúc.
- Thay đổi thuật toán sort hoặc image decoder.
- Kéo-thả từ trình duyệt/web vào ứng dụng.

## 3. Hành vi người dùng

| Input | Hành vi | Kết quả mong đợi |
|---|---|---|
| Một folder | `LoadFolderAsync(folder)` | Mở folder, chọn ảnh đầu tiên hoặc ảnh resume |
| Một ảnh hỗ trợ | `LoadFolderAsync(parent, image)` | Mở parent folder, chọn đúng ảnh |
| Nhiều ảnh cùng parent | Mở parent folder | Chọn ảnh đầu tiên theo thứ tự thả |
| Nhiều ảnh khác parent | Dùng parent của ảnh đầu tiên, cảnh báo | Không scan ngoài folder đã chọn |
| File không hỗ trợ | Từ chối | Không đổi session hiện tại |
| Shortcut `.lnk`/URL | Từ chối ở bản đầu | Không resolve ngoài ý muốn |
| Thả khi đang loading | Hủy hoặc thay thế load trước theo cancellation hiện có | Không race làm hiển thị sai ảnh |

## 4. Thiết kế kỹ thuật

### 4.1. XAML

- Đặt `AllowDrop="True"` trên root window hoặc vùng preview bao phủ toàn bộ content.
- Đăng ký `DragOver` và `Drop` ở root để mọi vùng con dùng chung hành vi.
- Không chặn thao tác zoom, compare click hoặc kéo chọn nội dung hiện có.
- Có thể thêm overlay nhẹ khi `DragOver` hợp lệ, nhưng không bắt buộc cho MVP.

### 4.2. Luồng xử lý

1. Nhận `DataFormats.FileDrop`.
2. Chuẩn hóa path bằng `Path.GetFullPath`.
3. Phân loại folder/file.
4. Với folder, chọn folder đầu tiên hợp lệ.
5. Với file, lọc bằng cùng tiêu chí extension của `ImageFolderService`.
6. Xác định parent folder.
7. Gọi luồng `LoadFolderAsync`; truyền `initialPath` nếu input là ảnh.
8. Ghi log loại input, số path và kết quả, không ghi dữ liệu nhạy cảm ngoài path cần thiết.
9. Hiển thị lỗi thân thiện nếu access denied, path biến mất hoặc không có ảnh hỗ trợ.

### 4.3. Tách trách nhiệm

- `MainWindow`: nhận event và gọi use case.
- Nên tạo `DragDropInputService` hoặc helper thuần để parse/validate input, tránh nhồi logic vào code-behind.
- Tái sử dụng extension/filter hiện có; không tạo danh sách extension thứ hai.
- Tái sử dụng cancellation token và session generation hiện có để chống kết quả load cũ ghi đè load mới.

## 5. Ưu tiên hiệu năng/I/O

- Không đọc metadata hoặc decode ảnh trong `DragOver`.
- `DragOver` chỉ kiểm tra format và path cơ bản.
- Chỉ scan folder một lần thông qua `LoadFolderAsync`.
- Không đọc lại ảnh đầu tiên nếu cache/session đã có thể tái sử dụng.
- Không scan toàn bộ ổ đĩa khi người dùng thả nhiều path khác nhau.
- Giữ chất lượng decode hiện tại; kéo-thả không được chuyển sang thumbnail thấp hơn.

## 6. Kế hoạch task

Xem danh sách chi tiết trong `DRAG-DROP-TASKS.md`. Thứ tự khuyến nghị: DD-01 → DD-03 → DD-04 → DD-05 → DD-06 → DD-07 → DD-08.

## 7. Tiêu chí hoàn thành

- Folder và ảnh thả từ Explorer mở đúng.
- Ảnh thả vào được chọn đúng trong danh sách đã sort.
- Input không hợp lệ không làm mất session hiện tại.
- Không phát sinh thao tác file ngoài ý muốn.
- Test tự động bao phủ parser và các nhánh lỗi chính.
- Manual test trên folder nhỏ, folder lớn, path Unicode, access denied và khi đang loading.
- Build/test/release verification thành công.

## 8. Rủi ro

- Root event bị vùng con nuốt: xử lý tại Window và kiểm tra `Handled`.
- Nhiều path gây hành vi mơ hồ: bản đầu chỉ chọn folder/parent đầu tiên và cảnh báo.
- Load liên tiếp tạo race: bắt buộc dùng cancellation/generation hiện hữu.
- Folder không có quyền đọc: giữ session cũ, chỉ báo lỗi.
- File bị xóa giữa lúc thả và scan: coi như input không còn hợp lệ, không crash.
