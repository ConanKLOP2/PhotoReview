---
id: NOWPF-WP23
order: 833
summary: |-
  WP-23 (đợt 3 NO-WPF, 2026-10-12): overlay HUD của shell Win32, không đổi bản WPF. `ShellOverlay` (cây của MainWindow.xaml: ToolbarPanel z cao + InfoOverlayHost chứa StatusPanel/FolderInfoPanel/ZoomIndicatorPanel/CapturePairBadge, thêm `LayerElement`), nút theo style XAML (nền Dark.Input, viền Dark.BorderToolbar, ⋮ 34x32), chuỗi `Tr` đổi trực tiếp khi `Localizer.CurrentChanged`, auto-hide đúng `ToolbarAutoHidePolicy`/`InfoOverlayAutoHidePolicy` (fade vào 150/ra 200 ms bằng `Animator`, hot zone 24, click xuyên ngay khi bắt đầu ẩn) chạy theo `IFrameClock` (`FrameDelayTimer`), hover/nhấn/click, Tab/Space/Enter, nút ⋮ -> menu Tools native (C-12), tooltip native `tooltips_class32`, nối `MainViewModel` + `SettingsStore`, hộp thoại chọn thư mục native `IFileOpenDialog` thay `UnwiredShellFolderDialog`. Golden G-OVL trên shell: 60 ca (toolbar + 4 panel x 3 cửa sổ x 2 DPI x 2 cỡ chữ) lệch tối đa 0,017 DIP; toolbar 210,75 x 40. Khung HUD đầy đủ GPU P50 0,34 / P95 0,47 ms CPU; khởi động +~10 ms. Mutation: xem mục 5.
---

# NOWPF-WP23 - overlay HUD, toolbar, status, auto-hide (2026-10-12)

Thẻ: WP-23 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); hợp đồng C-11/C-12 giữ nguyên (không đổi `Approved/contracts.v1.txt`).
Dựa trên [NOWPF-WP17](NOWPF-WP17-TEXT-OVERLAY.md) (OverlayHost, Animator, DarkPalette, ButtonElement), [NOWPF-WP18](NOWPF-WP18-MENU.md)
(`BuildToolsMenu`, `NativePopupMenuHost`), [NOWPF-WP20](NOWPF-WP20-STARTUP-SHELL.md) (khung vẽ, `RegisterFeatures`) và
[NOWPF-WP19B](NOWPF-WP19B-WPF-BRIDGE.md) (cửa sổ phụ). Bản WPF (`PhotoReview.App`) và liên kết `.jpg` KHÔNG đổi.
Số đo: [perf/2026-10-12-wp23-overlay-hud.md](../perf/2026-10-12-wp23-overlay-hud.md).

## 1. Đã làm (`src/PhotoReview.Shell.Win32`)

| File | Nội dung |
|---|---|
| `Overlay/ShellOverlay.cs` | Bộ HUD: sở hữu `OverlayHost` + `Animator` + hai `FrameDelayTimer`; `Update(OverlayModel)`, `Render`, `Pointer*`, `Wheel`, `KeyDown`, menu Tools, đồng bộ tooltip, auto-hide của toolbar và overlay thông tin (port từng nhánh của `MainWindow.xaml.cs`). Không biết HWND/view model: test bằng đồng hồ giả |
| `Overlay/ToolbarPanel.cs` | 4 nút theo XAML (📂 cỡ 16 Padding 8,2; ⚙; "Vừa khung"/"Fit"; ⋮ ToggleButton 34x32 cỡ 18), Margin 8, Padding 6,4, bo 4; `ApplyLocalization` (nội dung nút Fit, tooltip, AutomationName); mô hình focus bàn phím (Tab) |
| `Overlay/StatusPanel.cs`, `FolderInfoPanel.cs`, `ZoomIndicatorPanel.cs`, `CapturePairBadge.cs` | Đúng thuộc tính của từng `Border` XAML (Margin/Padding/CornerRadius/MaxWidth 640/badge lệch 44); `Apply(OverlayModel)` thay các binding; hàng cảnh báo bỏ qua + nút "Xem danh sách" |
| `Overlay/LayerElement.cs` | `Grid` một ô không nền (`InfoOverlayHost`): con chồng nhau, vùng trống click xuyên, Opacity/IsHitTestVisible áp cho cả lớp |
| `Overlay/OverlayModel.cs` | Ảnh chụp bất biến của view model (`From(MainViewModel)`) |
| `Overlay/FrameDelayTimer.cs` | Hẹn giờ một lần theo `IFrameClock` (thay `DispatcherTimer`); chỉ nghe đồng hồ khi đang đếm |
| `Overlay/ToolsMenu.cs` | `IOverlayCommands` + dựng/chạy menu Tools từ `ContextMenuModelBuilder.BuildToolsMenu` (mục xoá trùng no-op khi chưa có ảnh) |
| `Overlay/NativeTooltipHost.cs`, `DeferredTooltips.cs`, `OverlayNative.cs` | Tooltip hệ thống `tooltips_class32` (`TTM_ADDTOOLW` theo hộp từng phần tử, chuột chuyển bằng `TTM_RELAYEVENT`), tạo SAU khung đầu; P/Invoke riêng của gói |
| `Overlay/OverlayMessageHandler.cs` | Chuột/phím của cửa sổ -> overlay (đứng đầu chuỗi handler) |
| `Overlay/ShellMainWindow.Overlay.cs` | `AttachOverlay` (một lần sau `AttachRenderer`), nối `MainViewModel`/`InfoOverlay`/`Compare` PropertyChanged, `SettingsStore.Changed`, `Localizer.CurrentChanged`, Fit, báo lỗi lệnh |
| `Overlay/ShellOverlayServices.cs`, `Dialogs/NativeShellFolderDialog.cs` | `AddShellOverlay()` (MỘT dòng ở `RegisterFeatures`) + chọn thư mục native `IFileOpenDialog` (FOS_PICKFOLDERS \| FORCEFILESYSTEM \| PATHMUSTEXIST) |
| `Overlay/ButtonElement.cs` (WP-17) | Thêm: Background/BorderColor, FixedWidth/Height, IsChecked/IsFocused, ToolTip, AutomationName (mặc định giữ nguyên) |

Điểm chạm file chung (mỗi chỗ MỘT dòng, giữ cả hai khi xung đột): `ShellMainWindow.Frame.cs` (`DrawOverlay(context)` sau `DrawContent`),
`ShellMainWindow.cs` (`DisposeOverlay()`), `ShellStartup.cs` (`_main.AttachOverlay(services, _frameClock)`), `ShellCompositionRoot.cs`
(`AddShellOverlay()` + `FolderPicker` lấy `IShellFolderDialog` từ DI), `Hosting/ShellWindow.cs` (`InsertMessageHandlerFirst`),
`Startup/ShellWarmup.cs` (làm nóng DirectWrite trên pool). Không thêm assembly/exe; không chuỗi i18n mới (dùng khoá `main.toolbar.*`,
`main.tools.*`, `main.skipped.*`, `main.folderInfo.*`, `main.zoomIndicator.*` sẵn có).

## 2. Quy ước đã chọn (lệch/cụ thể hoá so với thẻ)

1. **Cây overlay**: `ToolbarPanel` là gốc cuối (z 100); bốn panel thông tin nằm trong `LayerElement "InfoOverlayHost"` - auto-hide của
   overlay thông tin chỉ chạm Opacity/IsHitTestVisible của lớp này (Q-R34), không đổi Visibility của con. `ComparePanel` là của WP-21.
2. **Auto-hide theo khung, không theo `DispatcherTimer`**: `FrameDelayTimer` chạm hạn ở khung đầu có `Timestamp >= hạn chót` (độ phân giải
   một khung ~16 ms); mốc t = 0 của tween là khung đầu sau `FadeTo` (WP-17 mục 2.2), nên opacity bắt đầu đổi sau ~1 khung. Hot zone,
   độ trễ (kẹp theo `AppSettings`, `Math.Max(1, ...)`), độ mờ toolbar (>= 20 %), "đếm lại từ lúc thông điệp/hộp thoại hết" như bản WPF.
3. **Click xuyên**: `IsHitTestVisible = false` ngay khi bắt đầu fade ra (như `FadeTo` của WPF); phần tử ẩn/opacity 0 không nhận chuột/bánh xe;
   nhấn trên panel còn hiện bị NUỐT (Border trong WPF không chuyển click xuống `ScrollViewer`), nhả cũng bị nuốt, chuột được capture suốt cú nhấn.
4. **Nút**: style của `MainWindow.xaml` (nền `Dark.Input`, viền `Dark.BorderToolbar` 1 DIP, không bo góc, chữ `Dark.TextBright`). Hover/nhấn dùng
   `Dark.ButtonHover`/`Dark.ButtonPressed`; bản WPF chạy chrome Aero2 mặc định (xanh nhạt `#BEE6FD` khi hover trên nền tối) - **CHƯA XÁC NHẬN**.
5. **Nút ⋮** mở menu Tools native (thẻ: C-12 `BuildToolsMenu` + `IPopupMenuHost`) neo dưới nút, thay `Popup` sáu nút của XAML (chọn theo
   NE-4 = menu native). Trong lúc menu mở toolbar bị ép hiện; đóng thì đếm ngược từ lúc đó.
6. **Nút "Xem danh sách"** (tệp bị bỏ qua) CÓ làm, dù thẻ ghi "skipped": rẻ (ButtonElement sẵn có) và nếu bỏ thì shell mất đường mở danh sách -
   **CHƯA XÁC NHẬN** (bỏ = ẩn `Status.SkippedRow`).
7. **Bàn phím**: Tab/Shift+Tab đi qua 4 nút (rồi ra khỏi toolbar, không vòng); Space/Enter kích hoạt nút đang giữ (bỏ auto-repeat); focus trong
   toolbar ép toolbar hiện; nhấn chuột ngoài toolbar trả bàn phím về ảnh. Tab bị nuốt luôn: nếu người dùng gán Tab làm phím tắt, gói input (WP-22)
   phải đứng TRƯỚC (hiện overlay đứng đầu chuỗi) - ghi để WP-22 quyết.
8. **Fit**: `ShellMainWindow.FitHandler` (hook). WP-22 nên đặt nó bằng bản dùng chung với `PointerInputController` (cùng `ViewportOperationVersion`);
   chưa đặt thì dùng `FitViewController` riêng trên `ViewportController` (hội tụ 3 lượt, như `FitViewController` của WPF).
9. **Tooltip native** (thẻ chọn native): cần comctl32 v6 (exe có manifest; test kích hoạt manifest nhúng như `Win32DialogServiceTests`), chữ dài
   ngắt dòng ở 420 DIP, chỉ cho phần tử đang hiện và nhận chuột. Vùng: 4 nút toolbar (`ToolTip`), dòng EXIF (toàn văn khi bị cắt "…"),
   huy hiệu cặp ảnh. Tạo SAU khung đầu (Background) vì nạp comctl32 v6 tốn 3-4 ms.
10. **Chọn thư mục native**: `IFileOpenDialog` modal trên luồng STA của UI, huỷ -> null (hành vi `WpfFolderPicker`: thư mục khởi đầu chỉ dùng
    khi tồn tại). Dùng cho nút 📂, mục "Mở thư mục", Move/Copy to folder (cùng `IFolderPicker`).
11. **Chữ & ngôn ngữ**: `Localizer.CurrentChanged` (có thể từ thread bất kỳ) -> `Dispatcher.Post` -> `RefreshLocalizedText()` + `ApplyLocalization()`
    + đọc lại model; huỷ đăng ký khi đóng (sự kiện tĩnh). Chữ do view model dựng (trạng thái, folder, badge) tự đổi qua `RefreshLocalizedText`.
12. **Làm nóng**: `ShellWarmup.WarmGraphics` thêm một lần `DrawText` DirectWrite (factory, bộ font, fallback emoji, đường chữ màu của D2D) trên pool.

## 3. Kiểm chứng

- Build `PhotoReview.slnx` Release 0 cảnh báo / 0 lỗi.
- **G-OVL trên shell** (`ShellOverlayGoldenTests`, DirectWrite thật + tiếng Việt như lúc ghi golden): 60 ca = {ToolbarPanel, StatusPanel,
  FolderInfoPanel, ZoomIndicatorPanel, CapturePairBadge} x 3 cửa sổ x 2 DPI x 2 cỡ chữ, lệch tối đa **0,017 DIP** (thẻ: 2 DIP); toolbar
  210,75 x 40 lệch 0,003 DIP; bất biến theo DPI; đổi ngôn ngữ en <-> vi đổi nội dung/độ rộng toolbar mà không dựng lại.
- `ShellOverlayBehaviorTests` (đồng hồ khung + Stopwatch giả, không phụ thuộc màn hình/thời gian thật): ẩn đúng hạn (1499 -> còn, 1500 -> ẩn),
  fade ra 200 ms/vào 150 ms theo mốc, hot zone (cạnh trong, 0,5 DIP ngoài), đếm ngược không đặt lại khi di chuột ngoài, độ trễ 0/500/99999,
  tắt tự ẩn, không có thư mục, đóng thư mục, độ mờ 60 %/kẹp 20 %, menu Tools giữ toolbar hiện, overlay thông tin (idle, hoạt động, thông điệp,
  compare, cửa sổ mất active, tắt), hover/nhấn/nhả ngoài/huỷ, panel nuốt click, click xuyên khi ẩn, lệnh lỗi/huỷ/ném đồng bộ, Tab/Shift+Tab/Space/
  Enter/auto-repeat, menu Tools (neo, 6 mục, no-op xoá trùng khi chưa có ảnh, không host, vào lại), tooltip (văn bản + hộp + DPI + ẩn), model ->
  panel, thứ tự vẽ (toolbar trên cùng), dispose (đồng hồ, layout, tooltip).
- Native: `NativeTooltipHostTests` (vùng/chữ/thay/xoá/đổi DPI/dispose trên control thật), `NativeShellFolderDialogTests` (cờ, thư mục khởi đầu,
  `Show` thật được đóng bằng WM_CLOSE từ luồng khác -> null).
- Shell thật (`ShellOverlayIntegrationTests`): toolbar có ở khung đầu tại (8, 8) cao ~40; thông điệp chuột Win32 thật click nút Fit, nuốt cú nhấn,
  cú nhấn giữa ảnh không bị nuốt; `SettingsStore.Save` -> toolbar tự ẩn/hiện theo chuột; đổi `Localizer` -> chữ toolbar đổi; đóng -> huỷ đăng ký
  sự kiện tĩnh `Localizer.CurrentChanged`.
- Mắt thường: `ShellOverlayPerfTests.WriteHudSnapshot_WhenAPathIsGiven` (PHOTOREVIEW_OVERLAY_PNG) ghi khung HUD đầy đủ ra PNG; đã xem.

## 4. Số đo

[perf/2026-10-12-wp23-overlay-hud.md](../perf/2026-10-12-wp23-overlay-hud.md): khung HUD đầy đủ (1920x1080, DPI 1,5, GPU) CPU submit P50 0,34 / P95 0,47 ms,
tới khi GPU xong P50 1,03 / P95 1,33 ms; chữ zoom đổi mỗi khung P50 1,0 ms; HUD thêm ~10 ms (~2 %) vào đường tới khung đầu (đã bớt ~7 ms nhờ
tooltip tạo muộn + làm nóng DirectWrite trên pool).

## 5. Đột biến

MUTATION_PLACEHOLDER

## 6. Việc còn lại / bàn giao

- WP-21: `ComparePanel` + trạng thái `Compare.IsVisible` đã nối vào auto-hide (model có `CompareVisible`); gọi `Overlay.NoteActivity()` khi đổi ảnh nếu
  không qua `MainViewModel.CurrentIndex/CurrentImage`.
- WP-22: đặt `FitHandler` dùng chung; quyết định Tab (mục 2.7); `Esc`/phím tắt đứng sau overlay; `ShellMainWindow.Overlay.HitTest(point)` cho
  con trỏ (hiện con trỏ vẫn là mũi tên khi trên nút).
- UIA/Narrator: `AutomationName` đã có trên nút/panel nhưng chưa có cây UIA (gói accessibility).
- Emoji 📂/⚙ trên bề mặt offscreen WARP vẽ đơn sắc; chưa kiểm bằng mắt trên swap chain GPU thật (WP-17 bật `EnableColorFont`).
- Độ phân giải auto-hide = một khung; nếu muốn đúng tới ms thì đổi `FrameDelayTimer` sang `WM_TIMER`/waitable timer (không cần cho UX).
- Kiểm bằng tay hộp thoại chọn thư mục có UI thật (đã kiểm đường `Show` -> đóng bằng WM_CLOSE; chưa bấm chọn thư mục bằng tay).
