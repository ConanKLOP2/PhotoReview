---
id: NOWPF-WP07
order: 232
summary: |-
  WP-07 (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): thực thi C-06 (RectD.Contains, KeyIdMapping bằng bảng sinh từ KeyInterop và so lại với WPF bởi test G-KEY), adapter WPF của C-04 (DispatcherUiScheduler : IUiDispatcher, mức ưu tiên ánh xạ theo tên), và sửa tại chỗ C-07 (IImageSurface/PointerInputController/ShortcutRouter/MouseGestures/WheelMessageSource/ShortcutKeyName dùng PointD/KeyId/KeyModifiers/PointerButton; ViewportSnapshot dùng bool thay Visibility). Không còn using System.Windows trong App/Input; hành vi WPF y nguyên; đề xuất hợp đồng v1.1 cho C-07.
---

# NOWPF-WP07-INPUT-SEAM - input và seam UI không-WPF (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) mục 5 (C-04, C-06, C-07), thẻ WP-07 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).

## 1. Đã làm

| Hợp đồng | Thực thi | Ghi chú |
|---|---|---|
| C-06 `RectD.Contains` | `App.Shared/Input/InputPrimitives.cs` | Ngữ nghĩa `Rect.Contains(Point)` (biên đóng); `Width`/`Height` âm = rỗng (WPF không dựng được Rect âm, nên đây là cách duy nhất khớp `Rect.Empty`). |
| C-06 `KeyIdMapping` | `App.Shared/Input/KeyIdMapping.cs` | Bảng VK->KeyId và KeyId->VK sinh một lần từ `KeyInterop` (WPF .NET 10), kiểm 0..255 và mọi giá trị `Key` bởi `KeyIdMappingTests` (App.Tests, cần WPF). `TryParse` = `ShortcutKeyCanonical` + `Enum.TryParse(ignoreCase: false)` + `IsDefined`, từ chối số/danh sách. |
| C-04 (WPF) | `App/Services/DispatcherUiScheduler.cs` | Implement `IUiDispatcher`; `Send/Normal/Render/Background` -> `DispatcherPriority` cùng tên; giá trị không định nghĩa -> Normal. `Post/InvokeAsync/YieldAsync` không mức = Normal/Normal/Background như cũ. |
| C-07 (WPF) | `App/Input/*`, `Services/{WpfImageSurface,ShortcutKeyName}.cs`, `MainWindowConvergence.cs` | Xem mục 2. Adapter `Services/WpfInputAdapters.cs`: `ToPointD/ToWpfPoint/ToKeyId/ToPointerButton/ToKeyModifiers`. |

`MainWindow.xaml.cs` chỉ đổi các handler input (`ImageScroll_PreviewMouseWheel`, `WindowMessageHook`, `MainImage_*`, `Window_KeyDown`): gọi adapter
trước khi chuyển cho controller. Không đổi `config.json`, không đổi hành vi.

## 2. Chữ ký C-07 đã sửa tại chỗ (đề xuất khoá ở v1.1)

- `IImageSurface`: `PointD ImageOrigin`, `PointD ToImageElement(PointD)`, `PointD? PointerPosition`; các thành viên còn lại giữ nguyên (kể cả
  `SourceSize`, `HookRenderFrame/UnhookRenderFrame/RenderingTime(EventArgs)`, `Timestamp`, `DisplayTiming`).
- `IFitSurface`: giữ nguyên thành viên; chỉ `ViewportSnapshot` đổi.
- `ViewportSnapshot`: `HorizontalScrollbarVisibility/VerticalScrollbarVisibility` (`Visibility`) -> `HorizontalScrollBarVisible/VerticalScrollBarVisible`
  (`bool`; WPF adapter: `ComputedXxxScrollBarVisibility == Visibility.Visible`). `ToString()` vẫn in `Visible`/`Collapsed` (log chẩn đoán không đổi).
- `PointerInputController`: `OnImagePress(PointerButton, int, PointD, int)`, `OnImageMove(bool, PointD, int)`, `OnImageRelease(PointD, int)`,
  `OnWheelAsync(WheelInput, PointD)` + quá tải `(int, bool, PointD)`, `TryPanByArrow(KeyId, bool)`.
- `ShortcutRouter.TryResolve(KeyId, KeyId, KeyModifiers, ...)`; `PointerGestures.ResolveKeyboardZoomAnchor(..., PointD?, ...) -> PointD`;
  `WheelMessageSource.ScreenPoint(nint) -> PointD`; `ShortcutKeyName.TryParse(string?, out KeyId)` và `IsReserved(KeyId)` (không còn quá tải `Key`:
  `SettingsWindow`/`ActionProfilesWindow` tự chuyển `Key -> KeyId`).

## 3. Lệch khỏi thẻ / hợp đồng

1. **Vị trí của C-07**: kế hoạch ghi `IImageSurface`/`IFitSurface`/`ViewportSnapshot` ở `App.Shared`. Ở WP-07 chúng vẫn `internal` trong
   `PhotoReview.App` (namespace giữ nguyên): `PointerInputController`, `FitViewController`, `ViewerState` còn nằm trong App, dời chúng là việc của WP-16/WP-24
   cùng lúc chúng chuyển sang Shared. Chữ ký không-WPF đã sẵn sàng để dời mà không đổi.
2. **`FromVirtualKey(vk, isExtended)`**: khớp `KeyInterop.KeyFromVirtualKey` mọi mã; thêm đúng một quy tắc của bộ cấp phím WPF
   (`VK_CONTROL`/`VK_MENU` + extended -> `RightCtrl`/`RightAlt`). `VK_SHIFT` luôn là `LeftShift`; bên phải phải được người gọi đưa vào dưới dạng
   `VK_RSHIFT` (từ scan code) - cờ extended không phân biệt được Shift. WP-14 (nơi nhận WM_KEYDOWN) cần biết điều này.
3. **`KeyIdMapping.TryParse` so với `ShortcutKeyName.TryParse`**: hai hàm khác nhau có chủ ý. Cái đầu theo hợp đồng (`ignoreCase: false` sau khi
   chuẩn hoá bí danh, không loại phím dành riêng); cái sau giữ hành vi cũ của cấu hình (không phân biệt hoa thường, loại Escape/Tab/modifier...).
4. **`LineFeed`/`System` không có mã ảo**: `ToVirtualKey` trả 0 cho `None`, `LineFeed`, `System`, `DeadCharProcessed` (giống `KeyInterop`).

## 4. Test

- Mới: `KeyIdMappingTests` (G-KEY: 0..255 + ngoài dải; Control/Alt extended; mọi `Key`; `TryParse`), `InputPrimitivesTests` (`RectD.Contains` đối chiếu
  `Rect.Contains` trên lưới biên/góc/kích thước 0; adapter không mất), `DispatcherUiSchedulerTests` mở rộng (ánh xạ mức, thứ tự thực thi theo mức,
  `YieldAsync(priority)`, `CheckAccess`).
- Đổi kiểu, không đổi assert: `PointerInputControllerTests*`, `ShortcutRouter*Tests`, `MouseGestures*Tests`, `ShortcutKeyNameTests`, `ViewportConvergenceTests`,
  `FitViewController*Tests`, `WpfImageSurfaceTests` (2 nơi), `KineticPanFrameMeasurementTests`.
