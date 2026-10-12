---
id: NOWPF-WP35-SPIKE-S3
order: 1010
summary: |-
  Spike S3 (chuẩn bị WP-35, NW-3): kiểm kê 8 cửa sổ phụ WPF (~3 600 dòng, 276 chuỗi `settings.*`), so sánh 6 phương án thay (tự vẽ D2D / hộp thoại Win32 / giữ cầu WPF / tiến trình đồng hành / WebView2 / Avalonia), prototype ZoomPrompt hai kiểu. Khuyến nghị (CHƯA XÁC NHẬN): hộp thoại Win32 gốc theo giai đoạn, sau bước tách presenter khỏi WPF; D là đường lùi rẻ.
---

# NOWPF-WP35-SPIKE-S3 - kiểm kê và chọn cách thay cửa sổ phụ WPF (2026-10-12)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); quyết định NW-3 trong [NO-WPF-MIGRATION-PLAN](NO-WPF-MIGRATION-PLAN.md).
Nền: [NOWPF-WP19B](NOWPF-WP19B-WPF-BRIDGE.md) (cầu WPF nạp muộn), [NOWPF-WP23](NOWPF-WP23-OVERLAY-HUD.md) (overlay D2D),
[NOWPF-WP32](NOWPF-WP32-AOT-SPIKE.md) (spike AOT), [NOWPF-WP36](NOWPF-WP36-PUBLISH-AOT.md) (artifact AOT không có cầu WPF).
Chỉ đọc + tài liệu + một prototype ném đi (không commit mã). Số đo ở máy đang 100 % CPU nên chỉ dùng để so cỡ, không làm mốc.

## 1. Kiểm kê (đo trên `src/PhotoReview.App.WpfWindows`, master 2026-10-12)

Cửa sổ "About" **không tồn tại** (phiên bản chỉ hiện ở chân `SettingsWindow` qua `BuildInfo.Describe`). Khung chung: `SecondaryWindowFactory` (94 dòng),
`Themes/*.xaml` (516 dòng: DarkControls, DarkMenus, DarkPalette, DarkScrollBars), `DarkTitleBarChrome` (34), `TrExtension` (31). Toàn bộ chuỗi đi qua `Tr`/`Localizer`
của Core (không phụ thuộc WPF); 955 khoá trong `en.json`, trong đó `settings.*` 276, `recovery.*` 103, `benchmark.*` 92, `dialog.*` 68, `diagnostics.*` 25,
`actionProfiles.*` 15, `batchReview.*` 6 (con số "222" của thẻ WP-35 là cũ).

| Cửa sổ | Mục đích | XAML + C# (dòng) | Điều khiển | Chuỗi | Modal / owner | Phụ thuộc | Test hiện có |
|---|---|---|---|---|---|---|---|
| **SettingsWindow** | ~60 setting (`AppSettings` có 70 thuộc tính), 7 trang | 469 + 1257 = **1 726** | CheckBox 45, ComboBox 16, Slider 2, TextBox 39 (28 ô bắt phím tắt + số + đường dẫn + JSON nhiều dòng), Button 29, RadioButton 2, ListBox điều hướng, ScrollViewer 7, GroupBox 24, ~18 CheckBox dựng bằng mã (menu chuột phải) | 276 `settings.*` + `enum.*` + `update.*` | Modal (`ShowDialog`), trả `bool`; mở lồng ActionProfiles + OpenFile/SaveFile/MessageBox | `SettingsStore`, `AppSettings.Clone` (JSON), `Localizer.CurrentChanged` (đổi ngôn ngữ sống), `IUpdateChecker`, `IImageDecoderFactory`, `WicCodecAvailability`, đăng ký Explorer (HKCU), `RamBudgetPolicy` | 12 file `SettingsWindow*` (2 519 dòng, 77 ca): FieldMap, RoundTrip, Defaults, Import, Redesign, ContextMenu, AutoHide, RamCacheSlider, JournalDurability, UpdateCheck, WebpHeic, LanguageRefresh; +`AccessibilityNamesTests`, `WindowLocalizationTests` |
| **RecoveryWindow** (+`RecoveryPathPanel`, `RecoveryPresenter`) | Danh sách journal dở dang, kiểm tra nền, thử lại/xoá | 82+363 + 16+64 + 249 = **774** | ListBox chọn nhiều + item template (huy hiệu), ComboBox lọc, ScrollViewer chi tiết, ItemsControl thành viên nhóm, TextBlock 20, Button 7 | 103 `recovery.*` + `dialog.confirm*` | Modal; chạy `RecoveryFileCheck` nền + `Retry` (thao tác tệp thật) | `IJournal`/callbacks retry+dismiss, `IFileSystem`, Explorer | 7 file (1 135 dòng, 55 ca): RecoveryWindowTests, GapTests, ListOps (manual) |
| **BenchmarkWindow** | Chạy workload nạp ảnh, bảng kết quả | 135 + 435 = **570** | DataGrid 10 cột (hàng tô màu theo trạng thái), ListBox chọn nhiều + tooltip, ProgressBar, TextBox 2, Button 9 | 92 `benchmark.*` | **Modeless** (`Show`, phím qua `IRawMessageFilter`), đóng giữa chừng phải huỷ | `PhotoReview.Benchmarking`, OpenFolderDialog, Explorer | 5 file `BenchmarkWindow*` (+13 file benchmark khác không phải cửa sổ) |
| **ActionProfilesWindow** | Sửa danh sách `ReviewAction` | 36 + 198 = **234** | ListBox, TextBox 3, ComboBox 1 (3 mục theo chỉ số), CheckBox, Button 6 | 15 + `dialog.*` | Modal, **lồng trong Settings** | OpenFile/SaveFile JSON, MessageBox | 3 file (361 dòng, 20 ca) |
| **DiagnosticsWindow** | 15 cặp nhãn/giá trị chỉ đọc | 23 + 85 = **108** | TextBlock 32, Button 1 | 25 | Modal | `ReviewMetricsSnapshot`, `ExplorerViewSnapshot`; `ReasonText`/`StatusText` thuần (tái dùng được) | `DiagnosticsUnitsLeftoversTests` + vài tham chiếu |
| **BatchReviewWindow** | Xác nhận danh sách tệp (trả `bool`) | 9 + 32 = **41** | ListBox, Button 2 | 6 | Modal | `BatchReviewItem` | 5 file tham chiếu (10 chỗ) |
| **SkippedFilesWindow** | Danh sách tệp bị bỏ qua (dựng bằng mã) | **67** | ListBox, TextBlock, Button | ~5 | Modal | `SkippedEntry` | 2 file |
| **ClickZoomCustomDialog** | Một ô số 10..800 + Apply/Cancel | 25 + 40 = **65** | TextBox 1, TextBlock 2, Button 2 | 4 | Modal | `IZoomPromptService` | `ClickZoomCustomDialogTests`, `ZoomPromptSeamTests` |
| **Tổng** | | **~3 585** (+ 516 theme, 125 khung) | | ~520 khoá | 1 modeless, 7 modal | | ~175 ca, **không có golden điểm ảnh** cho cửa sổ phụ |

Điểm quan trọng cho mọi phương án:

1. **Logic nằm trong code-behind.** `SettingsWindow.xaml.cs` trộn đọc/ghi `AppSettings`, kiểm tra hợp lệ, cảnh báo trùng phím tắt, ước lượng RAM, import/export với
   `TextBox`/`Slider` (`LoadFields` ~125 dòng, `Save_Click` ~220 dòng). Test hiện đọc cây điều khiển (`ShortcutBoxes`, `FindName`). Không thể thay view mà không tách
   presenter trước.
2. **Phần dữ liệu đã không-WPF**: `SettingsStore`/`SettingsNormalizer`/`SettingsValidator`/`AppSettingsJsonContext` (Core, AOT-sẵn), `ShortcutKeyName` + `KeyId`
   (App.Shared), `Tr` (Core). Rủi ro ghi config nằm ở ánh xạ trường-điều khiển, không ở lưu trữ (store đã có sao lưu bản mới hơn C-04, retry đọc, giữ file hỏng).
3. **Thiếu hạ tầng ở shell**: chưa có `IFileOpenDialog`/`IFileSaveDialog` (chỉ `NativeShellFolderDialog`); `TaskDialogIndirect` thay `MessageBox` đã có (WP-19a).
4. **Overlay hiện có không phải bộ control.** `Shell.Win32/Overlay` chỉ có `PanelElement` (xếp dọc/ngang), `TextElement`, `ButtonElement` + `DarkPalette`/`Animator`,
   vẽ vào *bề mặt cửa sổ chính*; không có hộp nhập, checkbox, slider, combobox, list, scroll, focus/Tab, lưới nhãn-cột.
5. Mục tiêu hiệu năng của thẻ WP-19b: mở Settings <= 150 ms (WPF hiện 170-215 ms warm, 470-690 ms lần đầu).

## 2. Prototype (scratchpad `spikes3-proto`, không commit)

Cùng một hộp thoại ZoomPrompt (nhãn, ô số, thông báo lỗi 10..800, Apply/Cancel, Enter/Esc/Tab, title bar tối, owner bị khoá) dựng hai cách, chạy tự động
(gửi `WM_CHAR`/`WM_KEYDOWN`: nhập 9999 -> lỗi hiện -> xoá -> 200 -> Enter -> kết quả 200 ở cả hai).

| | A: tự vẽ trên D2D (dùng `OverlayHost`/`PanelElement`/`ButtonElement`/`DarkPalette` thật + `D2DRenderSurfaceFactory.CreateForWindow`) | B: Win32 gốc (`DialogBoxIndirectParam` + DLGTEMPLATE trong bộ nhớ) |
|---|---|---|
| Mã | **329 dòng**: `TextBoxElement` 121 (caret, chọn, kéo chuột, Backspace/Delete/Home/End/Ctrl+A/C/V) + cửa sổ/vòng modal/DPI/focus 208; **+102** interop dùng chung | **138 dòng**: dựng template 35 + DlgProc 70 (+ cùng interop dùng chung) |
| Công (ước, một lượt viết + 2 lỗi biên dịch) | ~1 giờ | ~20 phút |
| Mở tới khung đầu (máy 100 % CPU, chưa R2R, JIT nguội) | 1,0 / 3,0 / 5,2 s (gồm tạo thiết bị D3D/D2D/DWrite) | 76 / 142 / 164 / 223 ms |
| Tối | Đúng palette app (`Dark.Window/Input/Accent`), đồng bộ overlay | Title bar `DWMWA_USE_IMMERSIVE_DARK_MODE`, nút/ô nhập tối bằng `SetWindowTheme("DarkMode_Explorer")` + `WM_CTLCOLOR*` + uxtheme ordinal 135 (shell đã dùng ở `DarkModeMenus`); ô nhập phẳng không viền, nút viền trắng khi focus: gần nhưng không đồng bộ |
| DPI | `surface.DpiScale` đúng; **chưa xử lý `WM_DPICHANGED`** (phải tự co cửa sổ) | Hệ thống tự co đơn vị hộp thoại theo font/DPI |
| Nhập văn bản | Chỉ ký tự `WM_CHAR`; **không IME/TSF** (gõ Telex tên action, đường dẫn tiếng Việt sẽ hỏng), không undo, không double-click chọn từ, không cuộn ngang | Miễn phí (IME, clipboard, undo, caret, chọn từ) |
| Focus / Tab / Enter / Esc | Tự viết (15 dòng cho 3 phần tử; Settings cần bộ quản lý focus ~200 dòng) | Dialog manager lo |
| UIA / đọc màn hình | **Không có**: Narrator thấy cửa sổ rỗng (phải viết provider COM) | Miễn phí (nhãn = static liền trước) |
| Vấn đề gặp | `ITextLayout` (C-11 đã đóng băng) không có `HitTestPoint`/số đo caret nên đo bằng cách tạo layout tiền tố O(n²); vẽ lại mỗi `WM_MOUSEMOVE`; mỗi cửa sổ một swap chain + thiết bị | Dựng template bằng tay dễ lệch căn (đơn vị dialog); không có ô nhập có viền tối chuẩn; để Settings 200 control cần DSL bố cục; không scroll sẵn |

Kết luận prototype: A không khó **cho một hộp thoại**, nhưng toàn bộ chi phí nằm ở thứ prototype *không làm*: IME, UIA, cuộn, combobox popup, list đa chọn, focus
toàn cục. B rẻ hơn ~3 lần ở độ phức tạp tối thiểu và cho IME/UIA/DPI miễn phí, đổi lại độ khớp giao diện kém hơn.

## 3. Phương án

Ước giờ gồm cả test chạy được trên máy tối đa 1 luồng, không gồm chờ phản hồi. "Hạ tầng" = bộ control/khung dùng chung. Dòng "Tách presenter" và "Viết lại test" là việc
chung cho A/B/E (xem mục 4 giai đoạn 0).

| Cửa sổ (giờ) | A tự vẽ D2D | B Win32 gốc | D tiến trình đồng hành | E WebView2 | F Avalonia (NW-3 b gốc) |
|---|---|---|---|---|---|
| Hạ tầng | 110-160 | 40-60 | 20-30 (pipe/theo dõi config, đóng gói) | 30-45 (host, cầu JSON, tiêm chuỗi, CSS tối) | 25-40 |
| ZoomPrompt | 6-10 | 3-4 | gộp | 3-5 | 4-6 |
| Diagnostics | 6-10 | 3-5 | gộp | 5-8 | 5-8 |
| SkippedFiles + BatchReview | 10-16 | 4-6 | gộp | 6-10 | 6-10 |
| ActionProfiles | 16-24 | 8-12 | gộp | 10-16 | 10-16 |
| Benchmark (modeless, lưới) | 30-45 | 20-30 | gộp | 15-25 | 15-25 |
| Recovery | 40-60 | 30-45 | gộp (+10-15 RPC cho retry/dismiss) | 25-40 | 25-40 |
| Settings | 70-110 | 45-70 | gộp | 40-60 | 35-55 |
| **Cộng view** | **~290-435** | **~153-232** | **~30-50 (+10-15)** | **~134-209** | **~125-200** |
| Tách presenter (giai đoạn 0) | 25-40 | 25-40 | 0 | 25-40 | 25-40 |
| Viết lại test/parity | 40-60 | 30-50 | 10-15 (IPC) | 40-60 | 30-45 |
| **Tổng** | **~355-535** | **~210-320** | **~50-80** | **~200-310** | **~180-285** |

Thẻ WP-35 ghi 120-200 giờ: chỉ B (phần view 150-230) tới gần cận trên; A vượt hơn gấp đôi.

| Tiêu chí | A tự vẽ D2D | B Win32 gốc | C giữ cầu WPF (hiện tại) | D `PhotoReview.Settings.exe` | E WebView2 | F Avalonia |
|---|---|---|---|---|---|---|
| Ưu | Đồng bộ palette/dark/DPI với overlay; AOT thuần; kiểm thử điểm ảnh G-PIX; không phụ thuộc ngoài | Ít mã nhất; IME/UIA/clipboard/DPI/Tab miễn phí; AOT thuần; Narrator chạy; test điều khiển bằng `SendMessage`; cơ chế dark đã dùng ở menu | 0 giờ; hành vi y nguyên 175 test | Shell AOT sạch; giữ nguyên 3 600 dòng WPF + test; thay rất rẻ | Giao diện tự do; UIA/IME miễn phí (Chromium); HTML dễ chỉnh | XAML gần WPF, port nhanh; AOT được; test headless |
| Nhược | Phải tự viết IME/TSF + UIA + cuộn + combobox popup + list + focus (>= 110 giờ hạ tầng); mở rộng C-11 `ITextLayout`; regress trợ năng so với WPF (đang có test tên UIA) | Không đồng bộ giao diện với overlay (control Windows chuẩn); dark không đủ ở combobox/tab/listview header; layout bằng đơn vị dialog + DSL tự viết; không cuộn sẵn; test UI cũ phải viết lại | Không AOT làm mặc định; giữ `UseWPF` 4 project + `WindowsDesktop`; WP-37 không xoá được WPF | Vẫn cần .NET Desktop Runtime (mất lợi "không cần cài .NET" khi mở Settings) hoặc self-contained 70-150 MB; khởi động ~0,6-1 s mỗi lần (không warm); hai tiến trình cùng ghi config; **WPF còn nguyên** | Phụ thuộc runtime Evergreen (Win10 LTSC/offline có thể thiếu); RAM +100-200 MB lúc mở; khởi động lạnh 0,4-0,9 s (ước); wrapper .NET của WebView2 chưa kiểm AOT; test không headless; 2 ngôn ngữ JS+C# | +20-35 MB (Skia), hai hệ UI trong tiến trình; vòng lặp thông điệp riêng cạnh `MessageLoop`; chưa kiểm AOT/IME/UIA trong shell này |
| Kích thước gói | +0,3-0,8 MB trong exe AOT (11 MB) | +0,2-0,5 MB | +20 MB WindowsDesktop (hiện) | +0,2 MB (FDD) hoặc +70-150 MB | +0,2 MB loader + wrapper ~1 MB + HTML ~0,3 MB | +20-35 MB |
| Mở cửa sổ | 100-300 ms ước (đo prototype 1-5 s gồm JIT+device, cần đo lại trong shell R2R/AOT) | 75-220 ms đo (nguội, 100 % CPU) | 470-690 ms lần đầu, 170-215 ms sau | ~600-1 000 ms mỗi lần | 400-900 ms lạnh | 300-600 ms (ước) |
| Khởi động shell | không đổi | không đổi | +1,9 % (WP-19b) | không đổi | không đổi | không đổi |
| Rủi ro dữ liệu (config) | Ánh xạ ~60 trường tay: giảm bằng presenter + FieldMap test | Như A | Không đổi | Hai tiến trình ghi `config.json`: cần quy tắc một người ghi + nạp lại | Lược đồ JSON JS<->C# lệch; validate lại ở C# | Như A |
| Rủi ro Recovery | Cao (UI tự viết cho thao tác tệp thật) | Trung bình | Không | Cao (retry cần RPC ngược về shell) | Trung bình | Trung bình |
| Trợ năng | **Mất** UIA/IME nếu không đầu tư lớn | Giữ (chuẩn) | Giữ | Giữ | Giữ | UIA có, IME cần kiểm |
| Kiểm thử | Mô hình cây + golden G-PIX tốt, nhưng không kiểm trợ năng | HWND thật trên desktop ẩn (hạ tầng `ShellRun` có sẵn) | Có sẵn | Giữ test WPF + test IPC | Cần harness trình duyệt | Headless tốt |
| AOT thuần | Có | Có | Không | Có (shell) | Cần kiểm wrapper | Có (có cảnh báo) |
| Cho phép WP-37 xoá WPF | Có (hoàn toàn) | Có (hoàn toàn) | **Không** | Một phần (xoá `PhotoReview.App`, giữ App.WpfWindows) | Có | Có (đổi sang Avalonia) |

Ghi chú về D' (AOT tự nạp CoreCLR qua `hostfxr` để dùng WPF trong tiến trình): về kỹ thuật được nhưng gom đủ rủi ro của C và D, đã bị loại ở NE-5; không xét tiếp.

## 4. Khuyến nghị (CHƯA XÁC NHẬN)

**Chọn B (hộp thoại Win32 gốc), làm theo giai đoạn, với D là đường lùi rẻ và C là lưới an toàn tới khi xong.** Căn cứ:

1. Mục tiêu của dự án là bỏ WPF và AOT làm mặc định: chỉ A/B/E/F cho phép WP-37 xoá hoàn toàn; trong đó B có tổng giờ thấp nhất (210-320 so với 355-535 của A) và
   là phương án duy nhất trong tầm 120-200 giờ của thẻ nếu tính riêng phần view.
2. App coi trợ năng là yêu cầu (có `AccessibilityNamesTests`, `AutomationProperties.LabeledBy` ở từng ô): A làm mất UIA/IME, B giữ nguyên miễn phí. Tên action và đường dẫn
   tiếng Việt cần IME.
3. Độ khớp giao diện là nhược điểm của B nhưng nhỏ: cơ chế dark (uxtheme 135, `DarkMode_Explorer`) đã chấp nhận ở menu native (NE-4 a); cửa sổ Settings không cần giống overlay.
4. Prototype cho thấy B làm đúng ở lượt viết đầu; A đòi hỏi mở rộng hợp đồng C-11 và hạ tầng lớn chưa thấy ở đâu trong kế hoạch.
5. D rất rẻ (50-80 giờ) nên giữ làm phương án nếu giai đoạn 1 vượt ngân sách: nó vẫn cho AOT làm mặc định, chỉ không xoá được WPF khỏi kho.

### Lộ trình

| GĐ | Việc | Giờ | Cổng chấp nhận |
|---|---|---|---|
| 0 | **Tách presenter khỏi WPF** (vẫn trên WPF, không đổi hành vi): `SettingsPresenter` (LoadFields/Save/Defaults/Import/Export/duyệt trùng phím/RAM), `RecoveryPresenter` (đã có một phần), `BenchmarkRunner`, `ActionProfilesModel`, các `*ViewModel` chỉ-đọc cho Diagnostics/Skipped/Batch vào App.Shared (không WPF); test cũ trỏ vào presenter | 25-40 | Toàn bộ 175 ca cũ xanh; `SettingsWindowFieldMapTests` kiểm "mọi thuộc tính `AppSettings` được ánh xạ hoặc nằm trong danh sách loại trừ"; luật kiến trúc: App.Shared không `System.Windows` |
| 1 | Bộ khung Win32 (`Shell.Win32/Dialogs/Forms`: dựng template + DSL bố cục, dark, DPI, modal, owner, `IFileOpenDialog`/`IFileSaveDialog`, vòng lặp modeless qua `IRawMessageFilter`/`IsDialogMessage`) + **ZoomPrompt, Diagnostics, SkippedFiles, BatchReview** | 35-50 | Hiện đúng 4 hộp thoại trên bản AOT; không nạp `PresentationFramework` (probe WP-19b); mở <= 150 ms; test HWND cho từng nút/Enter/Esc; ảnh chụp dark duyệt bằng mắt |
| 2 | **ActionProfiles + Benchmark** (modeless, ListView báo cáo 10 cột, ProgressBar, huỷ giữa chừng) | 28-42 | 20 ca ActionProfiles + 5 file BenchmarkWindow* tương đương; đóng giữa lúc chạy không treo (`BenchmarkWindowCloseMidRunTests`) |
| 3 | **Settings theo trang**: Chung -> Hiển thị -> Chuột -> Hiệu năng -> Tệp -> Chẩn đoán -> **Phím tắt cuối** (28 ô bắt phím = EDIT subclass `WM_KEYDOWN`, trùng phím cảnh báo) | 70-110 | 77 ca SettingsWindow* tương đương; FieldMap/RoundTrip/Defaults/Import xanh trên view mới; đổi ngôn ngữ sống; `config.json` sau Lưu không mất trường lạ (clone JSON) |
| 4 | **Recovery cuối** (an toàn dữ liệu): ListView + panel chi tiết, xác nhận trước mọi thao tác huỷ/xoá | 35-50 | 55 ca Recovery* tương đương + kiểm tay danh sách cũ trên bản sao journal; hai bản phát hành liên tiếp không lỗi |
| 5 | Đổi mặc định sang AOT (sửa `release.yml`, WP-30), WP-37 xoá `App.WpfWindows`/`WpfBridge`/`UseWPF` | 8-12 | Đủ điều kiện WP-37 (>= 2 bản ổn định + người dùng đồng ý) |

Trong suốt GĐ 1-4 bản hybrid giữ cầu WPF (C): cửa sổ nào chưa chuyển vẫn mở bằng cầu; `SecondaryWindowPorts` chọn theo từng cửa sổ (đã có cổng `ISecondaryWindowPort` 7 hàm).
Bản AOT tạm thời có thể làm cổng "chưa có" tối thiểu (3-4 giờ, chỉ ở artifact AOT): Settings -> mở `config.json` bằng trình soạn thảo mặc định + thông báo.

### Điều kiện dừng / đổi hướng
- Sau GĐ 1: nếu công thực tế > 1,5 lần ước (> 75 giờ cho 50) hoặc người dùng thấy dark của combobox/listview không chấp nhận được -> dừng, chuyển Settings + Recovery sang D
  (50-80 giờ) hoặc dùng owner-draw riêng cho control sai màu.
- Nếu người dùng chấp nhận yêu cầu .NET Desktop Runtime cho cửa sổ phụ (hoặc gói self-contained) -> D thắng ngay (rẻ hơn 3-5 lần), bỏ GĐ 2-4.
- Không làm Recovery (GĐ 4) khi `RecoveryWindow*` chưa có tương đương xanh; không xoá cầu WPF trước điều kiện WP-37.
- Không chọn A trừ khi có yêu cầu mới bắt buộc giao diện đồng nhất tuyệt đối và chấp nhận 355-535 giờ.

### Việc chưa kiểm (cho GĐ 1)
Bố cục hộp thoại 7 trang (side list + trang con `DS_CONTROL` có cuộn), `ComboBox`/`Trackbar`/`ListView` header ở chế độ tối, hành vi ở 125/150/200 % DPI nhiều màn hình,
`IsDialogMessage` cho Benchmark modeless trong `MessageLoop` của shell, độ rộng chuỗi tiếng Việt dài trong ô dialog-unit, thời gian mở trong shell R2R/AOT thật.

## 5. Hoàn tác
Tài liệu thuần; không đổi mã sản phẩm. Dòng `WP35-NW3` trong [NOWPF-UNCONFIRMED-DECISIONS-20261010](NOWPF-UNCONFIRMED-DECISIONS-20261010.md) ghi phương án đề xuất,
phương án thay và cách đổi hướng. Prototype nằm ở scratchpad (tạm), có thể tái dựng từ mô tả mục 2.
