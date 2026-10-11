---
id: NOWPF-WP22
order: 835
summary: |-
  WP-22 (đợt 3 NO-WPF, 2026-10-12): nhập liệu shell Win32. Ba handler thông điệp (phím, chuột, wheel/touchpad) + kéo-thả IDropTarget vào controller con trỏ/Fit/ShortcutRouter THẬT của App.Shared trên ViewportController (adapter ViewportImageSurface, không sửa Viewing/*), đúng thứ tự Window_KeyDown của WPF. G-INPUT (91 kịch bản) khớp 100 % qua thông điệp Win32 thật gửi vào HWND; G-KEY qua KeyIdMapping. Độ trễ phím->layout p50 0,18 ms / p95 0,37 ms; phím->khung ~260 ms do Present(1) (không thuộc WP-22, xem perf). Điểm cắm cho WP-23/24/25: OverlayHitTest, ActivationKeyOwned, EscapeInterceptor, PointerActivity, ShellInput.ExecuteAsync.
---

# NOWPF-WP22 - nhập liệu shell (2026-10-12)

Thẻ: WP-22 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiêu thụ C-06 (KeyId/PointD), C-07 (IImageSurface/IFitSurface), C-15
(IShellWindow). App WPF (`PhotoReview.App`) **không đổi**; liên kết `.jpg` không đổi. Đo: [perf/2026-10-12-wp22-input.md](../perf/2026-10-12-wp22-input.md).

## 1. Đã làm

| File (`src/PhotoReview.Shell.Win32/Input`) | Nội dung |
|---|---|
| `ShellInput.cs` | Bản Win32 của phần chuột/phím trong `MainWindow`: sở hữu `PointerInputController`, `FitViewController`, `ShortcutRouter` thật; `HandleKeyDown` đúng thứ tự `Window_KeyDown` (pan mũi tên khi không modifier và không so sánh -> Esc đóng popup -> Esc huỷ băm trùng lặp -> Space/Enter của nút -> router -> lệnh; file action không lặp khi auto-repeat); nhấn/di chuyển/nhả/capture; wheel dọc/ngang; `ExecuteAsync` = `ExecuteReviewCommandAsync` (phần viewport chạy tại chỗ, còn lại sang host). |
| `KeyboardMessageHandler.cs` | WM_KEYDOWN/SYSKEYDOWN/KEYUP/CHAR: VK + bit mở rộng -> `KeyId` (`KeyIdMapping`, G-KEY), Shift phải theo scan code, bit 30 = auto-repeat; phím đã xử lý thì KEYUP/CHAR đi kèm bị nuốt (không bíp), phím chưa xử lý đi tiếp (Alt+F4 đóng cửa sổ). |
| `PointerMessageHandler.cs` | WM_*BUTTONDOWN/DBLCLK/MOUSEMOVE/LBUTTONUP/CAPTURECHANGED, toạ độ có dấu (âm khi capture ra ngoài), pixel -> DIP theo DPI cửa sổ. |
| `WheelMessageHandler.cs` | WM_MOUSEWHEEL/MOUSEHWHEEL: toạ độ MÀN HÌNH -> client, delta có dấu ở HIWORD, Ctrl = MK_CONTROL; thiết bị touchpad đọc trong lượt phân phối. |
| `DropTarget.cs` | `IDropTarget` ([GeneratedComClass]) + `RegisterDragDrop`/`OleInitialize`; Copy khi có CF_HDROP, thả = mở file ĐẦU TIÊN, lỗi báo lên status. |
| `ClickCounter.cs` | Đếm số lần nhấn (lớp cửa sổ không có CS_DBLCLKS): cùng nút, <= GetDoubleClickTime, trong hình chữ nhật SM_CXDOUBLECLK; >= 2 = Fit. |
| `ViewportImageSurface.cs` | `IImageSurface` + `IFitSurface` trên `ViewportController` (WP-16 để chưa khai báo): mỏng, thêm `Capture()`. |
| `ShellInputEnvironment.cs`, `InputNative.cs` | Seam đọc hệ thống (phím chỉnh, `GetMessageTime`, thiết bị wheel, ngưỡng double-click) + user32 còn thiếu. |
| `ShellMainWindow.Input.cs` | Nối vào cửa sổ chính: cài đặt đổi -> `RebuildShortcuts`, điều hướng dừng đà, host (lệnh -> `MainViewModel`), keep-zoom/click-zoom lưu qua `SettingsStore`, `OpenClickZoomCustomDialogAsync`/`ApplyZoomMenuSelectionAsync` (cho WP-24). |

Sửa nhỏ ngoài `Input/`: `ShellMainWindow.cs` (một dòng `RegisterFeatures()` + hàm `RegisterFeatures` làm điểm cắm chung; tuỳ chọn thêm
`SettingsStore`/`ZoomPrompt`/`InputEnvironment`; `CreateViewportOptions` nối `DragMetricsPixels`/`CursorScreenPixel` WP-16 để lại),
`ShellStartup.cs` (truyền `store` và `IZoomPromptService`).

## 2. Quyết định / khác bản WPF

1. **Hit-test ảnh**: nhấn trái/di chuyển chỉ vào controller khi con trỏ trên chữ nhật ảnh (như `MainImage` WPF) hoặc ảnh đang giữ capture;
   nút giữa nhận trong cả vùng nhìn (không tính thanh cuộn đang chiếm chỗ). Golden không bị lệch bởi hit-test.
2. **Nguồn dấu thời gian**: wheel dọc dùng `GetMessageTime` (như `e.Timestamp`), wheel ngang dùng `Environment.TickCount` (như hook WPF).
   Ctrl của chuột lấy từ cờ MK_CONTROL của thông điệp, Ctrl của phím từ `GetKeyState`.
3. **So sánh đang hiện** (compare): nhấn/wheel/pan phím không tới ảnh chính (WPF: ComparePanel che ImageScroll).
4. **Điểm cắm cho gói khác** (mặc định không làm gì): `ShellInput.OverlayHitTest` (WP-23 PHẢI đặt để nhấn/wheel trên toolbar không thành
   pan/zoom ảnh), `ActivationKeyOwned` (nút giữ focus sở hữu Space/Enter), `EscapeInterceptor` (Esc đóng popup Tools),
   `PointerActivity` (auto-hide), `ShellInput.ExecuteAsync(ReviewCommand)` (menu/nút chạy cùng đường lệnh), `ApplyInitialViewAsync`
   (WP-21 nối `ApplyInitialViewModeOverride` của sink), `ShellMainWindow.ApplyZoomMenuSelectionAsync` (WP-24).
5. Phím chuột phải/nút phụ chỉ dừng đà (menu ngữ cảnh là WP-24). F11 chỉ đổi `ViewerState.IsFullscreen`; áp lên cửa sổ là WP-25.

## 3. Test

- **G-INPUT qua thông điệp thật** (`InputGoldenShellTests`, Shell.Integration.Tests, `Category=UI`): 91 kịch bản, mọi checkpoint khớp
  (zoom 0,001, độ dài 0,5 DIP). Harness: HWND thật của `ShellWindow`, ba handler thật, `ViewportController` + controller thật; kích thước
  client/DPI/dấu thời gian/phím chỉnh do test đặt (không phụ thuộc màn hình runner hay viền vô hình của GetWindowRect).
- **G-KEY**: `KeyIdMappingTests` (WP-07) + `KeyboardInputTests` (VK -> lệnh theo cài đặt, phím tắt tuỳ chỉnh, Rebuild, action profile).
- Unit (Shell.Tests, `Input/`): `ClickCounterTests`, `KeyboardInputTests`, `PointerInputTests` (chuột, capture, double-click, nút giữa,
  wheel, touchpad ngưỡng 250 ms, hwheel TickCount, kinetic), `DropTargetTests` (HDROP rộng/ANSI, vtable COM thật), `InputNativeTests`
  (SetKeyboardState -> `ReadModifiers`).
- Đầu-cuối (`ShellInputEndToEndTests`, ShellStartup thật): phím điều hướng, Esc/F11, wheel giữ điểm dưới con trỏ, nút giữa, click-zoom
  với capture thật, **port `KeyboardZoomFrameTests`** (không khung nào ở kích thước mới với offset cũ) và
  **`KeepZoomAcrossImagesIntegrationTests`**, kéo-thả (đăng ký OLE + Drop), đổi phím tắt qua `SettingsStore`, K lưu config, điều hướng
  dừng đà, độ trễ.

## 4. Đột biến

35 đột biến thủ công (mỗi cái: sửa một chỗ trong `Shell.Win32/Input` -> build -> chạy Shell.Tests `Input` rồi, nếu sống, Shell.Integration.Tests `Input`
trên desktop ẩn): bit auto-repeat (bỏ qua / bit 29), pan mũi tên (bỏ điều kiện modifier / so sánh đang hiện), Space/Enter của nút,
phím hệ thống không đi qua `System`, file action lặp khi giữ phím, đếm click nút giữa, nút giữa ngoài vùng nhìn / trên thanh cuộn,
`OnWindowPreviewMouseDown`, di chuyển/nhả không cần capture, wheel trên overlay, nguồn dấu thời gian wheel ngang/dọc (TickCount <->
MessageTime), cờ Ctrl của wheel, toạ độ không dấu, biên thời gian và slop double-click, scan code Shift phải, Alt luôn bật, đổi cài
đặt không dựng lại phím tắt, điều hướng không dừng đà, zoom/deactivate không dừng đà, Close không làm gì, thả mở file cuối,
DragEnter luôn Copy, ngưỡng kéo hằng số, K không lật, hwheel khi chưa load, KEYUP của phím khác bị nuốt, cờ MK_LBUTTON, dấu delta.
- 29 bị diệt ngay; **6 sống sót ban đầu** và đã lấp: M02 (hằng bit repeat: test dùng chính hằng của mã -> đổi sang literal), M11/M32
  (kéo/nhả NGOÀI chữ nhật ảnh khi còn capture: kịch bản cũ luôn nằm trong ảnh -> `Drag_ContinuesOutsideTheImageRectangle...`), M21
  (test đổi phím tắt bấm cả phím cũ lẫn mới nên xanh dù không Rebuild -> bấm J, rồi I (phải không đổi), rồi J), M33 (thứ tự KEYUP), M27 (xem dưới).
- 2 tương đương: M23 (`ExitFullscreen` -> `ToggleFullscreen`: router chỉ phát ExitFullscreen khi đang fullscreen nên hai lệnh giống nhau),
  M27 (SM_CXDRAG không đổi theo DPI trên máy này nên hằng 4 giống hệt; vẫn có test `CreateViewportOptions` đọc DPI mỗi lần gọi).
- M35 (`CursorScreenPixel` luôn null) không dựng được bản đột biến sạch (cảnh báo biến không dùng = lỗi build ở Release); hàm được
  ghim bởi `CursorScreenPixel_IsTheCursorPosition_OrNullWhenWindowsCannotSayIt`.
- Không phát hiện lỗi thật trong mã nguồn bởi đột biến; phát hiện hiệu năng: xem mục 5 và perf.

## 5. Việc còn lại / bàn giao

- WP-21: Present(1) chặn ~250 ms mỗi khung trên máy dev (xem perf); nối `ApplyInitialViewAsync` vào sink; compare cần `IsCompareVisible` đúng.
- WP-23: đặt `OverlayHitTest`, `ActivationKeyOwned`, `EscapeInterceptor`; nghe `PointerActivity`.
- WP-24: dùng `ShellInput.ExecuteAsync`, `ApplyZoomMenuSelectionAsync`, `OpenClickZoomCustomDialogAsync`; chuột phải (WM_CONTEXTMENU) chưa được xử lý.
- WP-25: F11 -> cửa sổ thật (`IsFullscreen`).
- Chưa làm (không thuộc thẻ): thanh cuộn tự vẽ (NE-3) nên nhấn trên thanh cuộn chỉ bị loại khỏi vùng nhìn, chưa kéo được thanh.
- Lỗi/khác biệt đã biết ở các bộ test không liên quan: Imaging.Tests (RAW corpus, WebP codec) và App.Tests `RecordKeyNames` (Manual) đỏ khi chạy
  mọi Category trên máy này; Architecture `L-CONTRACT (manual)` cần `PHOTOREVIEW_REGENERATE_CONTRACTS`.
