---
id: NOWPF-DIALOG-CHROME-NO-ORDINAL
order: 1170
summary: |-
  Hộp thoại Win32 gốc (ZoomPrompt, Diagnostics, SkippedFiles, BatchReview, ActionProfiles, Benchmark, Settings, Recovery) bỏ phụ thuộc uxtheme ordinal (SetPreferredAppMode/FlushMenuThemes/AllowDarkModeForWindow) và chủ đề DarkMode_*: điều khiển xoá chủ đề (SetWindowTheme "" "") rồi vẽ tay bằng bảng màu Dark.* (nút/check/radio/group/combo/progress/trackbar/đường kẻ: WM_PAINT; ô nhập/listbox/listview/progress: viền phẳng WM_NCPAINT; hàng chọn listview: sửa NM_CUSTOMDRAW ở subclass hộp thoại; header: tô phần thừa). DarkModeMenus bị xoá. Nền tối + tương phản >= 4,5:1 đo bằng điểm ảnh; giao diện KHÔNG đổi khi chế độ ứng dụng uxtheme bị đổi qua ordinal. CHƯA XÁC NHẬN trên màn hình thật build 26300.
---

# NOWPF-DIALOG-CHROME-NO-ORDINAL - hộp thoại tối không phụ thuộc chủ đề hệ thống (2026-10-13)

Tiếp [NOWPF-SHELL-MENUS-ICON](NOWPF-SHELL-MENUS-ICON.md) (menu: nền trắng + chữ sáng vì ordinal ForceDark nửa vời trên Windows 11 build
26300, desktop ẩn không có DWM nên không tái hiện). `NativeDialogChrome` còn dùng cùng cơ chế + chủ đề `DarkMode_Explorer/CFD/ItemsView`
=> nguy cơ cùng lỗi. Không đổi presenter, hành vi, bố cục.

## 1. Cách làm

| Điều khiển | Trước | Sau (`Dialogs/Native/NativeDialogChromePainter.cs`) |
|---|---|---|
| Mọi lớp | `SetPreferredAppMode(ForceDark)` + `AllowDarkModeForWindow` + `SetWindowTheme("DarkMode_*")` | `SetWindowTheme("", "")` (dáng cổ điển, mọi bộ phận dùng màu hệ thống đã biết); không gọi ordinal; title bar vẫn `DWMWA_USE_IMMERSIVE_DARK_MODE` |
| Nút, check, radio | chủ đề tối + NM_CUSTOMDRAW chữ | subclass `WM_PAINT` đệm kép: `Dark.Button/ButtonHover/ButtonPressed`, viền `BorderStrong` (mặc định `TextSoft`), hover bằng `TrackMouseEvent`, focus/phím tắt theo `WM_QUERYUISTATE`; hộp chọn `Dark.Input`/`Accent` + dấu tích; radio `Accent` |
| Group box | dáng cổ điển | chỉ vạch tiêu đề + khung (KHÔNG tô vùng client: điều khiển anh em nằm trong khung, không có WS_CLIPSIBLINGS) |
| ComboBox | `DarkMode_CFD` (+ `DarkMode_Explorer` cho danh sách) | ô đóng vẽ tay (`Dark.Input`, chữ mục chọn, mũi tên, viền `Accent` khi focus/thả); danh sách thả giữ dáng cổ điển, màu qua `WM_CTLCOLORLISTBOX` có sẵn |
| Edit / ListBox / ListView | `DarkMode_CFD`/`Explorer` | cổ điển; ruột qua `WM_CTLCOLOR*`/`LVM_SETBKCOLOR`; viền 3D bị vẽ đè bằng viền phẳng `BorderStrong` (+ vòng trong cùng màu ruột) ở `WM_NCPAINT`/`WM_PRINT` |
| ListView hàng chọn | chủ đề | subclass hộp thoại sửa `NM_CUSTOMDRAW`: hàng chọn (hỏi `LVM_GETITEMSTATE`, vì cờ `CDIS_SELECTED` không đáng tin ở dáng cổ điển) = `Dark.Selection` + chữ `TextStrong`, bỏ cờ chọn để listview không tô COLOR_BTNFACE sáng + chữ sáng khi mất focus; tự xin `NOTIFYITEMDRAW` nên listview không có handler cũng đúng |
| Header listview | `DarkMode_ItemsView` + vẽ ô | cổ điển + vẽ ô (cũ) + tô phần thừa bên phải cột cuối (`Dark.Surface`) |
| Progress / Trackbar | `DarkMode_Explorer` | vẽ tay: track + phần đã chạy `Accent`; kênh + thumb `Accent` + vạch chia (tickFrequency dày được thưa) |
| Thanh cuộn | `DarkMode_Explorer` (tối) | **cổ điển (sáng)**: không chữ nên luôn đọc được; vẽ tay thanh cuộn không bền (vòng theo dõi chuột của hệ thống tự vẽ) - đánh đổi có chủ ý, người dùng cần xem trên máy thật |

`DarkModeMenus` (không còn nơi dùng sau PR #492) và `DarkModeMenusTests` bị xoá. Interop thêm ở lớp riêng `DialogChromeNative`
(`BeginPaint/EndPaint`, `TrackMouseEvent`, `GetWindowDC`, `BitBlt`, `Ellipse/Polyline`, `SetProp/GetProp`). Cờ "đã gắn" dùng window property
chứ KHÔNG dùng `GetWindowSubclass` (chỉ xuất bởi comctl32 v6; testhost không manifest nạp v5 => EntryPointNotFound bị nuốt, dialog mất dáng tối).

## 2. Bằng chứng

- Ảnh chụp `PrintWindow` trước/sau (en + vi, 100 %/150 %, gallery mọi điều khiển, ZoomPrompt, Diagnostics, Skipped, Batch, ActionProfiles,
  Benchmark; `NativeDialogSnapshotTests`, `PHOTOREVIEW_DIALOG_SNAPSHOTS`): dáng gần như cũ (nút phẳng, hàng chọn, combo, group box nhẹ hơn),
  khác: thanh cuộn sáng, hộp chọn/radio theo bảng màu. "Trước" trên desktop ẩn đã tối đúng nên không tái hiện lỗi.
- Tương phản đo bằng điểm ảnh (`NativeDialogChromeTests`, mọi điều khiển của 7 hộp thoại + Benchmark, comctl32 v5 và v6, en/vi, 100/150 %):
  nền client có độ sáng <= 0,12, mực/nền >= 4,5:1 (chữ) hoặc >= 3:1 (đồ hoạ). Giá trị theo bảng màu: chữ nút 13,8:1 (hover 11,7; pressed 9,7),
  chữ tắt `TextMuted` 4,9:1, ô nhập 15,3:1, chữ trên `Dark.Selection` 7,1:1, `Warning` trên nền 11,7:1, huy hiệu 7,5:1, `Accent`/`Surface` 3,4:1 (đồ hoạ).
- "Chế độ hỏng ordinal": test gọi chính ordinal 135/136 của uxtheme đưa tiến trình vào mọi chế độ (Default/AllowDark/ForceDark/ForceLight)
  và lật chế độ + `WM_THEMECHANGED` khi hộp thoại đang mở: điểm ảnh của mọi điều khiển KHÔNG đổi (`Look_DoesNotChange_WhateverThePreferredAppModeIs...`).
- Chạy 2026-10-13 (desktop ẩn `PRChromeFix`): `Shell.Tests` (CI filter) 1 364 + bộ mới đều đạt; AOT `tools/check-aot-warnings.ps1 -RequireToolchain`
  = 0 cảnh báo, `PhotoReview.exe` 15,1 MB.

## 3. Đột biến (hai bước)

21 đột biến thủ công (script ở scratchpad, build Release + test trên desktop ẩn riêng). Bước 1 = `Shell.Tests` lọc `Dialogs.Native`
(`Category!=Manual&Category!=Native`): **21/21 bị giết ở bước 1, 0 sống** nên không cần bước 2.

| # | Đột biến | Kết quả |
|---|---|---|
| 1-2 | màu hover / pressed của nút = màu thường | giết (2 ca mỗi đột biến) |
| 3-4 | chữ nút tắt = `TextFaint` (< 4,5:1) / chữ nút bật mờ | giết (4 / 26 ca) |
| 5 | thứ tự vẽ: nền tô `F0F0F0` thay `Dark.Window` | giết (12) |
| 6-7 | hộp chọn tick không tô `Accent` / chấm radio mất | giết (2 / 3) |
| 8 | progress tô sai tỉ lệ | giết (2) |
| 9 | bỏ vẽ viền phẳng (WM_NCPAINT) | giết (2) |
| 10 | phần thừa header sáng | giết (2) |
| 11-13 | màu hàng chọn sai / không bỏ cờ chọn / không xin NOTIFYITEMDRAW | giết (4 / 4 / 2) |
| 14-15 | không subclass hộp thoại / ThemeFor không xoá chủ đề | giết (4 / 12) |
| 16, 21 | `WM_CTLCOLOREDIT` trả brush cửa sổ / chữ static đen | giết (2 / 31) |
| 17-20 | viền ngoài trùng nền / mất khung group box / thumb sai màu / mặt combo sai màu | giết (2 mỗi đột biến) |

## 4. Chưa kiểm / ghi chú

- Màn hình thật build 26300 (DWM): chưa xác nhận; kỳ vọng nền tối vì mọi màu do app tự vẽ.
- Số đo thời gian mở: máy đang tải bởi agent khác nên số đo wall-clock trong tiến trình test không dùng được (400-800 ms cả khi không có chrome);
  phần thêm của chrome là vài `SetWindowSubclass/SetProp` mỗi điều khiển và KHÔNG còn `LoadLibraryEx(uxtheme)` + 3 ordinal. Cổng <= 150 ms (GĐ2: tệ nhất 66 ms) cần đo lại trên máy yên.
- Thanh cuộn cổ điển (sáng) là thay đổi nhìn thấy được so với bản `DarkMode_Explorer`.
