---
id: NOWPF-WP35-G3A
order: 1130
summary: |-
  WP-35 GĐ3a (phương án B của spike S3, CHƯA XÁC NHẬN): KHUNG cửa sổ Settings Win32 gốc + hợp đồng trang `ISettingsPageView` + builder bố cục theo dialog-unit + 3 trang đầu (General, Display, Diagnostics = 20 thuộc tính). Trang tạo điều khiển LƯỜI (chỉ trang đang mở), cuộn dọc, kéo giãn, Save/Apply/Cancel/Defaults/Reset trang/Import/Export, nhớ trang cuối, Ctrl+Tab, đổi ngôn ngữ làm mọi chữ đổi theo; 4 trang còn lại hiện ghi chú và GIỮ nguyên giá trị (Save không làm mất). Cổng Win32 chỉ nhận Settings khi đủ mọi trang hoặc cờ ẩn PHOTOREVIEW_SETTINGS_WIN32=1; mặc định vẫn cầu WPF (hybrid) / cổng tạm (AOT). 0 cảnh báo ILC; không nạp WPF; 29 đột biến, 0 sống thật; mở Settings đo được 68-80 ms (lượt yên nhất) nhưng máy bị chiếm 100 % CPU nên cổng <= 150 ms CHƯA xác nhận lúc yên (xem mục 4b).
---

# NOWPF-WP35-G3A - khung Settings Win32 + hợp đồng trang + 3 trang đầu (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiếp [NOWPF-WP35-G2-WIN32-DIALOGS](NOWPF-WP35-G2-WIN32-DIALOGS.md) theo lộ trình
GĐ0-5 của [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md). Presenter/FieldMap KHÔNG đổi ([NOWPF-WP35-G0-PRESENTERS](NOWPF-WP35-G0-PRESENTERS.md)): view Win32 chỉ chép
giá trị giữa điều khiển và `SettingsFormState`. Bản WPF (`App.WpfWindows/SettingsWindow`) và cầu WPF giữ nguyên (lưới an toàn).

## 1. Đã làm

| Phần | File (`src/`) | Nội dung |
|---|---|---|
| Khung | `Shell.Win32/Dialogs/SettingsUi/SettingsDialog.cs` | modal, kéo giãn; cột trái = tiêu đề + danh sách điều hướng (owner-draw) + phiên bản; cột phải = tiêu đề trang + cửa sổ chứa trang CUỘN DỌC (thanh cuộn riêng, con lăn, `EnsureVisible` theo focus/lỗi); hàng nút Defaults / Reset trang / Export / Import (trái) và Cancel / Apply / Save (phải; hẹp thì hai hàng); Save = IDOK (Enter), Esc/X = Cancel; nhớ trang (`SettingsPresenter.RememberPage`); Ctrl+Tab đổi trang qua `OnDialogKey`; `SettingsTarget.ExternalEditor` mở trang Files mà không đổi trang được nhớ; `Saved` = có ít nhất một Save/Apply thành công (cầu WPF/`ShowSettings` trả bool này) |
| Hợp đồng | `.../ISettingsPageView.cs` | `Key`, `Title`, `Declare(builder)`, `Attach(context)`, `Load(form)`, `Collect(form)`, `OnCommand`, `OnScroll`, `OnLanguageChanged`, `FocusTarget(SettingsFocusField)`, `OnClosed`; `SettingsPageBase` cho mặc định |
| Builder | `.../SettingsPageBuilder.cs` | `Group`, `Check`, `Combo`, `Edit`, `Slider`, `Note`, `Text`, `Buttons`, `CheckFlow`: khai báo theo thứ tự đọc (= thứ tự Tab); bố cục theo dialog-unit + ĐO chữ nhiều dòng thật (tiếng Việt dài không bị cắt), nhãn 38 % bề rộng, nhóm có đệm, ẩn/hiện phần tử |
| Ngữ cảnh | `.../SettingsPageContext.cs` | mặt tiền mỏng cho trang: chữ/tick/combobox/thanh trượt/bật-tắt/ẩn-hiện/focus/cảnh báo/Explorer/hàng đợi UI; trang không chạm HWND |
| Danh mục | `.../SettingsPageCatalog.cs` | MỘT dòng mỗi trang; trang thiếu = `PendingSettingsPage` (ghi chú, không đọc/ghi); `IsComplete` khi đủ 7 trang |
| Trang | `.../Pages/GeneralSettingsPage.cs` | UiLanguage (combobox + mở thư mục/nạp lại/xuất bản dịch), ImageSortMode, InitialViewMode, InstanceMode; kiểm tra cập nhật thủ công (bất đồng bộ), tích hợp menu Explorer |
| Trang | `.../Pages/DisplaySettingsPage.cs` | ShowInfoOverlay, ShowFileInfo, ShowFolderInfo, InfoOverlayFontSize, ShowZoomIndicator, ToolbarAutoHide(+DelayMs), InfoOverlayAutoHide(+DelayMs), ToolbarOpacityPercent (thanh trượt), LoadingMode, ScalingQuality, ShowExifInfo, ExifInfoFields (10 ô), TitleBarFields (14 ô) |
| Trang | `.../Pages/DiagnosticsSettingsPage.cs` | LoggingEnabled + mở vị trí file log |
| Interop | `Shell.Interop/Win32/SettingsDialogNative.cs` | `SetParent`, `SetScrollInfo`/`GetScrollInfo`, `GetKeyState`, `ScrollInfo` (28 byte) |
| Khung chung | `Dialogs/Native/NativeDialog.cs` | `AddChildHost` + `Item` tra xuyên cửa sổ chứa, `RegisterChild`/`ApplyChrome` (điều khiển tạo sau), hook `OnWindowMessage` (thông điệp ngoài khung) và `OnDialogKey` (phím trước `IsDialogMessage`) |
| Khung chung | `Dialogs/Native/NativeDialogChrome.cs`, `DialogTemplate.cs` | `INativeDialogChrome.ApplyControls` (chủ đề cho điều khiển tạo sau, mặc định = `Apply`), scrollbar có chủ đề tối, `DialogTemplate.WindowClassName` |
| Cổng | `Dialogs/Native/Win32SecondaryWindowPort.cs` | `Supports(Settings)` = đủ trang HOẶC `PHOTOREVIEW_SETTINGS_WIN32=1`; `ShowSettings` mở `SettingsDialog` từ `SettingsStore`/`IImageDecoderFactory`/`LocalizationService`/`IUpdateChecker`/`IShellFileDialog` của DI |
| Chuỗi | `Core/Localization/Languages/{en,vi,en.notes}.json` | THÊM Ở CUỐI: `settings.resetPage`, `settings.pagePending` (không đổi khoá cũ) |

## 2. Thiết kế

1. **Trang = cửa sổ chứa con có thanh cuộn riêng.** Mỗi trang có một cửa sổ chứa (STATIC, `WS_CLIPCHILDREN`, `WS_EX_CONTROLPARENT`) trong template; điều khiển của trang là con
   của nó nên bị cắt đúng vùng nhìn và Tab của `IsDialogMessage` đi xuyên vào. Cửa sổ chứa không có DlgProc nên subclass chuyển lên hộp thoại mọi thông điệp mà điều khiển con gửi
   "cho cha" (WM_COMMAND/NOTIFY/DRAWITEM/CTLCOLOR*/HSCROLL/VSCROLL) -> toàn bộ máy móc của `NativeDialog` (màu tối, owner-draw, thanh trượt) dùng lại nguyên. Thanh cuộn là điều khiển
   SCROLLBAR riêng (chủ đề tối), không phải thanh cuộn phi-client. Con lăn: combobox/danh sách điều hướng/cửa sổ chứa chuyển lên khung (trừ combobox đang thả).
2. **Tạo điều khiển LƯỜI.** Template chỉ có khung + cửa sổ chứa; điều khiển của một trang được `CreateWindowEx` khi trang mở lần đầu (`EnsurePageCreated`): font + chủ đề tối + danh sách
   combobox + `Attach` + `Load` từ bản làm việc HIỆN TẠI (nên trang mở sau Defaults/Import vẫn đúng). Lý do: tạo 190 điều khiển một lần tốn ~65 ms (0,35 ms/điều khiển) và 7 trang
   đủ ~600 điều khiển sẽ phá cổng "mở <= 150 ms". Chữ lấy lại từ `Func<string>` lúc tạo (ngôn ngữ có thể đã đổi).
3. **Trang chưa chuyển không mất giá trị.** `CollectAll` bắt đầu từ `presenter.LoadFields()` (giá trị đã nạp của bản làm việc) rồi mỗi trang ĐÃ TẠO ghi đè phần của nó; trang chưa tạo hay chưa
   có bản Win32 giữ nguyên nên `TrySave` ghi lại đúng giá trị cũ (ca `AnUnportedPage_ShowsANotice_AndSaveKeepsItsSettingsUnchanged`, `SavingWithoutVisitingAPage_KeepsThatPagesValues`).
4. **Thứ tự mục combobox là hợp đồng** với `SettingsFormState` (chỉ số): trang khai báo đúng thứ tự mục của XAML; ca `*_ComboOrder*`/`*_EveryTagRoundTrips*` ghim.
5. **Reset trang** = giá trị mặc định built-in của CHỈ trang đang hiện, tính trên một `SettingsPresenter` tạm (bản sao) rồi `page.Load(...)`: bản làm việc đổi chỉ khi Save/Apply.
   **Defaults** giữ hành vi WPF (`presenter.ApplyDefaults()` + nạp lại mọi trang đã tạo). **Apply** = Save không đóng; Cancel sau Apply vẫn báo `Saved`.
6. **Ngôn ngữ**: `Localizer.CurrentChanged` (bất kỳ luồng nào) -> PostMessage `WM_APP+0x35` -> `RefreshLocalizedTexts` (tiêu đề cửa sổ, nhãn, nút, danh sách điều hướng, combobox tĩnh,
   `OnLanguageChanged` của trang, bố cục lại). `PostToUi` (hàng đợi + `WM_APP+0x36`) đưa kết quả kiểm tra cập nhật về UI thread mà không cần `SynchronizationContext`.
7. **Cổng**: Settings chỉ do cổng Win32 nhận khi `SettingsPageCatalog.IsComplete` hoặc cờ ẩn; bản hybrid giữ cầu WPF, bản AOT dùng cổng tạm (mở config.json). Router/`Win32DialogService` không đổi.
8. **Chữ**: nút/tick/group box xử lý `&` là phím tắt nên builder nhân đôi `&` ("Display & performance"); chuỗi mới thêm ở cuối catalog.

## 3. CÁCH THÊM MỘT TRANG (cho các agent làm song song)

Mỗi trang một file trong `src/PhotoReview.Shell.Win32/Dialogs/SettingsUi/Pages/` + ĐÚNG MỘT dòng trong `SettingsPageCatalog.Factories` (xếp chữ cái) + một khối test trong
`tests/PhotoReview.Shell.Tests/Dialogs/SettingsUi/SettingsPagesTests.cs`. KHÔNG sửa `SettingsDialog`, presenter, `SettingsFieldMap` (nếu thật sự thiếu: mở rộng FieldMap và ghi ở fragment của PR).

1. Đọc trang tương ứng trong `SettingsWindow.xaml` (+ code-behind: `LoadFields`/`ReadFields`/hàm `Update*Enabled`) và dòng của nó trong `SettingsFieldMap.Bindings` (thuộc tính nào, chỉ số combobox nào).
2. Tạo `XxxSettingsPage : SettingsPageBase`; `Key` phải khớp `SettingsPresenter.Pages` (`Mouse`, `Performance`, `Shortcuts`, `Files`).
3. `Declare(page)`: khai báo theo thứ tự đọc; lưu id trả về vào field. Ví dụ:

   ```csharp
   page.Group(() => Tr.SettingsGroupZoom, g =>
   {
       _clickZoomPercent = g.Edit(() => Tr.SettingsClickZoomPercentLabel, widthDlu: 45).Control;
       g.Note(() => Tr.SettingsClickZoomPercentHint);
       _middleClick = g.Combo(() => Tr.SettingsMiddleClickActionLabel, [() => Tr.EnumMiddleClickActionNone, /* ... đúng thứ tự XAML */]).Control;
       _keepZoom = g.Check(() => Tr.SettingsKeepZoomAcrossImagesLabel);
   });
   ```
4. `Load(state)`: `Context.SetText/SetChecked/SetComboIndex/SetSliderValue` từ `SettingsFormState` + gọi hàm cập nhật phụ thuộc (bật/tắt, chữ giá trị). `Collect(state)`: ngược lại, CHỈ thuộc tính của trang.
5. `OnCommand(id, notification)` (BN_CLICKED=0, CBN_SELCHANGE=1, EN_CHANGE=0x300; KHÔNG bắn khi đang `Load`), `OnScroll(id)` cho thanh trượt, `OnLanguageChanged()` để đặt lại chữ do mã đặt,
   `FocusTarget(SettingsFocusField)` để Save bị từ chối đưa focus về đúng ô.
6. Chữ: chỉ qua `Tr.*` trong `Func<string>`; chuỗi mới thì thêm ở CUỐI `en.json` + `vi.json` (+ `en.notes.json`).
7. Test: ca Load -> Collect -> Save cho MỖI thuộc tính (xem `Display_LoadsEveryProperty_...`), thứ tự combobox theo chỉ số, kiểm hợp lệ từng trường (`SettingsFocusField`), phụ thuộc bật/tắt; thêm thuộc tính
   vào mảng `*Properties` của `EveryPagesProperty_IsOwnedByTheFieldMap...`. Bố cục/Tab/rò handle/ngôn ngữ đã được các ca khung duyệt qua `SettingsPresenter.Pages`.
8. Khi đủ 7 trang `IsComplete` = true: đổi mặc định sang Win32 ở PR cuối (không cần cờ), cập nhật `LazyLoadTests`/`SecondaryWindowRoutingTests` (Settings thành cửa sổ Win32) và bỏ ghi chú trang chờ.

Ghi chú theo trang còn lại:

| Trang | Thuộc tính / điều khiển (WPF) | Lưu ý riêng |
|---|---|---|
| Mouse | ClickZoomPercent, ClickToZoomEnabled, ArrowPanStepPercent, KeyboardZoomAnchor, FitWidthAnchor/2, KeepZoomAcrossImages, KeyboardZoomStepPercent, MiddleClickAction (11 mục), ClickZoomKeyTogglesFit, MouseWheelAction, KineticPanEnabled, KineticGlideSmoothing, ArrowKeyNavigatesAtZoomEdge, ImageTransition(+Ms; ô Ms chỉ bật khi Fade), TouchpadSwipe(+Distance), HiddenContextMenuItems (18 ô dựng bằng mã từ `SettingsPresenter.ContextMenuRows()`, ô khoá có tooltip), tóm tắt phím zoom (`ZoomShortcutSummary`, phụ thuộc trang Shortcuts) | ~20 thuộc tính; ô tick con thụt vào; `FocusTarget` cho ArrowPanStep/KeyboardZoomStep/ImageTransitionMs/TouchpadSwipeDistance |
| Performance | DecoderBackend (mục TurboJpeg tắt khi `IsTurboJpegAvailable` = false: không có tooltip theo mục, dùng ghi chú), ImageCacheRamPercent (thanh trượt, khoảng đổi theo `RamCacheRange(forward, backward)` mỗi lần đổi ô preload), PreloadForward/BackwardCount, RawSupportEnabled, WebpHeicSupportEnabled (+ trạng thái codec), RawFullDecode, RawPairMode, CompareHash/Size | thanh trượt đổi khoảng: `Context.SetSliderRange`; chữ giá trị `RamCacheValueText` |
| Shortcuts | 27 ô bắt phím (`SettingsFieldMap.Shortcuts`), 9 nút xoá cho phím tuỳ chọn, cảnh báo trùng phím trực tiếp (`DuplicateShortcutWarning`), ActionsJson (nút mở ActionProfiles = `port.ShowActionProfiles`, modal lồng) | ô bắt phím: subclass EDIT + `SettingsPresenter.CaptureShortcut`; cần mở rộng khung để bắt WM_KEYDOWN/WM_IME của EDIT (làm ở PR trang này) |
| Files | ExternalEditorPath (+ Browse qua `IShellFileDialog`, `SettingsTarget.ExternalEditor` focus ô này: hook `FocusExternalEditor` còn thiếu), JournalDurability (2 radio: `BS_AUTORADIOBUTTON` + `WS_GROUP` ở ô đầu; builder chưa có `Radio`), ConfirmBeforeDelete, AllowPermanentDeleteWithoutRecycleBin, MoveCopyReuseLastFolder, nhóm RAW, WebP/HEIC... | thêm `Radio` vào builder |
| Diagnostics | (xong) | |

## 4. Kiểm chứng

Test mới (`Shell.Tests/Dialogs/SettingsUi`, 74 phương thức + 1 ca chụp ảnh thủ công; ca `[Theory]` nở thêm):

| File | Phương thức | Phủ |
|---|---|---|
| `SettingsDialogTests` | 32 | mở với đủ trang + nhớ trang cuối, chọn trang (tiêu đề, nhớ), `ExternalEditor` mở trang Files không đổi trang nhớ, Ctrl+Tab vòng + Tab thường để cho dialog manager, trang chờ có ghi chú và Save giữ nguyên giá trị, Save/Enter/Apply/Cancel (mọi đường huỷ không ghi, Apply rồi Cancel vẫn báo `Saved`), giá trị sai = cảnh báo của presenter + focus đúng ô trên đúng trang, store không ghi được, Defaults/Reset trang (chỉ trang đang hiện)/Import/Export, cuộn (con lăn, thanh cuộn, focus dưới nếp gấp), bố cục không chồng/không cắt ở 100 % và 150 % (en, vi), đổi kích thước, đổi ngôn ngữ (tiêu đề, nav, trang, MỌI nút), rò GDI/USER sau 5 lượt, chrome áp một lần cho khung và từng trang, điều khiển chỉ tồn tại cho trang đã mở, trang mở sau Defaults, modal khoá/bật owner |
| `SettingsPagesTests` | 22 | từng thuộc tính của 3 trang: Load -> Collect -> Save (mọi chỉ số/thẻ combobox, cờ EXIF/tiêu đề, thanh trượt), thứ tự mục combobox ghép theo CHỮ hiển thị (`General_SortMode_EveryComboItem_SavesTheModeItsTextNames`), hiệp ước với `SettingsFieldMap` (mỗi thuộc tính của trang thuộc đúng trang), kiểm hợp lệ từng ô, bật/tắt phụ thuộc, danh sách ngôn ngữ (mục `auto` ở chỉ số 0), Explorer/log/thư mục dịch |
| `SettingsPortTests` | 7 | cổng: `Supports(Settings)` chỉ khi bật, cờ ẩn môi trường, `ShowSettings` trả true sau Save/false khi huỷ, không có store, `ExternalEditor`, Dispose đóng hộp thoại đang mở |
| `SettingsTimingTests` / `SettingsSnapshotTests` | 1 + 1 | in thời gian mở; `PHOTOREVIEW_DIALOG_SNAPSHOTS=<dir>` ghi `settings-<trang>-{en,vi}[-150].png` (+ `-bottom`) |
| `SecondaryWindowRoutingTests`, `LazyLoadTests` | (chỉnh) | Settings do cổng Win32 nhận chỉ khi bật cờ; tiến trình sạch mở Settings + đóng bằng WM_CLOSE: KHÔNG nạp WPF/cầu |

Chạy 2026-10-13 (desktop ẩn `PRWp35g3a`, filter CI `Category!=Manual&Category!=Native`, `--blame-hang-timeout 120s`): `Shell.Tests` 1 277, `Shell.Integration.Tests` 145,
`Shell.WpfBridge.Tests` 39, `Architecture.Tests` 158, `Integration.Tests` 1 410, `App.Tests` 2 593 - 0 lỗi. Lặp bộ mới (`FullyQualifiedName~SettingsUi`, 74 ca/lượt): 50 lượt, 50 đạt, 0 lỗi (không lượt nào treo).
Lần chạy đầu `Architecture.Tests` đỏ 3 ca (đã sửa): `ConfigureAwait(false)` trong lớp App (kiểm tra cập nhật), `Context.Warn(Tr....)` bị luật "Tr.* không vào sink log"
(đổi tên thành `ShowWarning`), TEST-09 (lớp test đổi biến môi trường phải thuộc `[Collection("GlobalState")]`: đổi tên nhóm `OverlayLocalization` của Shell.Tests thành
`GlobalState` ở 16 file, cùng một nhóm không song song như cũ).

## 4b. Số đo (exe THẬT, R2R và Native AOT xen kẽ)

Bản probe tạm KHÔNG commit (`PR_WP35G3A_PROBE` trong `ShellStartup.ArmSmokeExit`: sau khung đầu mở `SettingsDialog` qua cổng thật 8 lần, owner = HWND shell, đóng ngay khi hiện),
`PhotoReview.exe --smoke <jpg 1600x1000>` trên desktop ẩn `PRWp35g3arun`, dữ liệu cô lập `PHOTOREVIEW_DATA_ROOT`; 6 lượt xen kẽ R2R, AOT, R2R, AOT... (12 tiến trình, 96 lần mở):

| Settings mở (ms, tới khi cửa sổ đã tạo + bố cục + hiện) | R2R | AOT |
|---|---|---|
| Lần đầu của tiến trình (n=6), median / min / max | 171 / 143 / 254 | 130 / 99 / 571 |
| Các lần sau (n=42), median / p95 | 107 / 150 | 103 / 234 |
| Lần sau, lượt YÊN NHẤT (lượt 6) | 74-80 | 68-75 |
| CPU xử lý/lần mở (n=42), median | 109 | (tương tự, 47-156) |
| Assembly WPF/cầu đã nạp | 0 ở mọi lượt | 0 ở mọi lượt |

CẢNH BÁO ĐỘ TIN: các lượt đo chạy khi máy BỊ CHIẾM ~100 % CPU bởi các agent song song (cùng máy; `Get-Counter` 96-100 %); cùng điều kiện đó, cửa sổ Diagnostics đã đo (GĐ1: 25-30 ms
lúc yên) lên 85-170 ms. Tỷ lệ Settings/Diagnostics đo cùng tiến trình = 1,5-1,8 lần (`--probe-native-timing`: Diagnostics 84-168, Settings 130-234 ms) nên ước ở máy yên là 45-70 ms.
Vì vậy cổng "mở <= 150 ms" chưa được xác nhận ở trạng thái yên; ở lượt yên nhất đạt (<= 80 ms các lần sau, lần đầu 99-143 ms). Việc còn lại: đo lại khi máy rảnh.
Không nạp WPF: `LazyLoadTests` (tiến trình sạch, DI thật, Settings cùng sáu cửa sổ khác) + probe (0 assembly `Presentation*`/`WindowsBase`/`System.Xaml`/`PhotoReview.App.Wpf*`/`WpfBridge`).
ILC: `tools/check-aot-warnings.ps1 -RequireToolchain` = **0 cảnh báo** (PhotoReview.exe AOT 14,7 MB, nhóm 22,4 MB); thư mục publish tạm đã dọn.

## 4c. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

29 đột biến thủ công (script ở scratchpad, build Release + test trên desktop ẩn). Bước 1 = `Shell.Tests` lọc `SettingsUi` (73 ca lúc đó); bước 2 (chỉ kẻ sống) = TOÀN `Shell.Tests` (CI filter).

| # | Đột biến | Kết quả |
|---|---|---|
| M01 | General Load: chỉ số chế độ xem lấy từ chỉ số instance | chết bước 1 (7 ca) |
| M02 | General Collect: instance đọc từ combobox chế độ xem | chết bước 1 (4) |
| M03 | General: thứ tự thẻ sắp xếp đổi (combobox lệch thẻ) | SỐNG cả hai bước -> test lấp `General_SortMode_EveryComboItem_SavesTheModeItsTextNames`, chết |
| M04 | General Collect: ngôn ngữ chỉ số 0 không bao giờ được chọn | SỐNG -> thêm khẳng định `auto` (chỉ số 0) trong `General_LanguagePicker_...`, chết |
| M05 | Display Collect: độ mờ thanh công cụ lệch 1 | chết (2) |
| M06 | Display Collect: độ trễ thanh công cụ đọc từ ô độ trễ lớp phủ | chết (4) |
| M07 | Display Load: ShowFileInfo lấy ShowFolderInfo | chết (1) |
| M08 | Display Collect: cờ EXIF ghi đè thay vì cộng dồn | chết (1) |
| M09 | Display: ô độ trễ lớp phủ bật bởi thanh công cụ | chết (1) |
| M10 | Display Collect: LoadingMode lấy chỉ số ScalingQuality | chết (2) |
| M11 | Diagnostics Collect: LoggingEnabled đảo | chết (2) |
| M12 | Display FocusTarget: ô sai cho lỗi độ trễ lớp phủ | chết (1) |
| M13 | Dialog CollectAll: bắt đầu từ form trống (trang chưa mở mất giá trị) | chết (29) |
| M14 | Dialog: trang cuối không được nhớ | chết (2) |
| M15 | Dialog TrySaveAll: cờ `Saved` không bao giờ đặt | chết (4) |
| M16 | Dialog TrySaveAll: lưu sai không focus ô lỗi | chết (1) |
| M17 | Dialog Reset trang: nạp lại giá trị hiện tại thay vì mặc định | chết (1) |
| M18 | Dialog Defaults: nạp lại giá trị hiện tại | chết (2) |
| M19 | Dialog Apply: không làm gì | chết (1) |
| M20 | Dialog CollectAll: gom cả trang chưa tạo điều khiển | chết (1) |
| M21 | Dialog Save (OK): đóng mà không ghi | chết (5) |
| M22 | Dialog Cancel: ghi | chết (3) |
| M23 | Danh mục: `IsComplete` luôn true | chết (1) |
| M24 | Cổng: Settings luôn được hỗ trợ | chết (2) |
| M25 | Dialog: hướng Ctrl+Tab đảo | chết (1) |
| M26 | Dialog đổi ngôn ngữ: danh sách điều hướng không đổi chữ | chết (1) |
| M27 | Dialog đổi ngôn ngữ: nút Apply giữ chữ cũ ("Apply" trùng chữ tiếng Anh) | SỐNG -> thêm khẳng định mọi nút ở tiếng Việt, chết |
| M28 | Cổng ShowSettings: luôn báo đã lưu | chết (1) |
| M29 | Dialog: sự kiện do chính việc nạp giá trị gây ra được xử lý như sửa tay | SỐNG, **tương đương hiện tại**: không trang nào có `OnCommand` quan sát EN_CHANGE/CBN do nạp; giữ chốt vì các trang Mouse/Performance (ô Edit + combobox phụ thuộc) sẽ cần |

Tổng: 29 chạy; 25 chết bước 1; 4 sống bước 1 (và bước 2); 3 được lấp bằng test mới (M03, M04, M27, kiểm lại: chết); 1 tương đương (M29). Còn sống thật: 0.
Ghi chú: lần làm trước bị ngắt giữa lúc chạy M01 nên cây làm việc còn dòng đột biến trong `GeneralSettingsPage.Load` (chưa commit); đã hoàn nguyên bằng sửa tay trước mọi phép đo và đo lại.

## 4d. Nhìn bằng mắt và phần CHƯA kiểm được bằng máy

Ảnh `PrintWindow` trên desktop ẩn (comctl32 v6): `settings-{general,display,diagnostics}-{en,vi}[-150].png`, `settings-display-*-bottom`, `settings-mouse-pending-*`. Đã xem: nav tối chọn xanh,
group box + tiêu đề, combobox/edit/checkbox/thanh trượt đọc được; tiếng Việt dài (ghi chú 3 dòng) không bị cắt; 150 % không chồng; thanh cuộn tối riêng bên phải.
Chưa kiểm được: title bar tối và DPI hai màn hình thật (như GĐ1/GĐ2), dòng chọn của danh sách thả (màu hệ thống), Narrator, bàn phím thật qua RDP/IME.

## 5. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

- CHƯA XÁC NHẬN: tiếp tục phương án B; GĐ3b-c = bốn trang còn lại song song (mục 3); GĐ4 (Recovery) riêng.
- Ngoài WPF (chỉ view): Apply, Reset trang, Ctrl+Tab giữ như WPF, nút Save giữ nhãn "Save" (WPF) thay vì "OK".
- Phương án thay: (D) `PhotoReview.Settings.exe` WPF đồng hành nếu các trang còn lại vượt ngân sách; (C) giữ cầu WPF (bỏ `Supports(Settings)`).
- Hoàn tác: revert PR; hoặc bỏ cờ/giá trị `SettingsEnabled` (một dòng) để Settings về cầu WPF.
