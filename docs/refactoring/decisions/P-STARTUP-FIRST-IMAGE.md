---
id: P-STARTUP-FIRST-IMAGE
order: 161
summary: |-
  Phản hồi 2026-10-09 (mở ảnh từ Explorer chậm, "từ rev 240 nặng hơn", khung trắng): đo 2026-10-10 không thấy hồi quy ở v2.0.240; cửa sổ giờ được đặt đúng placement trước khi hiện, cloak tới khung đầu (không còn khung trắng 1200x800) và decode ảnh khởi chạy ngay khi cửa sổ hiện (ảnh đầu -95..-120 ms); CÒN MỞ: cho Explorer chạy bản ReadyToRun (publish) để bớt thêm ~150-250 ms.
---

# P-STARTUP-FIRST-IMAGE - tốc độ mở ảnh đầu từ Explorer và khung trắng lúc khởi động

Số liệu đầy đủ: [`../perf/2026-10-10-startup-first-image.md`](../perf/2026-10-10-startup-first-image.md).

## Đã làm (nhánh `perf/startup-first-image`)

1. **"Từ rev 240 nặng hơn": không xác nhận.** 6 phiên bản (v2.0.200/239/240/260/300/380) x 3 điều kiện, đo xen kẽ: không có bước nhảy ở v240, decode JPEG như nhau;
   bản hiện tại nhanh nhất ở điều kiện giống máy người dùng (cache đĩa trống). Không cần bisect. Giả thuyết: từ 2026-10-08 placement đã lưu nằm trên màn hình thứ hai
   đã rút, nên app mở cửa sổ 1200x800 trắng thay vì toàn màn hình.
2. **Khung trắng / cửa sổ ½ màn hình:** placement được áp dụng trong `SourceInitialized` (trước khi hiện) và cửa sổ bị `DWMWA_CLOAK` tới tick render thứ 2 sau khi hiện
   (dự phòng `ContentRendered`, hẹn giờ 3 s). Placement trên màn hình đã rút + đã lưu Maximized -> vẫn mở Maximized (trước: 1200x800). Theme không đổi.
3. **Ảnh đầu sớm hơn:** Show() ngắn đi ~120 ms (không còn maximize + layout lần 2 sau khi hiện) và decode file khởi chạy bắt đầu lúc cửa sổ hiện
   (`MainViewModel.PrewarmInitialImage`, presenter nhập vào decode đang chạy: `Lookup = inflight` 100 %). Ảnh đầu: -120 ms (Name + Explorer), -95 ms (Default), cold.
4. **Không đổi (không có bằng chứng):** Prefetch thứ tự Explorer không nằm trên đường găng (snapshot luôn có trước catalog); bỏ Prefetch ở sort Default/Name A-Z/Z-A
   đo không lợi gì -> giữ nguyên. Không đổi thứ tự cuối của danh sách.

## Còn mở: Explorer chạy bản nào (ReadyToRun)

**Hiện trạng:** liên kết `.jpg` (HKCU `Applications\PhotoReview.App.exe`) chạy `src\PhotoReview.App\bin\Release\net10.0-windows\PhotoReview.App.exe` = bản **build**
(JIT toàn bộ code của app). `PublishReadyToRun` chỉ áp dụng cho `dotnet publish` (thư mục `...\publish`). Đo xen kẽ, cùng mã: bản publish (R2R) cho ảnh đầu sớm hơn
~150-250 ms (launch -> openPath -140 ms; tổng 1657 -> 1413 ms ở nhánh này). `dotnet publish` tăng dần sau build ~8 s.

| | Cách | Ưu | Nhược |
|---|---|---|---|
| A | Giữ nguyên | Không việc gì | Mất ~150-250 ms mỗi lần mở từ Explorer |
| B | Người dùng đổi liên kết `.jpg`/menu Explorer sang `...\net10.0-windows\publish\PhotoReview.App.exe`; quy tắc build cục bộ (`AGENTS.local.md`) chạy thêm `dotnet publish` sau mỗi build | Đúng bản đã phát hành (CI cũng publish R2R); không đổi csproj | Người dùng phải đổi liên kết một lần; mỗi lần build cục bộ thêm ~8 s; bản build và bản publish có thể lệch nếu quên publish |
| C | `AGENTS.local.md` publish thẳng vào `bin\Release\net10.0-windows` (đường dẫn liên kết hiện tại) | Không đổi liên kết; Explorer luôn chạy bản R2R | Trộn output build/publish trong một thư mục (build sau đó ghi đè DLL không R2R); khó hiểu khi debug |
| D | Bật R2R cho cả `dotnet build -c Release` (MSBuild tự gọi bước ReadyToRun sau build) | Mọi bản Release đều R2R, không đổi thói quen | Đổi csproj cho mọi người, build/test chậm hơn mỗi lần, MSBuild tuỳ biến dễ vỡ khi nâng SDK; test chạy trên DLL R2R |

**Đề xuất: B** - đúng bản phát hành, rủi ro thấp, chỉ là việc cấu hình trên máy này. Sau khi người dùng chọn: (B/C) đề xuất câu lệnh cho `AGENTS.local.md`
(người dùng tự sửa file riêng đó) và cách đổi liên kết; (D) một PR riêng đổi csproj + đo lại thời gian build/test.

## Cần người dùng xem bằng mắt

Đóng hẳn PhotoReview, double-click một ảnh trong Explorer (thư mục lớn như F4), lặp 3-5 lần, cả khi có và không có màn hình ngoài:
không còn khung trắng / cửa sổ nhỏ chớp lên; cửa sổ hiện thẳng ở vị trí và trạng thái lần đóng trước (toàn màn hình nếu lần trước maximize), nền tối, rồi ảnh hiện.
Kiểm thêm: F11 vào/ra toàn màn hình, kéo cửa sổ sang màn hình khác rồi đóng/mở lại (vị trí được nhớ).
