# Photo Review — Cơ chế hoạt động và checklist preview

Cập nhật 2026-09-15 theo source master tại commit c87d14a. Tài liệu này mô tả cơ chế đang có và phân biệt phần đã có bằng chứng với phần còn cần nghiệm thu trên Windows.

## 1. Khởi động và luồng mở folder

- App là WPF trên .NET 10, bản project 1.0.1; source chính ở PhotoReview.App.
- Mở bằng command line, nút chọn folder hoặc kéo-thả. Scan top-level dùng ImageFileTypes làm registry extension chung cho scan, drag-drop và sibling-folder.
- App đọc config/session tại %LOCALAPPDATA%\PhotoReview, khóa folder và lưu folder, ảnh hiện tại, danh sách bỏ qua.
- App luôn thử lấy thứ tự native từ Windows Explorer cho mọi folder qua STA worker/IFolderView2. Ảnh được hiển thị trước theo sort cấu hình; snapshot hợp lệ mới thay thế fallback.
- Snapshot thiếu, timeout, duplicate, path ngoài folder hoặc catalog thay đổi thì giữ thứ tự fallback. Date/Size/grouped Explorer parity vẫn cần kiểm thử thêm.
- Load folder nhận cancellation/generation token để load cũ không cập nhật UI sau khi chuyển folder.

## 2. Giao diện và điều khiển

| Tác động | Kết quả |
|---|---|
| Mũi tên trái/lên | Ảnh trước |
| Mũi tên phải/xuống | Ảnh kế tiếp |
| Click preview chính | Không chuyển ảnh; vùng Compare trái/phải dùng để chọn ảnh thao tác |
| Space | Bỏ qua, lưu ngay vào session; file vẫn ở nguồn |
| Action shortcut | Thực thi Move/Copy/Recycle theo profile đang cấu hình |
| Ctrl+Z | Undo Move gần nhất nếu path, size và LastWriteUtc còn khớp |
| F11 / Esc | Vào fullscreen / thoát về cửa sổ có khung |

Action profile có tên, shortcut, operation và destination; không hợp lệ thì bị từ chối, không tự động đổi thành Move. Chord modifier đầy đủ và command registry độc lập chưa hoàn tất.

## 3. Preview, adaptive decode và Fit

Fast decode adaptive preview không chờ thumbnail. Preview decode thumbnail tối đa khoảng 800px rồi decode adaptive preview rõ hơn; đây là mode mặc định. Original ưu tiên ảnh gốc theo pipeline hiện tại.

Target decode tính theo viewport, DPI và quality multiplier, giới hạn 1200–4000px. Disk preview key có path, length, last-write và target width; RAM preview và các loading task trong MainWindow hiện chủ yếu keyed theo path, nên resize/mode chưa được biểu diễn đầy đủ trong RAM key.

- Fit/100%/200%/400% là chế độ hiển thị/zoom; target decode vẫn tính động theo viewport và DPI.
- Dải đen khi tỷ lệ ảnh khác vùng xem là bình thường; viewer vẫn kéo đầy client.

## 4. Cache và preload

- Preview RAM cache chính có giới hạn 16 GB theo policy hiện tại. ThumbnailCache có RAM quota mặc định 256 MB và disk quota mặc định 1 GB.
- ThumbnailCache hỗ trợ ghi PNG atomically qua file tạm, flush và rename; instance hiện dùng trong MainWindow tắt persist thumbnail mới. Preview adaptive chỉ đọc disk cache nếu có và giữ bitmap trong RAM.
- Generation/cancellation guard ngăn waiter bị hủy làm hỏng công việc dùng chung và ngăn clear/dispose nạp kết quả cũ vào cache. Decode WPF đang chạy có thể vẫn hoàn thành background vì cancellation không ngắt được mọi bước decode đồng bộ.
- Preload ưu tiên ảnh lân cận và có memory-pressure guard khi preload rộng; lợi ích thực tế phải đo bằng benchmark key-to-visible-frame.
- Hash kiểm tra lại length/last-write sau khi đọc; đây là fingerprint cơ bản, không phải checksum bất biến.

## 5. File operation, journal và recovery

- Move/Copy tạo folder đích khi cần, không overwrite, ghi JSONL Prepared, thực thi, kiểm tra destination và ghi Committed hoặc Failed.
- Delete ghi journal loại Recycle và gửi file vào Windows Recycle Bin. Ctrl+Z chỉ gọi Undo Move; menu context có thể xử lý Move hoặc restore Recycle Bin khi thao tác cuối còn hợp lệ.
- Recovery UI hiển thị pending/failed và hỗ trợ retry có kiểm soát cho Move/Copy theo fingerprint/no-overwrite policy; không tự động replay pending khi khởi động.
- Reconcile hiện chủ yếu dựa trên source/destination path và size, có kiểm tra LastWriteUtc ở tuyến Undo; chưa có identity/hash mạnh. Dòng JSONL lỗi hoặc torn record cuối hiện bị bỏ qua im lặng.

## 6. Compare, sort và metadata

- Compare bật hash/size độc lập; pairing giới hạn trong folder đang chọn và cùng extension.
- Name sort dùng Windows logical sort khi API khả dụng, fallback natural sort xử lý đúng dãy số dài hơn 12 chữ số.
- Sort orientation có thể đọc metadata từng file; folder lớn có thể mở chậm. Metadata cache/background scan và benchmark còn mở.

## 7. Persistence và kiến trúc

- Session/config là JSON; operation journal là JSONL tại %LOCALAPPDATA%\PhotoReview\Data\operations.jsonl và được đọc incremental bằng StreamReader.
- MainWindow.xaml.cs vẫn chứa scan, navigation, preview orchestration, compare, batch và một phần mutation. Tách service là backlog audit.
- Không có SQLite, folder watcher, installer hoặc cloud/API bắt buộc trong workflow hiện tại.

## 8. Checklist preview trên Windows

- [ ] Mở PhotoReview.App/bin/Release/net10.0-windows/publish/PhotoReview.App.exe; kiểm tra cửa sổ, resize, Fit và ảnh nhỏ.
- [ ] Thử navigation, click vùng Compare trái/phải, Space, action Enter/F3/F4/F5, Delete, Ctrl+Z, Fast/Preview/Original, zoom, F11/Esc và DPI.
- [ ] Thử Move, Copy, Recycle, conflict tên, Recovery pending/failed/retry và resume sau khi mở lại.
- [ ] Thử JPG/PNG lớn, Unicode, ảnh hỏng, tên dài, folder rỗng và Explorer ordering dưới/trên 100 ảnh.
- [ ] Kiểm tra Explorer timeout, snapshot thiếu/duplicate/out-of-folder, rapid navigation và đổi folder liên tục.

Contract test/build xanh không thay thế GUI automation, Explorer matrix hoặc fault-injection.

## 9. Giới hạn và việc cần đo

1. Chưa có benchmark chuẩn cho key-to-visible-frame, P50/P95, source reads, decoded RAM/GC và preload contention.
2. Contract tests thiên về source/behavior contract; chưa thay thế GUI automation.
3. Recovery crash boundary, torn journal, cùng-size destination và filesystem fault-injection chưa được chứng minh đầy đủ.
4. Native Explorer Date/Size/grouped parity, COM timeout worker, DPI/focus/accessibility và MainWindow decomposition còn mở.

## 10. Lệnh verification bắt buộc

```powershell
dotnet run --project PhotoReview.Tests -c Release
dotnet publish PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o PhotoReview.App/bin/Release/net10.0-windows/publish
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\verify-release.ps1 -ReleaseDirectory 'PhotoReview.App/bin/Release/net10.0-windows/publish'
```

verify-all.ps1 là tiện ích lịch sử có default artifact khác; không dùng thay cho publish đúng path theo AGENTS.md.
