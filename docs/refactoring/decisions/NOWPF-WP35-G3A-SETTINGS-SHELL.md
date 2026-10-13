---
id: NOWPF-WP35-G3A
order: 1130
summary: |-
  WP-35 GĐ3a (phương án B của spike S3, CHƯA XÁC NHẬN): KHUNG cửa sổ Settings Win32 gốc + hợp đồng trang `ISettingsPageView` + builder bố cục theo dialog-unit + 3 trang đầu (General, Display, Diagnostics = 20 thuộc tính). Trang tạo điều khiển LƯỜI (chỉ trang đang mở), cuộn dọc, kéo giãn, Save/Apply/Cancel/Defaults/Reset trang/Import/Export, nhớ trang cuối, Ctrl+Tab, đổi ngôn ngữ làm mọi chữ đổi theo; 4 trang còn lại hiện ghi chú và GIỮ nguyên giá trị (Save không làm mất). Cổng Win32 chỉ nhận Settings khi đủ mọi trang hoặc cờ ẩn PHOTOREVIEW_SETTINGS_WIN32=1; mặc định vẫn cầu WPF (hybrid) / cổng tạm (AOT). Số đo, ILC, đột biến: xem mục 4-6.
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

Số ca, số đo, ILC và đột biến: xem phần CẬP NHẬT SỐ LIỆU ở cuối (điền sau khi chạy).

## 5. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

- CHƯA XÁC NHẬN: tiếp tục phương án B; GĐ3b-c = bốn trang còn lại song song (mục 3); GĐ4 (Recovery) riêng.
- Ngoài WPF (chỉ view): Apply, Reset trang, Ctrl+Tab giữ như WPF, nút Save giữ nhãn "Save" (WPF) thay vì "OK".
- Phương án thay: (D) `PhotoReview.Settings.exe` WPF đồng hành nếu các trang còn lại vượt ngân sách; (C) giữ cầu WPF (bỏ `Supports(Settings)`).
- Hoàn tác: revert PR; hoặc bỏ cờ/giá trị `SettingsEnabled` (một dòng) để Settings về cầu WPF.
