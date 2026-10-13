---
id: NOWPF-WP27-VISUAL-CHECK
order: 1240
summary: |-
  WP-27 (kiểm bằng mắt shell Win32 trên desktop ẩn, 2026-10-14, master 0a15b79d): menu chuột phải/Tools tối đúng (en+vi, 100/150 %, hover, tắt, dấu chọn, submenu), 8 hộp thoại phụ tối không cắt/chồng chữ; 2 LỖI: (1) SAU #503 mọi trang Settings TRỐNG (điều khiển bị group box cắt vì WS_CLIPSIBLINGS, test #503 không thấy), (2) emoji đơn sắc (DrawText không bật EnableColorFont). Chưa kiểm: F11 hai màn hình, DPI hai màn hình, title bar tối DWM. Không sửa mã; ảnh ở perf/wp27-visual.
---

# NOWPF-WP27-VISUAL-CHECK - kiểm bằng mắt trên desktop ẩn (2026-10-14)

Phạm vi: phần của WP-27 ("xem bằng mắt: menu tối, emoji màu, F11 hai màn hình") làm được trên desktop ẩn. Cây: `origin/master` 0a15b79d
(đã gồm #503). KHÔNG sửa mã sản phẩm. Ảnh: [`../perf/wp27-visual/`](../perf/wp27-visual/) (26 ảnh đại diện, 1,2 MB; số đo cả 300 ảnh ở
`capture-log.txt` cùng thư mục).

## 1. Cách chụp (và một cạm bẫy)

- Bộ chụp thủ công `tests/PhotoReview.Shell.Tests/Dialogs/Native/Wp27VisualCheckTests.cs` (`Category=Manual`, bỏ qua nếu không có biến):
  `PHOTOREVIEW_WP27_DIR=<thư mục>` rồi `tools/run-tests-hidden.ps1 ... --filter FullyQualifiedName~Wp27VisualCheckTests`. Dùng lại
  `NativeDialogTestKit`, `SettingsDialogKit`, `RecoveryRig`, `UiThread`, `PixelGoldenShellRenderer` (HUD thật `ShellOverlay` vẽ offscreen).
  Không SendInput/SetForegroundWindow: phím vào menu là `PostMessage` tới cửa sổ chủ như `NativePopupMenuHostTests`.
- Chụp bằng `BitBlt` từ DC cửa sổ (pixel thật), mỗi ảnh còn so với `PrintWindow`: chênh = 0 điểm mới nhận ảnh.
- **Cạm bẫy:** trên desktop ẩn, cửa sổ CHƯA vẽ xong thì `BitBlt` đọc bề mặt màn hình DÙNG CHUNG = pixel của desktop THẬT của người dùng
  (các ảnh nháp đầu có nội dung cửa sổ khác trên máy; đã xoá, không đưa vào repo). Bộ chụp ép `RedrawWindow` (khung + con) rồi thử lại
  tới 3 lần cho đến khi `BitBlt` == `PrintWindow`; ảnh nào còn chênh được đánh dấu `DIFF` trong log (lần chạy cuối: 0 ảnh DIFF).
  Ai viết bộ chụp mới trên desktop ẩn phải làm tương tự, nếu không sẽ vô tình chụp màn hình thật.
- Menu: dựng bằng đúng `NativePopupMenuHost.Populate` + `OwnerDrawMenuSession` (DPI truyền thẳng 96/144 vì tiến trình test không có DPI
  của màn hình; `Show` thật chỉ thêm `GetDpiForWindow`), `TrackPopupMenuEx` thật, chụp các cửa sổ `#32768`.

## 2. Kết quả

| # | Mục kiểm | Kết quả | Ảnh / ghi chú |
|---|---|---|---|
| 1 | Menu chuột phải: nền, chữ, phân cách, phím tắt căn phải (en, vi, 100 %, 150 %) | ĐẠT | `menu-context-en-open`, `menu-context-sub-vi-150`; nền #1E1E1E, chữ sáng, không vùng trắng (nearWhite <= 1 %) |
| 2 | Menu: mục hover, mục tắt (xám nhưng đọc được), dấu chọn, chữ dài + phím tắt không chồng | ĐẠT | `menu-context-en-hover3`, `menu-synthetic-en-hover-disabled`; hover trên mục tắt vẫn sáng hơn nền |
| 3 | Submenu Zoom (cấp 1), dấu chọn 100 % + "Also set as click zoom level" | ĐẠT | `menu-context-sub-en-150-level1`, `menu-synthetic-sub-vi-level1` |
| 4 | Menu Tools (⋮) en/vi, 100/150 %, mục tắt khi không có ảnh | ĐẠT | `menu-tools-vi-150-hover2` |
| 5 | Mũi tên submenu | ĐẠT (ghi chú) | thấy thêm mũi tên đen nhỏ của hệ thống cạnh mũi tên sáng của app (đã biết, NOWPF-SHELL-MENUS-ICON mục 3); rõ nhất ở mục đang hover |
| 6 | Menu có icon | KHÔNG ÁP DỤNG | `MenuItemModel` không có trường icon; chỉ có dấu chọn |
| 7 | Settings: 7 trang x (đầu, giữa, cuối cuộn) x en/vi x 100/150 %, nền tối, nút, combo, trackbar, ô nhập, mục tắt | **LỖI L1** | `settings-p1-en-top` (trống) so với `settings-p1-en-noclip-top` (nội dung) |
| 7b | Settings, giả định L1 đã sửa (bỏ WS_CLIPSIBLINGS): cắt chữ, chồng chữ, thanh cuộn, nút bị che | ĐẠT | `settings-p1-en-noclip-mid`, `-p2-en-noclip-top`, `-p4-en-noclip-top`, `-p5-vi-noclip-top`, `-p6-en-150-noclip-top`, `-p1-vi-150-noclip-mid`; không cắt/chồng chữ, nút Save/Apply/Cancel đủ |
| 8 | Settings: focus ring | ĐẠT | `settings-p0-en-noclip-focus` (combo có viền focus sáng; nút mặc định Save có viền) |
| 9 | Recovery (en/vi, 100/150 %, chọn hàng, tên tệp rất dài) | ĐẠT | `recovery-vi-selected`: huy hiệu tone màu, tên dài có "...", ô đường dẫn một dòng cắt theo thiết kế |
| 10 | Diagnostics | ĐẠT | `diagnostics-en`; dòng histogram dài xuống hàng, không cắt |
| 11 | ZoomPrompt (bình thường + lỗi 9999, 100/150 %) | ĐẠT | `zoom-error-vi-150`: chữ lỗi màu Warning rõ trên nền tối |
| 12 | BatchReview | ĐẠT | `batch-vi-150`: 30 dòng, thanh cuộn, nút cuối không bị che |
| 13 | Skipped | ĐẠT | `skipped-en`: dòng 100 ký tự không cắt |
| 14 | ActionProfiles | ĐẠT | `actionprofiles-en`: huy hiệu phím, combo, check |
| 15 | Benchmark (trước/sau khi chạy, hàng chọn, hàng xanh/đỏ, 150 %) | ĐẠT | `benchmark-en` |
| 16 | HUD: toolbar, status, folder, zoom, badge (en/vi, 100/150 %), không cắt/đè | ĐẠT (giới hạn) | `hud-vi-dpi100-toolbar-crop`, `-status-crop`, `hud-en-dpi150-toolbar-crop`; `ShellOverlay` thật vẽ offscreen trên cảnh tổng hợp, KHÔNG phải ảnh `C:\Xiuren` qua cửa sổ thật (swap chain DXGI không chụp được bằng GDI trên desktop ẩn; không thử) |
| 17 | Emoji màu (📂 📁 ⚙ ★ 🔍 ✔ ❤) | **LỖI L2** | `emoji-en`: 0 pixel có màu (chroma), toàn nét trắng đơn sắc |
| 18 | Icon app ở caption các hộp thoại phụ | ĐẠT | thấy icon máy ảnh xanh ở góc trái caption mọi ảnh dialog |
| 19 | Title bar tối DWM | CHƯA KIỂM - cần người dùng | desktop ẩn không có DWM: caption là khung cổ điển sáng (không phản ánh máy thật) |
| 20 | F11 toàn màn hình hai màn hình | CHƯA KIỂM - cần người dùng | cần phần cứng thật, hai màn hình |
| 21 | DPI hai màn hình (kéo cửa sổ giữa 100 % và 150 %) | CHƯA KIỂM - cần người dùng | 150 % chỉ mô phỏng bằng `WM_DPICHANGED`/tham số DPI |
| 22 | Menu/hộp thoại trên desktop thật build 26300 (DWM tổng hợp popup) | CHƯA KIỂM - cần người dùng | ghi ở NOWPF-SHELL-MENUS-ICON mục 2: lỗi "menu trắng" chỉ thấy trên desktop thật |
| 23 | Thanh cuộn sáng (cổ điển) của hộp thoại | ĐẠT (ghi chú) | chủ ý (NOWPF-DIALOG-CHROME-NO-ORDINAL); tương phản mạnh so với nền tối, người dùng nên xem trên máy thật |

Đã ĐỌC từng ảnh đại diện nêu trên; các ảnh còn lại (vi/150 % của cùng trang/hộp thoại) chỉ được xác nhận bằng số đo trong `capture-log.txt`
(BitBlt == PrintWindow, tỉ lệ pixel gần trắng 0,3-2 %, số màu), không đọc từng ảnh.

## 3. Lỗi và điều kiện tái hiện

### L1 (nghiêm trọng) - sau #503 mọi trang Settings hiện TRỐNG, chỉ còn khung group box

- Hiện trạng: `settings-p0..p6-*-top.png` (bản đang chạy) chỉ có tiêu đề, danh sách trang, các khung group box rỗng và nút dưới cùng;
  KHÔNG có checkbox/combo/ô nhập/nhãn. Cùng ảnh với `NativeDialogSnapshotTests`/`SettingsSnapshotTests` có sẵn (PrintWindow) nên không phải
  do cách chụp. Đây là hồi quy do #503 (`0a15b79d`), KHÔNG phải lỗi cuộn cũ.
- Tái hiện chính xác: `new SettingsDialog(new AppSettings(), null, null, null, null, Options(...))`, `ShowModal`, `GoTo(dialog, page)` cho
  bất kỳ trang 0..6, `Drain`, chụp (BitBlt hoặc PrintWindow). Hoặc chạy `Settings_Diag_ChildrenOfThePage` (in cây con + so sánh
  `diag-base` / `diag-base-noclipsib`).
- Nguyên nhân (đã chứng minh bằng thử nghiệm): `SettingsDialog.CreatePageControls` đặt `WS_CLIPSIBLINGS` cho MỌI điều khiển, kể cả group box
  (style `0x54000007`). Thứ tự Z (liệt kê bằng `GetWindow(GW_CHILD/GW_HWNDNEXT)`: trên -> dưới) là: group box TRƯỚC (cao hơn), các
  điều khiển của nó SAU. Mỗi điều khiển có `WS_CLIPSIBLINGS` bị trừ vùng của anh em nằm trên nó = toàn bộ hình chữ nhật của group box chứa
  nó => bị cắt hết, không vẽ gì. Bỏ cờ này khỏi các điều khiển con (`SetWindowLongPtr` ... `& ~0x04000000`) thì nội dung hiện đủ
  (`settings-p*-noclip-*.png`, ví dụ `settings-p1-en-noclip-top`).
- Vì sao test #503 xanh: `SettingsScrollRepaintFixTests` so ảnh sau mỗi bước cuộn với ảnh sau `RedrawWindow`; cả hai đều trống nên bằng nhau.
  Cần một khẳng định điểm ảnh "vùng của điều khiển không toàn màu nền" (hoặc số điểm khác nền trong từng group box > 0).
- Gợi ý hướng sửa (lead quyết, không làm ở đây): đưa group box xuống đáy Z (`SetWindowPos(HWND_BOTTOM)`) sau khi tạo trang (khung nằm DƯỚI
  điều khiển của nó, đúng ý comment trong code "group box frame is a full-rect fill under the controls"), hoặc không đặt
  `WS_CLIPSIBLINGS` cho điều khiển nằm trong group box; sau đó chạy lại `Settings_EverySevenPages_TopMiddleBottom` và xem ảnh không `-noclip`.

### L2 - emoji đơn sắc (không có màu như bản WPF)

- Hiện trạng: `emoji-en.png` (văn bản "📂 📁 ⚙ ⋮ ★ 🔍 ✔ ❤" cỡ 48 px, font Segoe UI + fallback) chỉ ra nét trắng; số pixel có độ bão hoà
  = 0. Toolbar thật (📂 ⚙ ⋮) và dòng folder-info "📁 {folder}" cùng đường vẽ => đơn sắc (`hud-vi-dpi100-toolbar-crop`: 📂 là hình viền).
- Tái hiện: `Hud_And_ColourEmoji` hoặc `DWriteTextRenderer.CreateLayout("📂 📁 🔍")` + `D2DDrawContext.DrawText` trên offscreen, đếm pixel có màu.
- Nguyên nhân: `src/PhotoReview.Shell.Rendering/D2DDrawContext.cs` (DrawText) gọi `DrawTextLayout(..., D2dDrawTextOptions.None)`; cờ
  `D2dDrawTextOptions.EnableColorFont` (đã khai báo ở `Interop/Graphics/D2D1.cs`) không được dùng nên glyph màu của Segoe UI Emoji bị vẽ đơn sắc.
  Cần kiểm lại trên desktop thật sau khi bật cờ (ClearType/độ lệch baseline của emoji màu có thể đổi hộp bao toolbar +-1-2 DIP;
  golden `overlay-boxes.v1.json` +-2 DIP).

## 4. Việc người dùng cần làm trên máy thật

Mục 19-22 ở bảng; cộng: nhìn lại menu chuột phải/Tools trên desktop thật build 26300 (đang CHƯA XÁC NHẬN ở hai fragment cũ), nhìn thanh cuộn sáng
của hộp thoại, và (sau khi L1 sửa) nhìn Settings thật.
