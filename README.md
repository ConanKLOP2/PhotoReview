# PhotoReview

PhotoReview là ứng dụng Windows WPF để duyệt, so sánh và phân loại ảnh theo thứ tự Explorer. Ứng dụng ưu tiên hiển thị ảnh nhanh, giữ thao tác file có thể phục hồi và xử lý dữ liệu cục bộ.

## Điểm chính

- Đọc thứ tự file từ Windows Explorer khi snapshot native hợp lệ; dùng thứ tự fallback trong lúc chờ hoặc khi Shell không sẵn sàng.
- Có chế độ Fast, Preview và Original; cache ảnh trong RAM/đĩa có giới hạn, preload có kiểm tra áp lực bộ nhớ.
- Delete chuyển file vào Recycle Bin; Move/Copy/Delete được ghi journal để hỗ trợ Undo và recovery.
- Hỗ trợ Compare, kiểm tra hash/kích thước tùy chọn, batch duplicate có bước xác nhận, zoom/Fit/fullscreen và phím tắt.
- Ảnh và đường dẫn được xử lý cục bộ; diagnostics nội bộ chỉ bật theo cấu hình.

Xem [cơ chế load ảnh, bất biến an toàn và hướng dẫn benchmark](outputs/APP-MECHANISMS-VI.md) trước khi sửa pipeline hoặc diễn giải kết quả hiệu năng.

## Yêu cầu

- Windows 10/11 x64.
- .NET 10 Windows Desktop Runtime cho bản framework-dependent; bản self-contained kèm runtime.

## Build, test và publish

```powershell
dotnet build PhotoReview.slnx -c Release
.\tools\verify-all.ps1
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

Artifact framework-dependent nằm tại `src/PhotoReview.App/bin/Release/net10.0-windows/publish`. Verification cho self-contained hoặc smoke/fault-injection dùng scripts và đường dẫn riêng trong `tools/`; một lần build/test thành công không thay thế benchmark hoặc GUI acceptance.

## File association (tùy chọn)

Đăng ký Open With cho `.jpg`, `.jpeg`, `.png`:

```powershell
.\outputs\install-photo-review-association.ps1 -ExePath 'C:\duong-dan\PhotoReview.App.exe'
```

Gỡ đăng ký:

```powershell
.\outputs\uninstall-photo-review-association.ps1
```

## Giới hạn cần biết

Thứ tự Explorer phụ thuộc cửa sổ/folder và snapshot Shell hợp lệ; fallback vẫn được dùng nếu Explorer chưa sẵn sàng hoặc snapshot lỗi/timeout. Contract tests không chứng minh GUI behavior, cảm nhận first-image latency hay P95; các kết luận đó cần phép đo runtime có kiểm soát.
