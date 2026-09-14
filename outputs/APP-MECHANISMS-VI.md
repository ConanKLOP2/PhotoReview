# Photo Review — Cơ chế hoạt động và checklist preview

Tài liệu này mô tả code hiện tại để người dùng tự preview, góp ý và chỉnh sửa app.

## 1. Khởi động và giao diện

- Mở bằng ảnh sẽ chọn đúng ảnh đó trong folder; mở không tham số thì bấm **Mở folder**.
- App đọc config tại `%LOCALAPPDATA%\PhotoReview\config.json` và khóa folder để tránh mở trùng phiên.
- Cửa sổ có title bar Windows, mở maximized; vùng preview kéo đầy phần client.
- Overlay phía trên ảnh chỉ giữ nút icon mở folder, Settings và nút công cụ phụ `⋮`; công cụ ít dùng nằm trong popup. Status nằm overlay phía dưới; dòng hướng dẫn phím tắt được ẩn để tối đa hóa diện tích ảnh.
- Folder dưới 100 ảnh sort theo Settings nhưng không đọc EXIF; folder từ 100 ảnh dùng thứ tự Windows Explorer hoặc fallback tên.
- `F11` chuyển giữa cửa sổ có khung và fullscreen không viền.

## 2. Điều khiển

| Tác động | Kết quả |
|---|---|
| Mũi tên trái/lên | Ảnh trước |
| Mũi tên phải/xuống | Ảnh kế tiếp |
| Click chuột trái trong viewer | Ảnh trước |
| Click chuột phải trong viewer | Ảnh kế tiếp |
| Space | Bỏ qua, file vẫn ở nguồn |
| Enter | Move vào folder 2 |
| Delete | Gửi vào Windows Recycle Bin |
| Ctrl+Z | Undo Move gần nhất nếu file chưa bị sửa |
| F11 | Vào/đổi fullscreen |
| Esc | Thoát fullscreen về cửa sổ có khung |

File còn ở folder nguồn mặc nhiên là loại 1; app không cần tạo folder hay marker cho loại này.

## 3. Hai chế độ tải ảnh

### Fast

Decode trực tiếp một adaptive preview cho ảnh hiện tại, không chờ thumbnail. Đây là chế độ nhanh nhất.

### Preview

Decode thumbnail tối đa 800px để hiện trước, sau đó decode adaptive preview rõ hơn. Đây là chế độ mặc định hiện tại và có thể đổi trong Settings.

Preload ảnh kế tiếp hoạt động ở cả hai chế độ.

## 4. Adaptive preview và Fit

Target decode được tính theo vùng xem và DPI:

```text
targetWidth = viewportWidth × dpiScale × qualityMultiplier
```

Giá trị bị giới hạn trong khoảng 1200–4000px. Cache key bao gồm đường dẫn, kích thước file, thời gian sửa đổi và target width.

- `Fit`: ảnh lớn thu nhỏ vừa vùng xem, ảnh nhỏ không phóng đại.
- `100%`, `200%`, `400%`: chọn trong Settings.
- `+/-`, `Z`, Ctrl+mouse wheel: zoom tạm thời.

Dải đen do tỉ lệ ảnh khác tỉ lệ vùng xem là bình thường; khung preview vẫn phải kéo đầy cửa sổ.

## 5. Cache và preload

- RAM cache dùng bounded LRU, giới hạn mặc định khoảng 1GB cho preview.
- Thumbnail cache có LRU riêng, thumbnail tối đa 800px.
- Disk cache ghi qua file tạm, flush rồi rename atomic; cache hỏng được decode lại.
- Preload ưu tiên ảnh `+1…+8`, sau đó `-1/-2`, tối đa 2 decode đồng thời.
- Khi đổi ảnh nhanh, hàng preload cũ được hủy.

## 6. File operation

### Enter — Move loại 2

App tạo folder đích, không overwrite, ghi journal `Prepared`, Move file, kiểm tra kích thước và ghi `Committed`. File được loại khỏi danh sách hiện tại.

### Delete — loại 3

App ghi journal `Prepared`, gửi file vào Recycle Bin, rồi ghi `Committed`. Không có Undo tự động cho Recycle Bin.

### Ctrl+Z

Chỉ Move được Undo nếu nguồn không tồn tại, đích còn tồn tại và fingerprint cơ bản trong journal vẫn khớp. Nếu file đích đã bị sửa, app không tự động Undo.

## 7. Persistence, journal và Settings

- Session lưu folder, ảnh hiện tại và danh sách bỏ qua.
- Journal nằm tại `%LOCALAPPDATA%\PhotoReview\Data\operations.jsonl`.
- Settings hiện có folder 2, shortcut, Fit/100/200/400 và Fast/Preview/Original; chuyển loại 2 được quản lý bởi Action JSON, không còn ô cấu hình trùng.
- Settings kiểm tra shortcut trùng, có khôi phục mặc định và lưu config qua file tạm.
- Pending journal có thể được phát hiện; màn hình recovery chi tiết vẫn là hạng mục cần mở rộng.

## 8. Checklist preview toàn app

- [ ] Mở EXE self-contained và kiểm tra cửa sổ có khung.
- [ ] Kiểm tra vùng preview sát hai mép client, không có margin layout thừa.
- [ ] Resize cửa sổ; Fit cập nhật và ảnh nhỏ không bị phóng đại.
- [ ] Thử trái/lên/phải/xuống và click chuột trái/phải.
- [ ] Thử Fast, Preview và Original trong Settings.
- [ ] Kiểm tra thumbnail xuất hiện trước preview trong Preview mode; mở folder không chờ decode EXIF toàn bộ.
- [ ] Đổi Fit/100/200/400 và kiểm tra zoom.
- [ ] Thử Enter, Delete, conflict tên file và Ctrl+Z.
- [ ] Đóng/mở lại app để kiểm tra resume.
- [ ] Thử folder có ảnh JPG/PNG lớn, Unicode, ảnh hỏng và tên dài.
- [ ] Thử DPI 100/150/200% hoặc màn hình khác.

## 9. Đề xuất tối ưu tốc độ

Ưu tiên cao:

1. Dùng `Fast` nếu mục tiêu là chuyển ảnh liên tục.
2. Giữ preload 1–2 ảnh nếu ổ đĩa chậm; tăng lên 8 chỉ khi benchmark chứng minh có lợi.
3. Dùng JPEG cache cho JPG, PNG chỉ cho ảnh có alpha.
4. Không ghi disk cache đồng bộ trên đường hiển thị ảnh.
5. Chỉ decode lại khi kích thước viewport thay đổi đáng kể, ví dụ trên 15%.
6. Thêm quota/LRU cho disk cache.

## 10. Đề xuất tối ưu chi phí

- Giữ toàn bộ xử lý local; không cần server, cloud, API hay AI cho workflow cơ bản.
- Không upload ảnh và không dùng telemetry mặc định.
- Dùng .NET/WPF/WIC có sẵn trước khi thêm thư viện ngoài.
- Benchmark trước khi mua thư viện decoder hoặc phần cứng.
- Giới hạn disk cache để không lãng phí dung lượng.

## 11. Giới hạn hiện tại

- Chưa có benchmark chính thức trên bộ 242 ảnh.
- Test contract chưa thay thế hoàn toàn UI automation trên máy thật.
- Recovery pending chưa có màn hình xử lý chi tiết.
- Shortcut modifier Ctrl/Alt/Shift và thumbnail strip là phần mở rộng.

## 12. Lệnh verification

```powershell
$env:DOTNET_CLI_HOME = (Join-Path (Get-Location) 'work\dotnet-home')
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\verify-all.ps1 -Configuration Release -RequireSelfContained
```

Bản chạy trực tiếp:

```text
outputs\release\PhotoReview-self-contained\PhotoReview.App.exe
```
