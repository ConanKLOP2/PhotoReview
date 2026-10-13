---
id: NOWPF-WP35-G2
order: 1120
summary: |-
  WP-35 GĐ2 (phương án B của spike S3, CHƯA XÁC NHẬN): khung hộp thoại Win32 gốc có thêm combobox, listbox đa chọn owner-draw có huy hiệu, listview report (cột, sắp xếp, header tối), progress, trackbar, edit nhiều dòng/số, checkbox/radio/group box, tooltip theo dòng (dark nhìn được qua ảnh chụp, không kích hoạt điều kiện dừng); cửa sổ Action profiles (modal, lồng được) và Benchmark MODELESS (một cửa sổ duy nhất, Esc huỷ lượt chạy rồi mới đóng) dùng presenter G0/G2a; cổng Win32 ưu tiên cho Benchmark/ActionProfiles, cầu WPF còn cho Settings/Recovery. Đo exe thật: mở 66 ms tệ nhất; 0 cảnh báo ILC (sửa BenchmarkReport.ToJson source-gen); không nạp WPF; 32 đột biến, 0 sống thật.
---

# NOWPF-WP35-G2 - hộp thoại Win32 gốc, giai đoạn 2: điều khiển mới + ActionProfiles + Benchmark modeless (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiếp [NOWPF-WP35-G1-WIN32-DIALOGS](NOWPF-WP35-G1-WIN32-DIALOGS.md) theo
lộ trình GĐ0-5 của [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md). Presenter không đổi:
[ActionProfilesPresenter](NOWPF-WP35-G0-PRESENTERS.md), [BenchmarkPresenter + IBenchmarkView](NOWPF-WP35-G2A-BENCHMARK.md). Bản WPF
(`App.WpfWindows`) và cầu WPF giữ nguyên (lưới an toàn cho Settings/Recovery).

## 1. Đã làm

| Phần | File (`src/`) | Nội dung |
|---|---|---|
| Interop | `Shell.Interop/Win32/CommonControlsNative.cs` | `[LibraryImport]` comctl32 (`InitCommonControlsEx`, `SetWindowSubclass`), `GetComboBoxInfo`, `DrawFocusRect`, `FillRect`... và `ControlConstants` (CB_/LB_/LVM_/HDM_/PBM_/TBM_/TTM_/NM_CUSTOMDRAW). Không DllImport. |
| Khung | `Shell.Win32/Dialogs/Native/DialogTemplate.cs` | lớp common-control ghi theo TÊN lớp (`SysListView32`, `msctls_progress32`, `msctls_trackbar32`) thay cho atom. |
| Khung | `.../NativeDialog.cs` | `ShowModeless` (không chặn, tự dọn, `closed` callback, bộ lọc `IsDialogMessage` đăng ký vào vòng lặp shell), `Activate`, `OnCancel` (Esc), `OnNotify`/`OnScroll`/`ListItemText`/`OnClosed`, WM_DRAWITEM owner-draw, tooltip chữ cố định + THEO DÒNG, subclass listview để vẽ header, ép cửa sổ kéo giãn vừa vùng làm việc. |
| Điều khiển | `.../NativeControls.cs` | `ComboBoxControl`, `ListBoxControl` (+ vẽ huy hiệu), `ListViewControl` (cột, hàng, chọn hàng, tô hàng NM_CUSTOMDRAW, vẽ header), `ProgressControl`, `TrackbarControl`, `ButtonStateControl`, `CommonControlsInit`. |
| Dark | `.../NativeDialogChrome.cs` | `DarkDialogChrome` có seam `setTheme`: combobox `DarkMode_CFD` + danh sách thả (cửa sổ riêng) `DarkMode_Explorer`; listview `DarkMode_Explorer` + header `DarkMode_ItemsView` (chữ tự vẽ); progress/trackbar `DarkMode_Explorer`; radio/group box dáng cổ điển (chủ đề tối vẽ chữ xám gần vô hình); chữ nút/static qua WM_CTLCOLOR* + NM_CUSTOMDRAW. |
| Cửa sổ | `.../ActionProfilesDialog.cs` | modal, kéo giãn: listbox owner-draw (huy hiệu = phím tắt) + Add/Remove/Import/Export + 3 ô, combobox thao tác, checkbox; Import/Export qua `IShellFileDialog`; cảnh báo qua `IDialogWarningSink` (TaskDialog). |
| Cửa sổ | `.../BenchmarkDialog.cs` | MODELESS, nút thu nhỏ/phóng to: ô thư mục + Chọn thư mục, giới hạn ảnh, listbox đa chọn 32 profile (huy hiệu = chế độ nạp, tooltip = mô tả), Run/Quick check/Cancel, progress, trạng thái xuống dòng, listview 10 cột (sắp xếp, hàng tốt nhất xanh, lỗi đỏ, xen kẽ), Open report/folder, Close. |
| Cổng | `.../Win32SecondaryWindowPort.cs` | `Supports` thêm Benchmark + ActionProfiles; `ShowBenchmark` một cửa sổ duy nhất (mở lần hai = `Activate`), `ShowActionProfiles` (trả danh sách đã sửa hoặc null), `Dispose` đóng cả modeless; `SecondaryWindowRouter.ShowActionProfiles`. |
| Cổng | `Dialogs/Win32DialogService.cs` | `ISecondaryWindowPort.ShowActionProfiles` (mặc định null: không phá cầu WPF) + `SecondaryWindowKind.ActionProfiles`. |
| ILC | `Benchmarking/BenchmarkModels.cs` | `BenchmarkReport.ToJson` dùng `JsonSerializerContext` sinh mã (đồ thị AOT nay chứa Benchmark; reflection = 2 cảnh báo IL2026/IL3050). Cùng đầu ra `WriteIndented`. |

## 2. Thiết kế

1. **Modeless đúng**: `CreateDialogIndirectParam` + owner KHÔNG bị vô hiệu hoá; vòng lặp shell không có "vòng riêng", nên `IRawMessageFilter`
   (đăng ký bằng `MessageLoop.AddMessageFilter`) gọi `IsDialogMessage` cho message của cửa sổ - Tab/Enter/Esc/phím tắt/IME như hộp thoại thật,
   không nuốt message của cửa sổ khác. Đóng (X, nút, `Close`, shell thoát) dọn ngay: gỡ bộ lọc, `OnClosed` (huỷ lượt chạy), huỷ cửa sổ, gọi `closed`.
2. **Esc**: dialog manager gửi IDCANCEL; Benchmark có nút Close riêng (id 118) nên IDCANCEL chỉ do Esc: đang chạy = huỷ lượt chạy (cửa sổ ở lại),
   rảnh = đóng. X/Alt+F4/nút Close luôn đóng (và huỷ qua presenter).
3. **Tiến độ từ luồng nền**: presenter đẩy về UI qua `IUiScheduler` (`Win32UiDispatcher` của shell); view chỉ chạy trên UI thread, mọi gọi sau khi
   cửa sổ huỷ bị chặn (`Hwnd == 0`), tiến độ đến muộn do presenter loại. Focus không bao giờ nằm trên điều khiển bị khoá (chuyển sang Cancel/Run).
4. **Một cửa sổ Benchmark**: cổng giữ tham chiếu; mở lần hai `Activate` (khôi phục nếu thu nhỏ + lên trước).
5. **Listview**: không ảo (<= vài chục hàng); mỗi `RefreshRows`/thay đổi collection dựng lại theo cột sắp xếp, giữ hàng đang chọn (theo chỉ số nguồn lưu
   trong `lParam`). Cột chia theo dialog-unit mỗi lần bố cục nên đúng ở mọi DPI. Tô hàng bằng NM_CUSTOMDRAW (nền hàng tốt nhất `Dark.SuccessBackground`,
   lỗi `Dark.ErrorBackground`, xen kẽ `Dark.Panel`/`Dark.RowAlt` như DataGrid). Sắp xếp: bấm header (như DataGrid), số theo giá trị.
6. **Header tối**: listview KHÔNG chuyển NM_CUSTOMDRAW của header lên hộp thoại; subclass listview (`SetWindowSubclass`) bắt WM_NOTIFY từ header và
   tự vẽ ô (nền `Dark.Surface`, chữ sáng, vạch ngăn, mũi tên ▲/▼) - chủ đề `DarkMode_ItemsView` một mình vẽ chữ xám khó đọc.
7. **Tooltip theo dòng**: một tool cho cả listbox (`TTF_SUBCLASS` + `LPSTR_TEXTCALLBACK`), subclass listbox để khi đổi dòng thì `TTM_POP` hiện lại;
   `TTN_GETDISPINFO` lấy dòng dưới con trỏ (`LB_ITEMFROMPOINT`). Tooltip tắt chủ đề + `TTM_SETTIPBKCOLOR/TEXTCOLOR` để có màu tối.
8. **Cổng ưu tiên**: `Win32SecondaryWindowPort.Supports(Benchmark/ActionProfiles) = true`; router chọn cổng Win32 trước, cầu WPF chỉ cho Settings/Recovery
   (nạp lười như GĐ1). Bản AOT: Benchmark mở thật (cổng tạm `Benchmark()` chỉ còn khi `Win32DialogService` không có cổng nào).
   Codec/decoder của phiên đo: `PixelBufferImageCodec` + `WicDirectDecoder` (mặc định của executor), không `WpfBitmapSourceCodec`.
9. **Chuỗi**: không khoá catalog mới (mọi chữ dùng `actionProfiles.*`, `benchmark.*`, `common.*`, `enum.fileOperation.*`, `dialog.*`).

## 3. Kiểm chứng

| Test (`tests/`) | Ca | Phủ |
|---|---:|---|
| `Shell.Tests/Dialogs/Native/NativeControlsTests` | 23 phương thức | tạo đủ lớp điều khiển, cờ dark theo lớp + `DarkDialogChrome(setTheme)` thật (combo, danh sách thả, header, radio/group box cổ điển), combobox chọn/thả/CBN_SELCHANGE, listbox đa chọn + chọn hết/bỏ, vẽ owner-draw vào DC bộ nhớ (màu chọn, huy hiệu), tooltip theo dòng, listview (cột, căn phải, hàng, chọn hàng, bấm cột sắp xếp tăng/giảm, NM_CUSTOMDRAW hàng, header tự vẽ, mũi tên chỉ ở cột đã sắp xếp), progress kẹp 0..1, trackbar + WM_HSCROLL, edit nhiều dòng giữ Enter, edit số, checkbox/radio, bố cục không chồng ở 100/150 %, không rò GDI/USER |
| `.../ActionProfilesDialogTests` | 24 phương thức | mở + trường hàng đầu, rỗng, huy hiệu phím tắt, chọn hàng lưu hàng trước + làm mới danh sách, thao tác/xác nhận qua vòng chọn, Add (phím tắt trống kế tiếp, mở hàng mới), Remove (chọn kế, hết thì xoá trường), Apply (hợp lệ = OK giữ sửa; tên trống/phím trùng/đích ra ngoài = cảnh báo + giữ mở), Enter = Apply, 4 đường huỷ, Import (thay danh sách; file sai = cảnh báo; huỷ), Export (ghi bản đang sửa; lỗi ghi = cảnh báo), modal khoá/bật owner, modal-trong-modal, chrome một lần, bố cục 100/150 % en/vi, rò handle |
| `.../BenchmarkDialogTests` | 26 phương thức | mở không chặn (owner bật, nút mặc định Run), thư mục mặc định Pictures, tooltip mô tả + huy hiệu từng profile, Run (tiến độ, khoá điều khiển, focus, hàng, trạng thái Done, lưu báo cáo), Quick check cố định, không chọn gì, chọn hết/bỏ, Cancel, Esc đang chạy (cửa sổ ở lại) và rảnh (đóng), đóng giữa chừng bằng nút/WM_CLOSE/API (huỷ, huỷ cửa sổ, tiến độ muộn bị loại, không hàng Fail giả), Tab qua vòng lặp shell thật, thứ tự hàng + đánh dấu tốt nhất, sắp xếp tăng/giảm số, giữ hàng chọn, màu hàng, lượt hai xoá hàng cũ, Browse (owner = cửa sổ) + huỷ, Open report/folder, đổi ngôn ngữ, bố cục + status dài 100/150 %, rò handle, cổng: một cửa sổ, mở lại sau khi đóng, Dispose đóng khi đang chạy, ActionProfiles Apply/huỷ |
| `.../SecondaryWindowRoutingTests` | (chỉnh) | Benchmark/ActionProfiles do cổng Win32 nhận, cầu chỉ còn Settings/Recovery, AOT: Settings + Recovery báo, Benchmark mở thật |
| `Shell.WpfBridge.Tests/LazyLoadTests` | (chỉnh) | tiến trình sạch: sáu cửa sổ Win32 (Benchmark modeless + ActionProfiles) mở/đóng, KHÔNG nạp WPF/cầu; `ShellWiringTests`: Tab tới Benchmark Win32 qua bộ lọc shell |
| `Shell.Integration.Tests/ShellNativeDialogsIntegrationTests` | +1 | shell thật, cầu vắng: Benchmark owner = HWND shell, shell không bị khoá, mở lần hai không tạo cửa sổ, chạy THẬT 32 profile trên JPEG, đóng SHELL khi đang đo: thoát mã 0, không treo |
| `NativeDialogSnapshotTests` / `...WindowsTests` (Manual) | 3 | `PHOTOREVIEW_DIALOG_SNAPSHOTS=<dir>`: `gallery*.png`, `actionprofiles-*`, `benchmark-*` (en/vi, 100 %/150 %, đã sắp xếp, cửa sổ danh sách thả) |

Chạy 2026-10-13 (desktop ẩn `PRWp35g2`, filter CI `Category!=Manual&Category!=Native`): `Shell.Tests` 1 201, `Shell.Integration.Tests` 145, `Shell.WpfBridge.Tests` 39 (+2 thủ công), `Architecture.Tests` 158, `App.Tests` 2 593, `Integration.Tests` 1 410 - 0 lỗi. Lặp bộ mới (`Dialogs.Native`, 170 ca/lượt): 50 lượt, 50 đạt, 0 lỗi (không lượt nào treo).

## 4. Số đo

Đo trên `PhotoReview.exe` THẬT (bản probe tạm KHÔNG commit: biến môi trường `PR_WP35G2_PROBE`, `--smoke` + một JPEG, owner = HWND shell,
desktop ẩn riêng `PRWp35g2run`, dữ liệu cô lập `PHOTOREVIEW_DATA_ROOT`), 2026-10-13, máy yên (không build/test chạy song song). **Hai bản xen kẽ**
R2R hybrid (publish mặc định) và Native AOT, 6 lượt mỗi bản (R2R, AOT, R2R, AOT...), mỗi tiến trình mở Action profiles (modal) và Benchmark
(modeless) x 8 vòng. Đo từ lệnh mở tới khi cửa sổ đã tạo + bố cục + hiện (callback `ready`, trước khung vẽ đầu):

| Cửa sổ (ms) | R2R lần đầu: median / max (n=6) | R2R các lần sau: median / p95 (n=42) | AOT lần đầu: median / max | AOT các lần sau: median / p95 |
|---|---|---|---|---|
| ActionProfiles (modal, 3 hàng) | 47,7 / 56,2 | 26,2 / 31,3 | 38,3 / 41,9 | 25,4 / 29,0 |
| Benchmark (modeless, 32 profile) | 39,5 / 66,3 | 34,2 / 38,8 | 37,4 / 40,7 | 33,3 / 37,5 |

Cổng "mở <= 150 ms": ĐẠT (tệ nhất 66 ms, Benchmark lần đầu R2R). So với WPF: 170-215 ms (nóng), 470-690 ms (lần đầu) theo spike S3. Cổng "không nạp
WPF": cả `LazyLoadTests.NativeSecondaryWindows_OpenWithoutLoadingWpfOrTheBridge` (tiến trình sạch, DI thật, sáu cửa sổ) và probe (0 assembly
`Presentation*`/`WindowsBase`/`System.Xaml` ở mọi lượt, cả R2R lẫn AOT). ILC: `tools/check-aot-warnings.ps1 -RequireToolchain` = **0 cảnh báo**,
`PhotoReview.exe` AOT 11,9 MB (lần đầu có 2 cảnh báo IL2026/IL3050 từ `BenchmarkReport.ToJson`: đã sửa bằng `JsonSerializerContext`; thư mục
publish tạm đã dọn). Không treo khi đóng shell lúc Benchmark đang đo thật 32 profile: `ShellNativeDialogsIntegrationTests` thoát mã 0.

## 5. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

32 đột biến thủ công (script ở scratchpad, build Release + test trên desktop ẩn, worktree riêng). Bước 1 = `Shell.Tests` lọc `Dialogs.Native` (170 ca);
bước 2 (chỉ kẻ sống) = toàn `Shell.Tests` (CI filter); `Shell.Integration.Tests`/`Shell.WpfBridge.Tests` phủ cổng/shell (đã xanh trên mã gốc).

| # | Đột biến | Kết quả |
|---|---|---|
| M01 | combobox không có chủ đề dark | chết bước 1 |
| M02 | không áp chủ đề cho danh sách thả / header listview | chết bước 1 |
| M03 | radio/group box không dùng dáng cổ điển | chết bước 1 |
| M04 | progress không dùng chủ đề dark (làm lại trên mã cuối vì đổi từ màu tay sang chủ đề) | chết bước 1 |
| M05 | modeless vô hiệu hoá owner (như modal) | chết bước 1 |
| M06 | không đăng ký bộ lọc IsDialogMessage vào vòng lặp shell | chết bước 1 |
| M07 | Esc lúc đang chạy không huỷ lượt chạy (đóng cửa sổ) | chết bước 1 |
| M08 | đóng cửa sổ không dispose presenter (không huỷ) | chết bước 1 |
| M09 | bộ lọc modeless không gọi IsDialogMessage | chết bước 1 |
| M10 | mở lần hai tạo cửa sổ thứ hai | chết bước 1 |
| M11 | Dispose cổng không đóng cửa sổ modeless | chết bước 1 |
| M12 | router bỏ qua cổng Win32 cho ActionProfiles | chết bước 1 |
| M13 | cổng Win32 không nhận Benchmark (rơi về cầu WPF) | chết bước 1 |
| M14 | progress không kẹp 0..1 | SỐNG, **tương đương**: `PBM_SETPOS` tự kẹp vào khoảng (chỉ NaN khác, đã đổi thành 0 ở nhánh còn lại); giữ kẹp vì tường minh |
| M15 | sắp xếp luôn tăng | chết bước 1 |
| M16 | cột P95 so sánh theo chữ | chết bước 1 |
| M17 | nút Cancel không bật khi chạy | chết bước 1 |
| M18 | nút Run không khoá khi chạy | chết bước 1 |
| M19 | tô hàng tốt nhất bị đảo | chết bước 1 |
| M20 | đổi chọn hàng không lưu hàng trước | chết bước 1 |
| M21 | Apply bỏ qua kết quả presenter | chết bước 1 |
| M22 | thao tác (combo) luôn Move | chết bước 1 |
| M23 | Run chạy mọi profile thay vì các profile đã chọn | chết bước 1 |
| M24 | tooltip profile hiện tên thay vì mô tả | chết bước 1 |
| M25 | bảng không đồng bộ khi presenter xoá/thêm hàng | chết bước 1 (`ASecondRun_ClearsTheOldRows_BeforeItsFirstResult`) |
| M26 | mũi tên sắp xếp vẽ nhầm cột | chết bước 1 (`ListView_HeaderShowsTheSortArrowOnlyOnTheSortedColumn`) |
| M27 | không vẽ huy hiệu | chết bước 1 (lần đầu lỗi biên dịch do analyzer; làm lại dạng `&& badge.Length > 100`) |
| M28 | hàng chọn không tô màu chọn | chết bước 1 |
| M29 | IDCANCEL: OnCancel đảo ngược | chết bước 1 (25 ca, cả bộ GĐ1) |
| M30 | lớp ListView không có tên trong template | chết bước 1 (27 ca) |
| M31 | Import không thay danh sách | chết bước 1 |
| M32 | Apply hợp lệ bị coi là không hợp lệ | chết bước 1 |

Tổng: 32 chạy; 31 chết ở bước 1; 1 tương đương (M14). Còn sống thật: 0. Đột biến chạy trên commit f084fc96; sau đó chỉ
đổi progress sang chủ đề (M04 chạy lại trên mã cuối, chết) và bố cục dòng trạng thái (không có đột biến riêng: `Layout_AtTwoScales...` phủ).

## 6. Nhìn bằng mắt và phần CHƯA kiểm được bằng máy

**Đã nhìn bằng mắt** (ảnh PNG do `NativeDialogSnapshot*` ghi bằng `PrintWindow` trên desktop ẩn, có comctl32 v6 như exe thật):
ảnh `gallery*.png`, `actionprofiles-{en,vi}[-150]`, `benchmark-{en,vi}[-sorted|-150]`, danh sách thả `gallery-combo-list`):
- ListView: nền `#171717`, hàng xen kẽ `Dark.Panel`/`Dark.RowAlt`, hàng tốt nhất xanh, hàng lỗi đỏ, header `Dark.Surface` chữ sáng + mũi tên sắp xếp; đọc
  được ở 100 % và 150 %, tiếng Việt không bị cắt (cột chia theo dialog-unit).
- ComboBox: ô chọn tối viền sáng + mũi tên; danh sách thả tối chữ sáng (dòng chọn màu hệ thống, xem phần chưa kiểm).
- Listbox đa chọn owner-draw: chọn `Dark.Selection`, huy hiệu viên thuốc, không chồng chữ ở 150 % sau khi đặt chiều cao dòng theo DPI.
- Progress (vạch trắng trên nền tối), trackbar (đường sáng + núm xanh), edit nhiều dòng có thanh cuộn tối, checkbox xanh, radio/group box chữ sáng.
- Sửa nhờ ảnh chụp (bốn lần): chữ radio/group box xám gần vô hình -> dáng cổ điển; header listview chữ xám -> tự vẽ; progress cổ điển có khung 3D
  trắng chói -> chủ đề; hàng huy hiệu chồng chữ ở 150 % -> `LB_SETITEMHEIGHT` theo DPI.

**Chưa kiểm được bằng máy / mắt** (ghi để người dùng kiểm tay):
- Title bar tối và DPI hai màn hình thật: như GĐ1 (ảnh chụp trên desktop ẩn không có DWM tổng hợp; chỉ mô phỏng `WM_DPICHANGED`).
- Danh sách thả của combobox được chụp riêng (cửa sổ `ComboLBox`): nền tối, chữ sáng, dòng chọn dùng MÀU CHỌN HỆ THỐNG (xanh), không phải `Dark.Selection`.
  Muốn đồng nhất cần owner-draw combo (CBS_OWNERDRAWFIXED; khung vẽ `ListBoxControl.DrawItem` đã dùng lại được): chưa làm.
- Progress dùng chủ đề `DarkMode_Explorer` (nền tối, vạch trắng); không đặt màu nhấn (PBM_SETBARCOLOR + chủ đề cổ điển vẽ khung 3D trắng chói).
- Trợ năng thật (Narrator/UIA): ô thư mục Benchmark không có nhãn đứng ngay trước (WPF dùng `AutomationProperties.Name`): chỉ có tooltip, UIA lấy tên từ
  đoạn mô tả đứng trước; chưa chạy Narrator. Dòng trạng thái không phải live region (như GĐ1).
- Ký hiệu cúp ở cột đầu (🏆) vẽ bằng font hệ thống: ra glyph đơn sắc, không phải emoji màu như WPF.
- Trackbar và edit nhiều dòng chưa dùng ở cửa sổ nào (dựng sẵn cho Settings): được test + ảnh gallery, chưa thấy trong ngữ cảnh thật.
- Chạy tay trên desktop tương tác (R2R và AOT): chưa; chỉ tiến trình thật trên desktop ẩn.

## 7. Công sức so với ước và điều kiện dừng của spike

Điều kiện dừng (dark của ListView/ComboBox không chấp nhận được, hoặc công > 1,5 lần ước 28-42 giờ): **chưa kích hoạt**. Ảnh chụp cho thấy listview (header tối,
hàng tô màu, sắp xếp), combobox (ô + danh sách thả), listbox đa chọn, progress, trackbar, group box/radio đều đọc được; combobox/listview cần thêm mã riêng
(vẽ header, tô hàng, chủ đề cổ điển cho radio/group box) nhưng trong phạm vi khung. Quy mô: ~2 600 dòng `src/` mới (2 900 dòng test).

## 8. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

- CHƯA XÁC NHẬN: tiếp tục phương án B sau GĐ2; GĐ3 (Settings theo trang, phím tắt cuối) và GĐ4 (Recovery) giữ cầu WPF/cổng tạm.
- Thêm so với bản WPF (chỉ view): huy hiệu (phím tắt ở Action profiles, chế độ nạp ở Benchmark), Esc huỷ lượt đo trước khi đóng, nút thu nhỏ/phóng to,
  sắp xếp header giữ như DataGrid (tăng -> giảm). Muốn bỏ: sửa `ListItemText` / `OnCancel` / `ExtraWindowStyle`.
- Phương án thay: (D) `PhotoReview.Settings.exe` đồng hành nếu GĐ3 vượt ngân sách; (C) giữ cầu WPF cho mọi cửa sổ (đổi `Supports` của `Win32SecondaryWindowPort`
  về false cho Benchmark/ActionProfiles).
- Hoàn tác: revert PR; hoặc chỉ trả Benchmark về cầu WPF bằng cách bỏ `SecondaryWindowKind.Benchmark` khỏi `Supports` (không đổi code khác).
