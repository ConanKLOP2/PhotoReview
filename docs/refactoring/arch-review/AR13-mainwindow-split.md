# AR13 — Tách `MainWindow.xaml.cs`: `PointerInputController` và `FitViewController`

**Finding:** F16 · **Quyết định:** không cần · **Kích thước:** ~1.5 ngày, 1 PR `refactor/ar13-mainwindow-split` (2 commit: 13a pointer, 13b fit) · **GUI:** **có** — pan, kinetic glide, wheel zoom/next, click-to-zoom, Fit (T89) · **Agent:** strongest (layout/gesture là vùng T89) · **Không chạy song song** với task nào khác sửa `MainWindow.xaml.cs` (AR19).

## Hiện trạng

`src/PhotoReview.App/MainWindow.xaml.cs` (708 dòng) gộp:

| Trách nhiệm | Dòng | Trạng thái riêng |
|---|---|---|
| Lifecycle, DPI, placement, đóng khi file action xong | 69–118, 203–262, 269–281 | `_cachedDpiScale`, `_placementRestored` |
| **Pointer**: wheel (zoom/next/prev), zoom tại điểm, pan, kinetic glide, click-to-zoom, capture | 283–548 (~265 dòng) | 17 field: `_isPanning`, `_panMoved`, `_panStartPoint`, `_panLastPoint`, `_pressCanPan`, `_pressConsumed`, `_pressStoppedGlide`, `_pressTimestamp`, `_wheelGestures`, `_panVelocity`, `_kinetic`, `_kineticFrameHandler`, `_kineticHooked`, `_hasKineticFrame`, `_lastKineticFrame`, `_lastSeenIndex`, `_settings` (đọc `MouseWheelAction`, `ClickToZoomEnabled`, `ClickZoomPercent`, `KineticPanEnabled`) |
| **Fit**: `ApplyFitViewAsync` vòng hội tụ 3 pass, `CaptureViewportSnapshot`, `UpdateFitSize`, `UpdateTargetDecodeBox` | 172–201, 646–698 | `_viewportOperationVersion` |
| Phím tắt `Window_KeyDown` switch 20 case | 565–616 | — |
| Drag-drop, menu/toolbar forward | 551–563, 637–706 | — |

Phần toán thuần đã tách và có test: `Input/MouseGestures.cs` (`WheelGestureInterpreter`, `PanVelocityTracker`), `Input/KineticPan.cs` (`KineticScroller`), `MainWindowHelpers` (`ZoomImagePoint`), `MainWindowConvergence.cs` (`ViewportConvergence.IsStableViewport`, `ViewportSnapshot`) với `tests/PhotoReview.App.Tests/Input/*`, `ViewportConvergenceTests.cs`. Cái chưa tách là **máy trạng thái** nối chúng với `ScrollViewer`/`Image`/`Dispatcher`.

## Thiết kế

Nguyên tắc: controller **không** giữ tham chiếu `Window`; nhận một interface nhỏ mô tả bề mặt view để test bằng fake, còn `MainWindow` chỉ còn adapter WPF + wiring.

### 13a — `PointerInputController` (`src/PhotoReview.App/Input/PointerInputController.cs`)

```csharp
internal interface IImageSurface            // adapter do MainWindow hiện thực
{
    double HorizontalOffset { get; } double VerticalOffset { get; }
    double ViewportWidth { get; } double ViewportHeight { get; }
    double ExtentWidth { get; } double ExtentHeight { get; }
    void ScrollTo(double h, double v);
    Point PositionIn(MouseEventArgs e);      // e.GetPosition(ImageScroll)
    void CaptureMouse(); void ReleaseMouse();
    void HookRenderFrame(EventHandler h); void UnhookRenderFrame(EventHandler h); // CompositionTarget.Rendering
    TimeSpan RenderTime(EventArgs e);        // ((RenderingEventArgs)e).RenderingTime
}
internal sealed class PointerInputController(IImageSurface surface, MainViewModel vm, Func<AppSettings> settings, Func<Point, Task> zoomAtPoint, Func<Point, Task> clickZoom)
{
    public Task OnWheel(int delta, bool ctrl, Point at);
    public void OnWindowPreviewMouseDown(int timestamp);           // Window_PreviewMouseDown
    public void OnImagePress(Point at, int timestamp, int clickCount);
    public void OnImageMove(Point at, bool leftDown);
    public Task OnImageRelease(Point at, int timestamp);
    public void OnLostCapture(); public void CancelPan(); public bool StopKinetic();
}
```

- Chuyển nguyên khối 283–548 vào controller, đổi `ImageScroll.X` → `surface.X`, `_viewModel` → `vm`, `_settings` → `settings()`. `ZoomAtPointAsync`/`CaptureZoomAnchor` (318–366) cũng chuyển (chúng chỉ cần offset/viewport + `Viewer`).
- `MainWindow` giữ 6 handler một dòng: `ImageScroll_PreviewMouseWheel` → `await _pointer.OnWheel(e.Delta, ctrl, e.GetPosition(ImageScroll))`, v.v. `Window_Closed` gọi `_pointer.Dispose()` (unhook render frame).
- `_settings` shadow field trong `MainWindow` (`:29`) chỉ còn dùng bởi `_shortcutRouter` → xem xét bỏ, đọc `_settingsStore.Current` trực tiếp.

### 13b — `FitViewController` (`src/PhotoReview.App/Coordinators/FitViewController.cs`)

```csharp
internal interface IFitSurface
{
    (double Width, double Height) ViewportSize { get; }   // GetViewportSize()
    void UpdateLayout(); Task YieldToRenderAsync();        // Dispatcher.InvokeAsync(() => {}, Render)
    ViewportSnapshot Capture();                            // CaptureViewportSnapshot()
    void ScrollHome(); bool IsLoaded { get; }
}
internal sealed class FitViewController(IFitSurface surface, ViewerState viewer, Action cancelPan)
{
    public async Task ApplyFitAsync();   // nguyên vòng 646–678, version guard bên trong
    public long OperationVersion { get; }
}
```

- **Giữ nguyên vòng hội tụ (tối đa 3 pass, dừng sớm khi ổn định)** (rule "No `ApplyFitViewAsync` single-pass without T89 evidence"): chỉ di chuyển, không đổi số pass, không đổi thứ tự `UpdateLayout → UpdateFitSize → yield Render → snapshot`.
- `UpdateFitSize`/`UpdateTargetDecodeBox` (176–201) ở lại `MainWindow` (chúng gắn `ViewportSizeSource` và `SizeChanged`); controller gọi qua `surface`.

### Sau khi tách

`MainWindow.xaml.cs` mục tiêu ≤ 350 dòng: ctor + wiring, lifecycle/DPI/placement, `Window_KeyDown`, drag-drop, menu forward, hai adapter `IImageSurface`/`IFitSurface` (private nested class hoặc `MainWindow` hiện thực trực tiếp).

## Tests (mới, không STA, dùng fake surface)

- `tests/PhotoReview.App.Tests/Input/PointerInputControllerTests.cs`
  - Wheel + `MouseWheelAction.Zoom` → gọi `zoomAtPoint`; `Ctrl` đảo sang next/prev theo `WheelGestureInterpreter` (mutation: bỏ `StopKinetic()` đầu `OnWheel` → test "wheel dừng glide" đỏ).
  - Press → move quá ngưỡng → release: `surface.ScrollTo` được gọi với offset đúng; release nhanh (< ngưỡng) không zoom khi `_panMoved`.
  - Click (không move) + `ClickToZoomEnabled` → `clickZoom`; tắt → không.
  - Glide: release với vận tốc → `HookRenderFrame`; đóng cửa sổ/`StopKinetic` → `Unhook` (mutation: bỏ unhook → đỏ). Ghi chú khi làm: code thật không dừng glide khi mất capture; giữ nguyên hành vi đó.
  - `Dispose` unhook khi đang glide.
- `tests/PhotoReview.App.Tests/Coordinators/FitViewControllerTests.cs`
  - Snapshot ổn định sau pass 2 → không chạy pass 3 (đếm `Capture`); không ổn định → đủ 3 pass rồi `ScrollHome` (mutation: đổi `pass < 3` thành `< 1` → đỏ).
  - Version tăng giữa chừng (gọi `ApplyFitAsync` lần 2) → lần 1 dừng, không `ScrollHome` hai lần.
- Test kiến trúc: `MainWindow.xaml.cs` không chứa `CompositionTarget.Rendering` và `_isPanning` (khoá không cho logic quay lại code-behind; thêm vào `StructureOptimizeRulesTests` theo pattern có sẵn).
- Giữ nguyên `tests/PhotoReview.Integration.Tests/MainWindowBehaviorTests.*` (STA) — chúng là gate hành vi; phải xanh không sửa.

## GUI check (người dùng, sau khi merge)

1. Zoom > Fit, kéo pan, thả nhanh → glide và dừng khi chạm biên. 2. Wheel: zoom tại con trỏ; Ctrl+wheel: chuyển ảnh (theo `MouseWheelAction`). 3. Click đơn với `ClickToZoomEnabled` bật → zoom `ClickZoomPercent` tại điểm. 4. Fit: mở folder, resize cửa sổ, DPI đổi (kéo sang màn hình khác) → ảnh khớp viewport, không thanh cuộn thừa (T89 checklist).

## Verification / Acceptance

- Gate chung + integration STA tests. Không cần perf gate (không chạm decode/present).
- `wc -l MainWindow.xaml.cs` ≤ 350; hai controller có test; GUI check 1–4 do người dùng xác nhận.

## Rủi ro và rollback

- Rủi ro chính: sai lệch thứ tự `e.Handled`/capture khi chuyển handler → hiện tượng "kẹt pan". Giảm bằng cách giữ nguyên thân method, chỉ đổi tham chiếu; commit 13a và 13b tách rời để revert từng phần.
- Rollback: revert PR; không có migration dữ liệu.

## Không thuộc phạm vi

`Window_KeyDown` (đã dùng `ShortcutRouter`), `SettingsWindow` 596 dòng (AR11b thêm test, không tách), `ImagePresenter.PresentAsync` dài (biên imaging, không đổi).
