---
id: NOWPF-WP16
order: 362
summary: |-
  WP-16 (2026-10-10): engine viewport thuần C-08 (ViewportLayoutEngine, khớp bit-for-bit layout WPF thật trên 2.660 ca), ViewportState (layout trễ, offset yêu cầu được nhớ), ViewportController Win32 (thành viên C-07, chờ v1.1) và ArrowPanRule (pan phím mới: bước = % x cạnh ngắn); golden G-VIEW/G-INPUT tự chạy khi WP-10 commit file. Không merge trước WP-10.
---

# NOWPF-WP16-VIEWPORT-ENGINE - engine viewport thuần + ViewportController (2026-10-10)

Thẻ: WP-16 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); hợp đồng C-07/C-08 và tiêu chí 7.3 trong [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md).
App WPF **không đổi hành vi**: không file nào dưới `src/PhotoReview.App` bị sửa; engine đứng cạnh, chưa nối vào app.

## 1. Đã làm

| File | Nội dung |
|---|---|
| `src/PhotoReview.App.Shared/Viewport/ViewportLayoutEngine.cs` | Thân `Compute`/`ClampOffset` (C-08) + `ViewportLayoutMath` (internal). Tái tạo đúng các bước WPF: `FrameworkElement.MeasureCore`/`MinMax`, `Image.MeasureArrangeHelper` + `Viewbox.ComputeScaleFactor` (Uniform -> Uniform, None -> Fill qua `ViewerStretchModeConverter`), `ScrollViewer.MeasureOverride` Auto/Auto (lượt 1 cả hai thanh ẩn, lượt 2 khi có thanh, lượt 3 chỉ khi đúng một thanh), `ScrollContentPresenter` (con đo vô hạn hai trục, ô = max(extent, viewport), `CoerceOffset`), `ArrangeCore` (kẹp, Stretch coi như Center), `DoubleUtil.AreClose/GreaterThan/IsZero`. Không làm tròn layout (MainWindow không bật `UseLayoutRounding`). Overlay/Hidden (NE-3) chỉ cho bản Win32. |
| `.../Viewport/ViewportContracts.cs` | `ViewportLayoutEngine` thành `static partial` (chữ ký không đổi; `ContractSurfaceTests` xanh, file duyệt không sửa). |
| `.../Viewport/ViewportState.cs` | Trạng thái offset + layout: input/`ScrollTo` chỉ áp ở `UpdateLayout` (như hàng đợi lệnh cuộn + lượt layout WPF); offset **yêu cầu** được nhớ, chỉ offset hiệu lực bị kẹp (ScrollContentPresenter `_offset` vs `_computedOffset`) - co rồi giãn viewport trả offset về chỗ cũ. |
| `.../Viewport/ArrowPanRule.cs` | Quy tắc pan phím **mới** (lead báo 2026-10-10): bước DIP bằng nhau hai trục = `ArrowPanStepPercent`% x min(ViewportWidth, ViewportHeight); kẹp [0, Max], ngưỡng 0,5 DIP như `KeyboardPan`. Xung lượng kinetic tự theo vì `PointerInputController` tính từ quãng `target - current`. |
| `src/PhotoReview.Shell.Win32/Viewing/ViewportController.cs` | Bản Win32 của ImageScroll + MainImage trên `IShellWindow` + `IUiDispatcher` + `IFrameClock`: đủ thành viên của `IImageSurface`/`IFitSurface` (PointD), lượt layout gộp ở `UiPriority.Render`, `YieldToRenderAsync` = `InvokeAsync(noop, Render)`, resize -> UpdateFitSize, nhánh `MainImage_SizeChanged` khi Fit, DragThreshold = SM_CXDRAG / DPI, PointerPosition trong viewport, khung render qua `FrameTickEventArgs`. |

## 2. Test

| Test | Ở đâu | Chứng minh |
|---|---|---|
| `ViewportEngineWpfParityTests` (`Category=UI`, STA, không cửa sổ) | App.Tests | ScrollViewer + Image **WPF thật** với `Themes/DarkScrollBars.xaml` thật: lưới 7 client x 4 DPI x 5 ảnh x 19 chế độ = 2.660 ca, lệch lớn nhất **0 DIP**; 19 ca biên (lượt đo 3, AreClose, client < thanh, không bitmap, Fill auto-size, bitmap 72/300 DPI) lệch <= 8e-5; kẹp offset và "offset yêu cầu được nhớ" khớp WPF. Đối chứng tại chỗ, không thay golden. |
| `ViewportLayoutEngineTests`, `...PropertyTests` (20.000 input/thuộc tính) | Shell.Tests | Giá trị tính tay; offset luôn trong [0, Max]; thanh Auto hiện đúng khi tràn viewport của nó; ảnh trong ô, căn giữa; Fit giữ tỉ lệ, chạm một cạnh, không cuộn. |
| `ViewportStateTests`, `ViewportControllerTests`, `ArrowPanRuleTests` | Shell.Tests | Layout trễ/gộp, thứ tự Render, DPI, con trỏ, capture, khung render, dispose; quy tắc pan mới. |
| `EngineDrivenViewportTests` | App.Tests | `FitViewController` + `PointerInputController` **thật, không sửa** trên engine (adapter `EngineImageSurface`): Fit ổn định ở **pass 2** (trace đúng thứ tự T89), resize giữa pass -> **pass 3**; MaxImage cũ tràn được `SizeChanged` kéo về; wheel/phím/click zoom giữ điểm ảnh (con trỏ, tâm); FitWidth neo 1/3 trên/giữa/1/3 dưới ở DPI 1/1,5/2 + hiệu chỉnh thanh cuộn bên; FitHeight panorama; kéo pan, kinetic (mọi khung trong [0, Max], tự dừng ở mép), mũi tên; đổi bitmap RAW giữ tâm. |
| `ViewportGoldenTests` (G-VIEW), `ViewportInputGoldenTests` (G-INPUT) | Shell.Tests, App.Tests | `[GoldenFact]`: **tự Skip** khi `tests/Fixtures/golden/*.v1.json` chưa có, tự chạy khi WP-10 commit file (không phải sửa test). Bộ đọc + bộ so sánh + runner kịch bản có test riêng chạy ngay. |

Mutation: xem mục 5.

## 3. Lệch khỏi thẻ

1. `ViewportState.cs` ở **App.Shared/Viewport** (thẻ: `Shell.Win32/Viewing`): để App.Tests chạy controller thật của App trên cùng lõi; vẫn trong vùng xung đột của WP-16.
2. `ViewportController` **chưa khai báo** `: IImageSurface, IFitSurface` và chưa có `Capture()`: hai interface + `ViewportSnapshot` (C-07) còn trong App (WPF), chưa khoá, do WP-07 sở hữu. Thành viên đã trùng tên/ngữ nghĩa.
3. `ViewportInputGoldenTests` ở **App.Tests** (thẻ: Shell.Tests) vì `PointerInputController` còn ở App tới WP-09. `FitViewControllerTests`/`MutationGapTests` không chạy lại nguyên văn trên `ViewportController` (cần C-07 v1.1); thay bằng `EngineDrivenViewportTests` chạy controller thật trên lõi engine.
4. Bộ đọc golden tạm ở `Shell.Tests/Viewport/GoldenFixture.cs` + `App.Tests/Viewport/InputScriptRunner.cs` (TestSupport/Golden là vùng của WP-10); đọc khoan dung (mảng gốc hoặc object bọc mảng, NaN/Infinity dạng chuỗi). Khi WP-10 merge: đổi sang `GoldenFile` của nó (hai chỗ) và **đối chiếu từ vựng bước G-INPUT** (doc của `InputScriptRunner`); bước lạ làm test đỏ.

## 4. Phát hiện (hành vi hiện có của bản WPF, không sửa - N-1)

- **FitWidth/FitHeight lệch nửa thanh cuộn:** pass hiệu chỉnh của `CorrectForSideScrollbarAsync` đọc `ViewportCentre` khi **cả hai** thanh đang hiện (viewport tạm 1270x710 ở client 1280x720), nên điểm neo nằm ở y = 355 (FitWidth) / x = 635 (FitHeight), lệch 5 DIP so với tâm cuối. Golden WP-10 sẽ ghi đúng điều này; bản Win32 giữ nguyên để khớp. Nếu muốn sửa: một PR riêng trên `PointerInputController` (đọc tâm sau layout), không thuộc WP-16.
- **Fit tự sửa trong cùng layout:** MaxImage cũ làm ảnh Fit tràn được `MainImage_SizeChanged -> UpdateFitSize` kéo về ngay trong lượt layout; vòng 3 pass của `FitViewController` vẫn cần cho resize xảy ra giữa các lần nhường Render (test chứng minh pass 3). **Không rút gọn** vòng T89.
- **Fill khi chưa biết kích thước nguồn** (Stretch=None, ImageWidth=NaN): ảnh đo theo bitmap rồi **giãn theo ô** (méo) - đúng WPF, chỉ xảy ra trong khoảnh khắc chưa có SourcePixelWidth.
- **Bitmap DPI khác 96** (mục 10 của kế hoạch): chỉ tỉ lệ ảnh hưởng Fit; lệch đo được <= 8e-5 DIP (làm tròn pixel của bitmap thử).

## 5. Mutation

Stryker (Basic, filter chuẩn) phạm vi `ViewportLayoutEngine.cs`, `ViewportState.cs`, `ArrowPanRule.cs` (từ Shell.Tests) và `ViewportController.cs`: kết quả ghi trong PR. Ngoài ra kiểm thủ công nhánh quan trọng trên App.Tests (mục PR).

## 6. Đề xuất hợp đồng v1.1 (lead)

- **C-07:** dời `IImageSurface`, `IFitSurface`, `ViewportSnapshot` (trường thanh cuộn thành `bool`), `ViewportConvergence`, `ViewportOperationVersion` sang App.Shared (WP-07/WP-09), kiểu toạ độ `PointD`. Khi đó `ViewportController` thêm `: IImageSurface, IFitSurface` + `Capture()` (một hunk) và `EngineImageSurface` của App.Tests bỏ đi; `FitViewControllerTests` chạy nguyên văn trên `ViewportController`.
- **C-08 (tuỳ chọn):** `ViewportLayout` thêm `bool IsLayoutRounded = false` chỉ khi sau này bật `UseLayoutRounding` (hiện không cần).
- **C-17:** WP-10 nên ghi rõ: `AfterStep` 0-based (-1 = sau InitialViewMode), `frame` dùng `TimestampMs` làm RenderingTime, tên lệnh cho `command`; và ghi lại pan phím sau PR quy tắc mới (golden pan phím cũ không phải chuẩn).

## 7. Việc tiếp

WP-10 merge -> merge origin/master vào nhánh này, chạy golden thật (không sửa golden để xanh) rồi mới merge. WP-22 nối `ArrowPanRule` cho bản Win32 và interop con trỏ/SM_CXDRAG vào `ViewportControllerOptions`.
