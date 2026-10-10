---
id: NOWPF-WP10
order: 312
summary: |-
  WP-10 (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): bộ ghi golden chạy trên MainWindow WPF thật (không hiện cửa sổ, desktop ẩn) -> tests/Fixtures/golden/{viewport-layout,input-scripts,key-names,context-menu,overlay-boxes}.v1.json (1200 ca G-VIEW, 91 kịch bản G-INPUT, 12 ca G-MENU, mọi System.Windows.Input.Key, 72 hộp G-OVL); test GoldenWpfConformanceTests phát lại golden trên WPF (chặn hồi quy); tools/diag/record-golden.ps1 ghi lại tất định; app WPF không đổi hành vi.
---

# NOWPF-WP10-GOLDEN-RECORDER - golden từ bản WPF (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) mục 5 (C-17), 7.3; thẻ WP-10 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Golden là **chuẩn tương đương** của bản Win32: WP-16 (engine viewport) phải tái tạo `viewport-layout`, WP-22/24/23 phải khớp
`input-scripts`/`context-menu`/`overlay-boxes`, WP-07 phải khớp `key-names`. Golden sai là rủi ro lớn nhất của dự án, nên
file được test xác nhận lại trên WPF ở mỗi lần chạy CI (`Category=UI`).

## 1. Có gì

| Golden | File (`tests/Fixtures/golden/`) | Ca | Ghi bởi |
|---|---|---|---|
| G-VIEW | `viewport-layout.v1.json` | 1200 = 5 client {800x600, 1280x720, 1920x1080, 1366x728, 3840x2160} x 4 DPI {1, 1.25, 1.5, 2} x 5 ảnh {6000x4000, 4000x6000, 800x600, 12000x1000, 1000x12000} x 12 chế độ (`fit`, `fit-preview`, `fitwidth`, `fitheight`, `z25 z50 z75 z100 z150 z200 z300 z400`) | `ViewportGoldenRecorder` |
| G-INPUT | `input-scripts.v1.json` | 91 kịch bản, checkpoint sau MỌI bước (khung kinetic cũng có) | `InputScriptCatalog` + `InputScriptRecorder` |
| G-KEY | `key-names.v1.json` | mọi tên `System.Windows.Input.Key` (giá trị + `KeyInterop.VirtualKeyFromKey`) | `KeyNameGoldenTests` (App.Tests) |
| G-MENU | `context-menu.v1.json` | 12 cấu hình settings (6 nhóm ẩn/hiện, legacy flags, không ảnh) | `MenuGoldenRecorder` |
| G-OVL | `overlay-boxes.v1.json` | 72 = 3 cửa sổ x 2 DPI x 2 cỡ chữ x 6 panel (chuỗi cố định) | `OverlayGoldenRecorder` |

Lược đồ: các kiểu `Golden*`/`*Dto` của C-17 (khoá, không đổi) làm phần tử `items`; phong bì `GoldenDocument<T>` +
`GoldenOverlayBox` + `GoldenFile` + `GoldenJsonContext` (source-gen) là kiểu mới trong `PhotoReview.TestSupport.Golden`, ngoài hợp đồng.
Mỗi phần tử một dòng, LF, UTF-8 không BOM, không timestamp/tên máy: ghi lại trên cùng bản WPF cho ra **cùng byte** (đã kiểm: 2 lần
ghi liên tiếp, `cmp` giống hệt).

## 2. Cách ghi lại (tái tạo)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/diag/record-golden.ps1          # build + ghi
powershell -NoProfile -ExecutionPolicy Bypass -File tools/diag/record-golden.ps1 -NoBuild # dùng build Release có sẵn
```

Script chạy `tools/run-tests-hidden.ps1` (desktop ẩn) với `PHOTOREVIEW_GOLDEN_RECORD=1`: test `Category=Manual`
`GoldenRecordTests.RecordAll...` (Integration.Tests) và `KeyNameGoldenTests.RecordKeyNames...` (App.Tests). Recorder từ chối ghi khi
thiếu biến môi trường. Điều kiện: cửa sổ KHÔNG hiện (nội dung `MainWindow` được Measure/Arrange ở kích thước client cố định, như
`MainWindowZoomDetailTests`); DPI được mô phỏng theo ca (xem 4); dữ liệu/config trong thư mục tạm riêng (`DataRootFixture`), không
đụng ảnh/cache/config thật; thời gian ~25 s (G-VIEW 5 s, G-INPUT 17 s). Sau khi ghi, `git diff tests/Fixtures/golden` là hành vi
WPF đã đổi: phải đọc và hiểu, KHÔNG ghi lại chỉ để test xanh.

## 3. Cách ghi (bản WPF thật ở đâu, thay thế ở đâu)

- **Hình học là WPF thật**: `ScrollViewer` (`ImageScroll`, thanh cuộn Auto) + `Image` (`MainImage`, binding `Viewer.Stretch/MaxImageWidth/
  ImageWidth`) của `MainWindow` dựng qua `AppHost.BuildServices`; `ViewerState`, `PointerInputController`, `FitViewController`,
  `ShortcutRouter`, `MiddleClickResolver`, `ContextMenuItems` là mã sản xuất.
- **Thay bằng kịch bản** (`ScriptedSurface` bao `WpfImageSurface`): callback khung `CompositionTarget.Rendering` (RenderingTime cố định
  = `TimestampMs`), chuột/capture (không cần input desktop), cửa sổ "đã Loaded", ngưỡng kéo 4 DIP, `DisplayTiming = null` (kinetic
  bước theo RenderingTime, như `KineticGlideSmoothing=Off`). Nhờ đó kinetic phát lại tất định.
- **Đi dây lại trong recorder** (tách khỏi `MainWindow` ctor để thay surface): `PointerCommands`, `FitViewController`, `ZoomModeChanged ->
  StopKinetic`, hợp phần của `Window_KeyDown` (arrow pan -> `ShortcutRouter.TryResolve` -> `ExecuteReviewCommandAsync` phần zoom/Fit),
  chuỗi "ảnh mới" (`SetSourceSize(newImage)`, `UpdateFitSize`, `ApplyInitialViewAsync`). Nếu `MainWindow` đổi dây, recorder phải đổi theo:
  test chéo `ScriptedHarness_MatchesTheRealShownWindow_ForKeyboardZoomAndFit` (cửa sổ WPF THẬT được hiện ngoài màn hình, bắn
  `PreviewKeyDown` thật `Add, Add, D1, Add, F`, so zoom/offset/extent từng bước với recorder, sai số 0,5 DIP) bắt lệch.
- **Hai chỗ chạm vào private** (reflection, test-only, không sửa app): `MainWindow._cachedDpiScale` (cache DPI mà `MainWindow_DpiChanged`
  gán) và `MainWindow.UpdateFitSize`. Đổi tên một trong hai làm recorder ném lỗi rõ ràng.

## 4. Phát hiện khi ghi (đưa vào WP-16/WP-22/WP-23)

1. **`ScrollBarThickness` = 10 DIP, đo được** (thanh cuộn tối `DarkScrollBars.xaml`: `Width/MinWidth=10`), KHÔNG phải
   `SystemParameters.VerticalScrollBarWidth` như mục C-08 của kế hoạch viết (17). Golden ghi đúng 10 trong `Input.ScrollBarThickness`.
2. **DPI không đổi bố cục DIP**: cùng ca ở DPI 1.0/1.5 cho hộp bao overlay giống hệt (không bật `UseLayoutRounding`); DPI chỉ
   vào `ViewerState.DpiScale` (kích thước phần tử ảnh = pixel x zoom / DPI) và hệ số fit width. G-VIEW có 4 DPI để engine thử đúng
   công thức đó. Mô phỏng DPI = `VisualTreeHelper.SetRootDpi` + gán `_cachedDpiScale` + `UpdateFitSize` (đường `DpiChanged`).
3. **Fit width/height ghi kết quả SAU pass hiệu chỉnh thanh cuộn** (`FitWidthAsync` -> `CorrectForSideScrollbarAsync`): ví dụ ảnh
   6000x4000, client 1280x720, DPI 1.25: `ImageWidth = 1270` (không phải 1280) vì thanh cuộn dọc chiếm 10 DIP. Engine nhận
   `ImageWidth/MaxImageWidth` đã chốt trong `Input`, không tự lặp.
4. **Fit upscale ảnh nhỏ** (`800x600` ở client 1280x720 Fit -> 960x720 khi MaxWidth/Height = viewport): ghi nguyên; `fit-preview`
   dùng bitmap là bản preview (1.15 x hộp pixel, không lớn hơn nguồn) vì đó là kích thước tự nhiên thật mà `Stretch=Uniform` đo.
5. `ViewerState.MaxImageWidth/Height` = `+Infinity` ngoài Fit và `ImageWidth/Height` = `NaN` trong Fit: file dùng chuỗi `"Infinity"`/`"NaN"`
   (`JsonNumberHandling.AllowNamedFloatingPointLiterals`); bản Win32 đọc bằng cùng `GoldenJsonContext`.
6. `Window.Settings`-driven: ZoomStep của viewer là `KeyboardZoomStepPercent/100` (mặc định 10 %), KHÔNG phải hằng 0,25 của
   `ViewerState.ZoomStep`; recorder đẩy giá trị như `MainViewModel.Settings` setter. Bước wheel từ Fit đi từ `FitZoom`.

## 5. Từ vựng bước của G-INPUT (`GoldenInputStep`)

| Kind | Trường dùng | Ý nghĩa |
|---|---|---|
| `wheel` / `hwheel` | `Delta`, `X`,`Y`, `Modifiers` ("Control"), `Key` ("Touchpad" = gợi ý thiết bị), `TimestampMs` | `PointerInputController.OnWheelAsync(WheelInput, point)` |
| `press` | `Key` ("Left"/"Middle"), `Delta` = số click, `X`,`Y`, `TimestampMs` | Window tunnel `OnWindowPreviewMouseDown` rồi `OnImagePress` (trái) hoặc `MiddleClickResolver` (giữa, chỉ click đầu) |
| `move` / `release` | `X`,`Y`, `TimestampMs` | `OnImageMove(leftDown, ...)` / `OnImageRelease` |
| `key` | `Key` = tên `System.Windows.Input.Key`; `Modifiers` = "Control,Shift,Alt,Repeat" | `TryPanByArrow` rồi `ShortcutRouter.TryResolve` (modifier lấy từ bước vì `Keyboard.Modifiers` không đặt được khi headless) |
| `frame` | `TimestampMs` = RenderingTime; `Delta` = ms từ khung trước (thông tin) | một callback `CompositionTarget.Rendering` |
| `resize` | `X`,`Y` = client mới | `ImageScroll` đổi cỡ -> `UpdateFitSize` |
| `command` | `Command` = `ReviewCommandType`, hoặc `SetClickZoomLevel`(X=%), `ZoomToLevelMenu`, `SetDpi`(X), `LoadImage`(X,Y), `SwapSourceSize`(X,Y) | lệnh viewport như `ExecuteReviewCommandAsync`; lệnh không đụng viewport (xoá, di chuyển...) bị bỏ qua |

Setup: ảnh đầu hiện ở Fit với settings mặc định, rồi mới áp `SettingsOverridesJson` (enum dạng tên, khoá camelCase `AppSettings`).
Mỗi kịch bản một `MainWindow` mới (controller giữ trạng thái và đăng ký sự kiện của viewer suốt đời). Phủ: wheel 5 điểm + ảnh
nhỏ/dọc/panorama, Ctrl+wheel, `MouseWheelAction=Navigate`, FitWidth (3 anchor x ngang/dọc, FitWidth2, FitHeight, panorama, DPI 1.5),
click-zoom bật/tắt (100/200/50 %, dưới ngưỡng kéo, double-click Fit), kéo pan (kẹp mép, ở Fit, ảnh nhỏ hơn viewport), kinetic (6 kịch
bản: khung 16/7 ms, khung bất thường, nhấn dừng glide, chạm mép, wheel dừng glide), phím mũi tên (bước 10/25 %, mép, setting điều hướng
ở mép, kinetic impulse, ở Fit), zoom bàn phím (2 anchor, bước 5 %, auto-repeat, Ctrl+Add), touchpad (pan/swipe/pinch/tắt/ngang ở Fit),
chuột giữa (8 `MiddleClickAction`), KeepZoom 3 ảnh (bật/tắt) + 6 `InitialViewMode`, đổi kích thước RAW (3), DPI đổi giữa chừng (3),
resize (4), menu zoom (3) + 4K/DPI 2.

### Nhóm `ArrowPan-pending-rule-change|` (chờ đổi quy tắc)

Chủ dự án sắp đổi bước pan phím mũi tên của app WPF (pixel bằng nhau hai trục = `ArrowPanStepPercent` % x min(viewport W, H); hiện
mỗi trục x kích thước viewport của trục đó). 5 kịch bản pan bằng mũi tên khi đang zoom (`arrow-keys-pan-at-100pct-step10`,
`...-at-edge-consumed-no-navigate`, `...-at-edge-navigate-setting`, `...-step-25pct`, `...-kinetic-impulse-frames`) mang tiền tố
`ArrowPan-pending-rule-change|` và được ghi theo quy tắc CŨ: KHÔNG phải chuẩn cho bản Win32. Chúng bị loại khỏi lần phát lại chặn
(`InputScripts_CommittedGolden_ReplayOnWpf`) và chỉ có test `Category=Manual` `InputScripts_ArrowPanPendingRuleChange_ReplayOnWpf`.
Sau khi PR pan merge: chạy test Manual đó để thấy đúng chỗ đổi, chạy `tools/diag/record-golden.ps1`, rồi bỏ tiền tố
(`InputScriptCatalog.ArrowPanPending`) và đưa nhóm này lại vào lần phát lại chặn. `arrow-keys-at-fit-navigate` (không pan) không bị ảnh hưởng.

## 6. G-MENU: id

`ContextMenuItemId` cho cấp trên (`Undo, MoveToRecycleBin, Fit, ZoomToLevel, ZoomSubmenu, Refresh, OpenFolder, NextFolder,
PreviousFolder, ExternalEditor, CopyFileName, CopyFullPath, Settings`), `Separator:{nhóm}` (2 = trước cụm zoom, 3 thư mục, 4 trình
sửa ngoài, 5 sao chép, 6 Settings), trong menu Zoom: `ZoomFitWidth, ZoomFitWidth2, ZoomFitHeight, Separator:Zoom.2, Zoom.Preset.N,
Zoom.Custom, Separator:Zoom.3, Zoom.AlsoSetClickLevel, Zoom.SetCurrentAsClickLevel` (danh sách phẳng, ngay sau `ZoomSubmenu`). Tiền
tố tên ca `photo|`/`nophoto|` = có/không có ảnh đang mở (`CopyFileName/CopyFullPath` cần ảnh). `ContextMenuModelBuilder` (WP-18) phải cho
đúng chuỗi id này.

## 7. Test và mutation

`GoldenWpfConformanceTests` (`Category=UI`, ~30 s): `ViewportLayout_CommittedGolden_MatchesWpf` (ghi lại trong bộ nhớ, so từng trường
0,001 DIP, tên ca/lưới đúng), `InputScripts_CommittedGolden_ReplayOnWpf` (phát lại BƯỚC trong file, so mọi checkpoint),
`InputScripts_CatalogStepsAreTheCommittedOnes`, `ContextMenu_...`, `OverlayBoxes_...` (+/- 2 DIP, phụ thuộc phông máy ghi),
`ScriptedHarness_MatchesTheRealShownWindow_...`; `KeyNameGoldenTests` (2 test, App.Tests). Mutation (sửa tạm một dòng WPF, build, chạy
conformance, hoàn tác): xem PR; kết quả bảng dưới.

| Mutation (mã WPF) | Kết quả |
|---|---|
| `KineticScroller.TimeConstantMs` 325 -> 300 | `InputScripts_CommittedGolden_ReplayOnWpf` đỏ |
| `DarkScrollBars.xaml` Width 10 -> 12 | `ViewportLayout_...` và `InputScripts_...` đỏ |
| `CalculateFitWidthAnchorPoint` TopThird 1/3 -> 0,4 | `InputScripts_...` đỏ |
| `ContextMenuItems` nhóm của Refresh 2 -> 3 | `ContextMenu_...` đỏ |
| `ViewerState.MaxStepZoom` 4,0 -> 1,5 | `InputScripts_...` đỏ |
| recorder: `ScrollBarThickness` giả (+1) | `ViewportLayout_...` đỏ ("expected 10 but WPF gives 11") |
| file `key-names.v1.json`: sửa một `virtualKey` | `KeyNames_CommittedGolden_...` đỏ |

## 8. Lệch so với thẻ WP-10

- G-VIEW zoom bước: 8 giá trị (25, 50, 75, 100, 150, 200, 300, 400) thay vì mọi bội của 25 để file ~650 KB; vẫn 1200 ca (>= 600).
- G-OVL: 2 DPI cho cùng bố cục DIP (phát hiện 2 ở trên): giữ cả hai để WP-23 kiểm đúng điều đó, không phải hai chuẩn khác nhau.
- `DisplayTiming`/vblank không có trong golden kinetic (bước theo RenderingTime). Win32 có thể dùng dự đoán vblank, phải so ở dạng
  "cùng điểm cuối +/- 0,5 DIP" chứ không từng khung nếu bật `Predict`.
- Không thêm seam vào app WPF (reflection trong test thay cho seam đọc trạng thái).
