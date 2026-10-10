---
id: NOWPF-WP18
order: 372
summary: |-
  WP-18 (đợt 2 của NO-WPF-EXEC-PLAN, 2026-10-10): `ContextMenuModelBuilder` (App.Shared, dùng `ContextMenuItems.Compute` nên không nhân đôi luật ẩn/hiện/dấu phân cách)
  dựng mô hình menu chuột phải + menu Tools tương đương menu WPF; host native `NativePopupMenuHost` (`CreatePopupMenu`/`TrackPopupMenuEx`, `TPM_RETURNCMD`) +
  `DarkModeMenus` (uxtheme ordinal 135/136/133, rơi về sáng). Hợp đồng v1 không đổi; G-MENU thật chờ WP-10 merge.
---

# NOWPF-WP18 - mô hình menu + host native (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (mục 5 C-12, NE-4 = a), thẻ WP-18 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Phụ thuộc WP-13a ([NOWPF-WP13A-INTEROP](NOWPF-WP13A-INTEROP.md)). WP-24 (controller menu) và WP-23 (menu Tools) tiêu thụ phần này.

## 1. Phạm vi và kết quả

| File | Nội dung |
|---|---|
| `src/PhotoReview.App.Shared/Menus/ContextMenuModelBuilder.cs` | `Build` (menu chuột phải) và `BuildToolsMenu`; chuyển khỏi `MenuContracts.cs` (chữ ký công khai giữ nguyên) |
| `src/PhotoReview.App.Shared/Menus/ZoomMenuRules.cs` | `internal`: mức zoom có sẵn + `CalculateClickLevelFromEffectiveZoom` (bản sao của `MainWindow.ZoomPresets` / `MainWindowHelpers`) |
| `src/PhotoReview.Shell.Win32/Menus/NativePopupMenuHost.cs` | `IPopupMenuHost` thực thi: dựng HMENU đệ quy, `TrackPopupMenuEx(TPM_RETURNCMD)`, DIP -> px theo DPI cửa sổ chủ, luôn `DestroyMenu` |
| `src/PhotoReview.Shell.Win32/Menus/NativeMenuMapping.cs` | phần thuần: chữ (`&` nhân đôi, phím tắt sau TAB), cờ `MF_GRAYED`/`MF_CHECKED`, command id -> id mô hình |
| `src/PhotoReview.Shell.Win32/Menus/DarkModeMenus.cs` | uxtheme `SetPreferredAppMode(ForceDark)` + `FlushMenuThemes` + `AllowDarkModeForWindow` qua ordinal; thiếu API/build < 18362 -> false, menu sáng |
| `src/PhotoReview.Shell.Interop/Win32/Kernel32Loader.cs` | thêm `LoadLibraryExW` (System32) + `GetProcAddress` theo ordinal (LibraryImport, L-PINV) |
| `tests/PhotoReview.Shell.Tests/Menus/*` | 4 file test (xem mục 3) |

Không có chuỗi i18n mới (mọi khoá `main.menu.*`/`main.tools.*` đã có). MainWindow WPF không đổi.

## 2. Quy ước id (G-MENU / WP-24 phải dùng đúng)

- Mục cấp trên và con trực tiếp của Zoom: `ContextMenuItemId.ToString()` (`Undo`, `MoveToRecycleBin`, `Fit`, `ZoomToLevel`, `ZoomSubmenu`, `Refresh`,
  `OpenFolder`, `NextFolder`, `PreviousFolder`, `ExternalEditor`, `CopyFileName`, `CopyFullPath`, `Settings`; trong Zoom: `ZoomFitWidth`, `ZoomFitWidth2`, `ZoomFitHeight`).
- Con của nhóm `ZoomPresets`: `Zoom.Preset.50|70|100|150|200|300|400` (Toggle) + `Zoom.Custom` (Command). Nhóm `ZoomLevelOptions`: `Zoom.AlsoSetClickLevel` (Toggle),
  `Zoom.SetCurrentAsClickLevel` (Command).
- Dấu phân cách (`Kind = Separator`, `IsEnabled = false`): `Separator.<N>` trước nhóm N ở cấp trên (N = 2..6, đúng `HasSeparatorBefore(N)`), `Separator.Zoom.<N>` trong submenu Zoom.
- Menu Tools: `Tools.Recovery|Diagnostics|Benchmark|ClearCache|RemoveNumberedDuplicates|RemoveOriginalDuplicates`.
- Văn bản = `Tr.*` hiện tại; phím tắt = chuỗi `AppSettings.Shortcuts` y như WPF `InputGestureText` (rỗng -> null); chỉ Recycle/Fit/Zoom to N%/Refresh/Next/Previous và
  3 mục Fit width/height có phím tắt, như `ImageContextMenu_Opened` + `ZoomMenu_SubmenuOpened`.

## 3. Test và mutation

- `ContextMenuModelBuilderTests` (HotPath, 17 ca): danh sách id viết tay theo XAML cho mặc định / không ẩn gì / không ảnh / chỉ còn Settings / cờ cũ (`HiddenContextMenuItems == null`) /
  submenu rỗng bị bỏ; **property test** trên ~10 nghìn tập ẩn x có/không ảnh: so từng entry với `ContextMenuItems.Compute`, không dấu phân cách đầu/cuối/đôi, không submenu rỗng;
  văn bản/phím tắt/AutomationName từ `Tr` + settings; preset được chọn = `ClickZoomPercent`; "Set current" tắt khi Fit; id duy nhất.
- `NativeMenuMappingTests`, `DarkModeMenusTests` (HotPath): hàm xuất giả bằng `[UnmanagedCallersOnly]`, kiểm mode = ForceDark, flush, gọi một lần, rơi về sáng khi thiếu ordinal/build cũ.
- `NativePopupMenuHostTests` (`Category=Native`, chạy trên desktop ẩn qua `tools/run-tests-hidden.ps1`): cửa sổ chủ lớp STATIC không hiện; phím `WM_KEYDOWN` được `PostMessage` vào hàng đợi
  trước khi mở menu: Esc -> null, Down+Enter -> id mục đầu, Down,Right,Down,Enter -> id con của submenu, mục xám -> null; danh sách rỗng/không cửa sổ chủ -> null.
- Mutation: xem mục 5.

## 4. Chỗ lệch khỏi thẻ / điều lead cần biết

1. **Undo và Open in External Editor luôn bật** (như WPF), dù `ContextMenuBuildContext` có `CanUndo`/`CanOpenInExternalEditor`. Lý do: bản WPF không bao giờ tắt chúng; chưa
   cấu hình editor thì nhấn mở Settings (`MainViewModel.OpenInExternalEditor`). `CanUndo`, `IsFit`, `IsCompareVisible` hiện không đổi mục nào (`IsFit` chỉ ngầm qua `EffectiveZoom = null`).
   Nếu muốn Undo xám khi không có gì để hoàn tác thì chỉ cần đổi một dòng ở builder (`isEnabled: context.CanUndo`) - cần quyết định riêng vì khác WPF.
2. `HasImage && CanCopyPath` quyết định hiện Copy File Name/Full Path (= `MainViewModel.CanCopyCurrentFilePath` của WPF). Mọi mục khác không phụ thuộc có ảnh.
3. Menu Tools: hai mục xoá trùng tắt khi không có ảnh (WPF: bấm là no-op vì catalog rỗng); các mục còn lại luôn bật. Không có phím tắt.
4. `NativePopupMenuHost` là `internal` (host được WP-24 tạo trong cùng assembly `PhotoReview`); constructor nhận `Func<nint>` cửa sổ chủ (không có HWND trong `IPopupMenuHost.Show` của hợp đồng).
   Tọa độ vào là DIP của DPI cửa sổ chủ (`GetDpiForWindow`), không theo màn hình đích.
5. Mục xám (`MF_GRAYED`): Win32 cho di chuyển bằng phím tới mục xám nhưng Enter không trả lệnh.
6. Menu tối bật cho cả tiến trình (`SetPreferredAppMode` là cài đặt toàn tiến trình) - đúng mục đích vì shell chỉ có menu của chính nó; màu không khớp hẳn `DarkMenus.xaml` (đã nêu ở NE-4).
7. Test tự động chưa kiểm được màu menu tối thật (cần nhìn bằng mắt: mục "menu chuột phải tối" của kịch bản thủ công WP-27b).

## 5. Mutation (kết quả)

Chạy từng đột biến trên mã nguồn rồi chạy `Shell.Tests` mục `Menus` trên desktop ẩn (hoàn nguyên sau mỗi lần; 12/13 bị bắt, 1 đột biến tương đương):

| # | Đột biến | Kết quả |
|---|---|---|
| M1 | builder thay mục phân cách bằng một mục khác | bắt (7 ca fail) |
| M2 | preset không bao giờ được chọn | bắt (1) |
| M3 | "Set current zoom" luôn bật | bắt (1) |
| M4 | bỏ qua tập ẩn (`EffectiveHidden`) | bắt (5) |
| M5 | bỏ qua điều kiện có ảnh của nhóm Copy | bắt (2) |
| M6 | hai mục xoá trùng luôn bật khi không ảnh | bắt (1) |
| M7 | không nhân đôi `&` trong chữ menu | bắt (1) |
| M8 | command id lệch một (đổi về id mô hình) | bắt (7) |
| M9 | bỏ cờ `MF_CHECKED` | bắt (1) |
| M10 | bỏ chặn build Windows < 18362 | bắt (1) |
| M11 | `SetPreferredAppMode(1)` thay vì ForceDark | bắt (1) |
| M12 | command id đưa vào `AppendMenu` lệch một | bắt (2) |
| M13 | bỏ chốt `items.Count == 0` ở host | **sống sót, tương đương**: menu rỗng làm `TrackPopupMenuEx` trả 0 ngay -> vẫn null; chốt chỉ tránh gọi OS |

## 6. G-MENU

WP-10 (golden từ bản WPF) chưa merge lúc làm gói này nên chưa có `context-menu.v1.json`. Khi WP-10 merge: dựng `ContextMenuBuildContext` từ `SettingsOverridesJson` của từng
`GoldenMenuCase` rồi so `VisibleIdsInOrder` với id theo quy ước mục 2 (dấu phân cách theo id `Separator.*`); nếu golden dùng quy ước id khác thì sửa bộ chuyển đổi
trong test, KHÔNG sửa golden.
