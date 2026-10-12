---
id: NOWPF-UNCONFIRMED-DECISIONS-20261010
order: 610
summary: |-
  Các quyết định chủ dự án còn mở mà agent ĐÃ làm theo khuyến nghị (người dùng cho phép 2026-10-10, "chưa xác nhận"): NE-3/4/6/7/9, khởi động, B-06, C-04. Người dùng xác nhận hoặc đảo lại từng mục; đảo lại thì ghi cách hoàn tác.
---

# Quyết định đã làm theo khuyến nghị, CHƯA được người dùng xác nhận

Người dùng (2026-10-10): "Các quyết định nào cần tôi quyết định cứ làm theo đề xuất, rồi đánh dấu là tôi chưa xác nhận để tôi xác nhận lại sau".
Mọi mục dưới đây có trạng thái **CHƯA XÁC NHẬN**. Agent làm theo khuyến nghị; mỗi mục ghi cách hoàn tác. Người dùng xác nhận
bằng cách đổi trạng thái thành ĐÃ XÁC NHẬN (hoặc chọn phương án khác).

| Mã | Quyết định đã áp dụng (= khuyến nghị) | Phương án còn lại | Hoàn tác | Trạng thái |
|---|---|---|---|---|
| NE-3 | Thanh cuộn bản Win32: (a) tự vẽ, tương đương bản trước | (b) thanh cuộn ẩn kiểu overlay | Đổi ở WP thanh cuộn, không ảnh hưởng dữ liệu | CHƯA XÁC NHẬN |
| NE-4 | Menu chuột phải: (a) menu native + dark qua uxtheme | (b) tự vẽ | Chỉ lớp menu Shell | CHƯA XÁC NHẬN |
| NE-6 | Single-instance beta: (a) prefix pipe riêng cho bản Win32, hợp nhất ở P6 | (b) dùng chung pipe | Đổi một hằng prefix | CHƯA XÁC NHẬN |
| NE-7 | (a) đóng băng tính năng UI mới trên bản WPF từ đầu đợt 3 | (b) song song hai bản | Mở lại tính năng WPF | CHƯA XÁC NHẬN |
| NE-9 | (a) D2D cubic/linear; G-PIX theo PSNR + xem bằng mắt. ĐÃ ĐO (WP-28): ảnh chụp Fit 40,5-44,5 dB, zoom khác 100 % 44-60 dB (ngưỡng 32/28); mẫu vạch 1-2 px 21,8-26,8 dB vì WPF Fant răng cưa ở 2:1 | (b) thu nhỏ CPU bằng WIC Fant mỗi bước zoom (số đo cho thấy không cần) | Đổi bộ lọc trong WP-15 | CHƯA XÁC NHẬN |
| WP04-JPEG | Cache JPEG qua WIC chậm hơn +17-30 % (cầu WPF thêm 2 bản chép khung, ~+4 ms/preview nền): (a) chấp nhận tới WP-06, WP-06 bắt buộc tiêm codec | (b) lối tắt băng-dải, cần mở rộng C-02 đóng băng | Không đổi mặc định (codec WPF mặc định); đổi ở WP-06 | CHƯA XÁC NHẬN |
| STARTUP-1 | Bản .jpg dùng publish R2R 15 MB (người dùng ĐÃ chọn, xác nhận) | 174 MB self-contained + composite | Đổi khoá registry | ĐÃ XÁC NHẬN |
| STARTUP-2 | ĐÃ ĐO 2026-10-11, KHÔNG làm: bỏ hẳn toolbar/ContextMenu/overlay khỏi XAML chỉ bớt 0-17 ms (nhiễu); InitializeComponent ~200 ms là chi phí khởi tạo lạnh chung của WPF (BAML, ResourceDictionary, theme, binding). Đòn bẩy thật còn lại: self-contained + R2R composite (-140..-160 ms, 174 MB) hoặc bỏ XAML chrome ở bản NoWpf | giữ nguyên | Không cần (chưa làm) | CHƯA XÁC NHẬN |
| B-06 | Thêm hậu kiểm "nguồn đã biến mất" cho Recycle đơn như bản nhóm (fail + không commit nếu file còn) | giữ nguyên | Revert PR | CHƯA XÁC NHẬN |
| C-04 | Save sau khi Load ConfigVersion mới hơn: sao lưu `config.json.newer-<ts>` trước khi ghi v3 | giữ nguyên (mất trường lạ) | Revert PR | CHƯA XÁC NHẬN |
| WP05-SCALE | Fine-scale dùng WIC Fant (người dùng ĐÃ chọn b, xác nhận) | PixelAreaResampler tự viết | WP-06 | ĐÃ XÁC NHẬN |
| WP20-VISIBLE | Cửa sổ Win32 chỉ hiện SAU khi giữ khoá instance (không nháy cửa sổ ở bản thứ hai): thấy được ~306 ms, thẻ ghi <= 150 ms | (b) dựng+present canvas trong lúc đọc config, bỏ cloak sau khoá (~-55 ms); (c) bỏ cloak trước khoá (~150 ms, bản thứ hai nháy cửa sổ tối) | Đổi thứ tự trong ShellStartup.StartupCoreAsync | CHƯA XÁC NHẬN |
| WP23-HOVER | Hover/nhấn nút toolbar bản Win32 dùng `Dark.ButtonHover`/`Dark.ButtonPressed` (đồng bộ DarkControls) | (b) giống bản WPF: chrome Aero2 mặc định (xanh nhạt `#BEE6FD` khi hover trên nền tối, vì `MainWindow.xaml` không merge DarkControls) | Đổi hai màu trong `ButtonElement.RenderCore` | CHƯA XÁC NHẬN |
| WP23-TOOLS | Nút ⋮ mở menu Tools native (C-12, theo thẻ + NE-4) thay `Popup` sáu nút của XAML | (b) vẽ popup sáu nút bằng overlay | Thay `ShellOverlay.ShowToolsMenu` | CHƯA XÁC NHẬN |
| WP23-VIEWLIST | Giữ nút "Xem danh sách" ở hàng cảnh báo tệp bị bỏ qua (thẻ WP-23 ghi "skipped") | (b) bỏ nút (shell không có đường mở danh sách bị bỏ qua) | Ẩn `StatusPanel.ViewList` | CHƯA XÁC NHẬN |
| WP21-PRESENT | Cửa sổ thường luôn `Present(0)` (không xé hình: DWM ghép trọn khung; đo `Present(1)` chặn P50 222 ms mọi khung); F11 `Present(1)` + chờ waitable không chặn, tự hạ cấp nếu chặn. Nâng cấp preview -> full upload nền rồi mới thay | (b) giữ `Present(1)` cả cửa sổ thường (tearing-free chắc chắn nhưng UI thread chặn); (c) luôn `Present(0)` kể cả F11 (xé hình ở independent flip) | `FramePacer.ModeFor` một hàm | CHƯA XÁC NHẬN |
| WP21-STALL | Nguyên nhân gốc đã xác định (GPU Intel rỗi > ~25 ms thì lần nộp việc đầu chặn ~0,47 s; xem [NOWPF-WP21STALL](NOWPF-WP21STALL-GPU-KEEPWARM.md)). Đã làm theo khuyến nghị: `GpuKeepAlive` luồng nền nộp lệnh tí hon mỗi 20 ms trong 5 s sau mỗi tương tác của cửa sổ ĐANG ACTIVE, BẬT mặc định (key->frame P95 215-229 -> 8,8 ms, khung >= 100 ms 26,5 -> 0 %, +0,6 % CPU một lõi, ~4 % engine GPU khi tương tác) | (a) bật mặc định, 5 s (đã làm); (b) tắt mặc định; giữ ấm liên tục khi active; tắt khi dùng pin | `PHOTOREVIEW_GPU_KEEPWARM=0` hoặc bỏ `RegisterKeepWarm()` | CHƯA XÁC NHẬN |
| WP25-REFIT | Đang F11, `WM_DISPLAYCHANGE`/`WM_DPICHANGED` ép lại cửa sổ lên rcMonitor của monitor gần nhất (thêm so với bản WPF) | giữ y WPF (để Windows dời, cửa sổ có thể không phủ hết) | bỏ handler / `ShellFullscreen.Refit` | CHƯA XÁC NHẬN |
| WP25-SURFACE-DPI | Bề mặt D2D `Resize` cả khi chỉ DPI đổi (cùng số pixel) | chỉ khi cỡ pixel đổi | một điều kiện ở `ShellMainWindow.Frame.cs` | CHƯA XÁC NHẬN |
| WP26-COMPARE | Khi so sánh đang hiện, thanh cuộn của `ImageScroll` vẫn còn thấy và nhấn được ở dải 8 DIP quanh `ComparePanel` (đúng như bản WPF; thanh nằm dưới panel `Margin=8`) | (b) ẩn hẳn thanh cuộn khi so sánh (đẹp hơn, lệch WPF) | Một điều kiện ở `ShellScrollBars.Draw/HitTest` | CHƯA XÁC NHẬN |
| WP24-ANCHOR | Menu chuột phải mở bằng phím Menu/Shift+F10 neo ở GIỮA vùng client (WPF khi `CursorLeft == -1` có thể neo khác; chưa kiểm chứng trên WPF thật) | neo ở góc trên trái client / vị trí con trỏ cuối | Một hàm `ClientCenterScreenPixel` ở `ContextMenuMessageHandler` | CHƯA XÁC NHẬN |
| WP28-HALFPIXEL | Ở 100 % tại nửa pixel thiết bị (client lẻ, offset cuộn phân số ở DPI > 1) shell snap vào lưới pixel (sắc nét, lệch <= 0,5 px; ảnh chụp 28,3 dB, mẫu vạch 11,6 dB) còn WPF nội suy làm mờ | (b) bỏ snap khi góc rơi vào nửa pixel để mờ giống WPF | Một nhánh ở `D2DDrawContext.DrawImage` (`DrawGeometry.TrySnapPixelExact`) | CHƯA XÁC NHẬN |
| NW-2 | Toolchain AOT: người dùng ĐÃ cài VS Build Tools 2022 (MSVC 14.44 + SDK 26100) 2026-10-12; spike S1 (WP-32) đã chạy: shell publish AOT được, ảnh đầu 355-390 ms vs R2R 821-933 ms cùng lô (xem [NOWPF-WP32](NOWPF-WP32-AOT-SPIKE.md)) | không | Gỡ toolchain (không ảnh hưởng bản R2R) | ĐÃ XÁC NHẬN (cài); kết luận spike CHƯA XÁC NHẬN |
| WP32-GO | Đi tiếp AOT làm mục tiêu: WP-33 (bỏ COM `dynamic`, 78 cảnh báo ở 2 file) -> WP-34 (`IsAotCompatible`, lấy publish AOT làm cổng, giữ `EventSourceSupport=true`) -> WP-36 (`PublishAot`, bỏ WindowsDesktop khỏi bản AOT, CI); WP-35 (cửa sổ phụ) vẫn quyết ở NW-3; G0.1 (<= 200 ms) hụt (355-390 ms) nhưng G0.3 đạt xa | (b) dừng ở bản hybrid R2R + cầu WPF (không AOT): không cần WP-33..36, ảnh đầu ~460 ms | Không có mã đổi hành vi; bỏ WP-33..36 khỏi kế hoạch | CHƯA XÁC NHẬN |
| WP28-REFERENCE | Golden G-PIX là đầu ra bộ raster phần mềm của WPF (`RenderTargetBitmap`); chưa so với ảnh chụp màn hình thật của đường phần cứng | (b) ghi golden bằng chụp cửa sổ thật trên desktop tương tác | Đổi `Capture` của `PixelGoldenRecorder` + ghi lại | CHƯA XÁC NHẬN |
| WP33-SHELL-COM | Đường COM shell chuyển sang typed: gửi Thùng rác = `IFileOperation` (thêm `FOFX_RECYCLEONDELETE` ngoài cờ cũ `FOF_ALLOWUNDO`...), khôi phục = `IShellItem2` + `IContextMenu` (đọc `System.DateModified` bằng FILETIME, tên mục = `SIGDN_PARENTRELATIVE`), mỗi lần trên luồng STA riêng; Explorer = `IShellWindows` theo `Item(i)`; chi tiết [NOWPF-WP33](NOWPF-WP33-COM-SHELL.md) | (b) giữ `FileSystem.DeleteFile` VB + `dynamic` (không AOT) | Đổi `ShellFileRecycler.RecycleFlags` / revert 1 commit | CHƯA XÁC NHẬN |
| DOCS-BUDGET-OPEN-DECISIONS | Nâng trần `OPEN-DECISIONS.md` 48 -> 49 KB (`tools/docs-budget.ps1`): bảng sinh tự động chỉ còn dư 10 B (CRLF) trước hàng WP-33 | (b) rút gọn các hàng cũ của agent khác | Đặt lại 48 * 1024 và rút gọn hàng | CHƯA XÁC NHẬN |

## Không làm mà không hỏi (vẫn cần người dùng)
- NE-1/2/5/8 đã chốt trước đó. Chế độ chạy nền/khay, cài thêm toolchain (MSVC + SDK đã cài 2026-10-12; agent không tự cài), xoá dữ liệu người dùng, bất cứ thay đổi
  nào chạm Thùng rác thật: KHÔNG tự làm.
