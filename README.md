# PhotoReview

Trạng thái source 2026-09-15: `1.0.1` tại commit `5189298`; chi tiết audit function, gate chưa có evidence và thứ tự tối ưu xem [CURRENT-STATE.md](outputs/photo-review-plan/CURRENT-STATE.md) và [FUNCTION-AUDIT-PLAN.md](outputs/photo-review-plan/FUNCTION-AUDIT-PLAN.md).

PhotoReview là ứng dụng Windows WPF tối ưu cho việc duyệt và phân loại ảnh nhanh theo thứ tự đang hiển thị trong File Explorer.

## Điều làm PhotoReview khác biệt

PhotoReview được xây dựng như một công cụ review ảnh tốc độ cao, ưu tiên cảm giác “mở folder là duyệt ngay” thay vì chỉ là trình xem ảnh:

- **Giữ đúng thứ tự Windows Explorer** — đọc thứ tự native từ Explorer, bao gồm Name/Date/Size và chiều ASC/DESC; khi Shell chưa sẵn sàng, ảnh vẫn hiện ngay bằng thứ tự fallback rồi tự đồng bộ lại.
- **Tận dụng RAM để giảm I/O** — cache preview theo LRU, preload ảnh lân cận và mở rộng tới toàn bộ folder khi dung lượng phù hợp; có memory-pressure guard để không làm treo máy.
- **Review không phá hủy dữ liệu** — Delete đưa file vào Recycle Bin thay vì xóa vĩnh viễn; Undo có thể khôi phục lại file đúng vị trí ban đầu. Move/Copy/Delete đều có journal, fingerprint và recovery để tránh mất dữ liệu khi gián đoạn.
- **Tập trung tối đa vào vùng ảnh** — toolbar gọn, tên folder nằm trên title bar, fullscreen/zoom/Fit và chuyển ảnh bằng bàn phím giúp giảm thao tác thừa.
- **Compare và xử lý hàng loạt an toàn** — chọn cặp ảnh để so sánh, kiểm tra hash/kích thước tùy chọn, duplicate batch luôn có màn hình dry-run trước khi thực hiện.
- **Làm việc cục bộ, minh bạch** — không upload ảnh hoặc đường dẫn; có diagnostics nội bộ, lưu session/vị trí cửa sổ và khôi phục trạng thái làm việc lần trước.

## Tính năng

- Đọc thứ tự item native từ cửa sổ Windows Explorer qua `IFolderView2`, gồm Name/Date/Size và hướng sắp xếp khi Shell cung cấp.
- Hiển thị ảnh fallback nhanh, sau đó cập nhật theo thứ tự Explorer mà không đọc lại ảnh đã cache.
- Cache preview trong RAM có giới hạn, preload ảnh lân cận và bảo vệ theo áp lực bộ nhớ.
- Chế độ tải Fast/Preview/Original, zoom, Fit và fullscreen.
- Workflow review: mũi tên duyệt ảnh, Enter Move sang folder 2, Delete vào Recycle Bin, Space bỏ qua, Ctrl+Z Undo thao tác gần nhất (Move/Delete).
- Kéo-thả ảnh hoặc folder từ Windows Explorer vào cửa sổ để mở nhanh; khi kéo ảnh, ứng dụng mở folder cha và chọn đúng ảnh đó.
- Compare cặp ảnh, tùy chọn hash/kích thước, xử lý duplicate theo batch có màn hình xác nhận.
- Journal an toàn cho Move/Copy/Recycle, recovery và retry có kiểm tra fingerprint.
- Lưu session, vị trí cửa sổ và cấu hình người dùng.
- Diagnostics nội bộ; không gửi path hoặc dữ liệu ảnh ra ngoài.

## Yêu cầu

- Windows 10/11 x64.
- .NET 10 Windows Desktop Runtime nếu dùng bản framework-dependent.
- Bản self-contained đã kèm runtime .NET.

## Build và chạy

Build solution:

```powershell
dotnet build PhotoReview.slnx -c Release
dotnet run --project PhotoReview.Tests -c Release
```

Publish artifact chính vào thư mục mặc định của SDK:

```powershell
dotnet publish PhotoReview.App\PhotoReview.App.csproj -c Release --self-contained false -o PhotoReview.App/bin/Release/net10.0-windows/publish
```

Executable nằm tại:

```text
PhotoReview.App\bin\Release\net10.0-windows\publish\PhotoReview.App.exe
```

Publish bản self-contained x64:

```powershell
dotnet publish PhotoReview.App\PhotoReview.App.csproj -c Release -r win-x64 --self-contained true -o outputs\release\PhotoReview-self-contained
```

Mỗi build tự ghi build stamp UTC vào Settings, ví dụ `1.0.1+build.20260914.164707`.

## File association

Đăng ký PhotoReview trong Open With cho `.jpg`, `.jpeg` và `.png`:

```powershell
.\outputs\install-photo-review-association.ps1 -ExePath "C:\duong-dan\PhotoReview.App.exe"
```

Gỡ đăng ký:

```powershell
.\outputs\uninstall-photo-review-association.ps1
```

## Giới hạn native Explorer order

PhotoReview chỉ dùng snapshot native khi Shell trả đủ item regular-file thuộc đúng folder. Nếu Explorer đóng, đang tải, trả item thiếu/virtual hoặc vượt timeout 2 giây, ứng dụng fallback về sort đã cấu hình. Grouped view và các sort property tùy biến còn phụ thuộc API Shell của phiên bản Windows.

## Kiểm thử

Các gate chính:

`verify-all.ps1` hiện mặc định kiểm tra artifact trong `outputs/release/PhotoReview-framework-dependent`. Khi dùng thư mục publish bắt buộc ở trên, truyền `-ReleaseDirectory PhotoReview.App/bin/Release/net10.0-windows/publish`; self-contained verification cần artifact riêng tại `outputs/release/PhotoReview-self-contained`.

```powershell
.\tools\verify-all.ps1 -RequireSelfContained
```

Probe thứ tự native của một folder Explorer đang mở:

```powershell
dotnet run --project PhotoReview.Tests -c Release -- --explorer-probe "C:\duong-dan\folder-anh"
```

## Cấu trúc

- `PhotoReview.App/` — ứng dụng WPF và các service cache, journal, Shell interop.
- `PhotoReview.Tests/` — contract/persistence/safety tests và native Explorer probe.
- `tools/` — build, publish verification, smoke test và fault-injection test.
- `outputs/photo-review-plan/` — product, architecture, performance và audit plans.

## Trạng thái

Release hiện tại: `1.0.1`. Native Name DESC đã được kiểm thử trực tiếp trên Explorer; matrix Date/Size/Group và GUI acceptance đầy đủ vẫn là các hạng mục mở trong kế hoạch.
