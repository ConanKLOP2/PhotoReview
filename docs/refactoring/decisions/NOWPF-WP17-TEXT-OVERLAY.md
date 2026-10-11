---
id: NOWPF-WP17
order: 670
summary: |-
  WP-17 (đợt 2 NO-WPF, 2026-10-11): chữ DirectWrite + bộ overlay + animator cho shell Win32 (C-11), không đổi bản WPF. `Shell.Rendering/Text`: `DWriteTextRenderer` (ITextRenderer, cắt "…" một dòng, ngắt dòng khi không cắt), `TextLayoutCache` (LRU theo text/style/maxWidth/trimming, lease đếm ref, layout độc lập thiết bị nên sống qua mất thiết bị). `Shell.Win32/Overlay`: `OverlayElement/PanelElement/TextElement/ButtonElement/OverlayHost` (bố cục đo-đặt kiểu WPF, hit-test bỏ phần tử ẩn/opacity 0), `Animator` (tween opacity theo `IFrameClock` tiêm được, FadeTo huỷ tween cũ), `DarkPalette` (khớp từng khoá XAML). G-OVL: 5 panel x 12 điều kiện = 60 ca lệch tối đa 0,017 DIP so với WPF (thẻ cho 2 DIP); ToolbarPanel để WP-23. Perf GPU: layout miss P50 0,10 ms, hit ~0, khung overlay submit P50 0,47 / P95 1,4 ms. Hợp đồng v1.1 không đổi.
---

# NOWPF-WP17-TEXT-OVERLAY - chữ DirectWrite + overlay + animator (2026-10-11)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (C-11), thẻ WP-17 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Dựa trên renderer [NOWPF-WP15](NOWPF-WP15-D2D-RENDERER.md) (`INativeTextLayout` đã định nghĩa ở đó), interop
[NOWPF-WP13B](NOWPF-WP13B-INTEROP-GRAPHICS.md), golden [NOWPF-WP10](NOWPF-WP10-GOLDEN-RECORDER.md). Hợp đồng C-11 giữ nguyên
(`ContractSurfaceTests` xanh, không sửa file duyệt). Số đo: [perf/2026-10-11-wp17-text-overlay.md](../perf/2026-10-11-wp17-text-overlay.md).

## 1. Đã làm

| File | Nội dung |
|---|---|
| `Shell.Interop/Graphics/DWriteAdditions.cs` | CHỈ THÊM: `IDWriteFactory::CreateEllipsisTrimmingSign` (slot 20 WP-13b giữ chỗ) gọi qua vtable thô, cùng cách `D2D1RawCalls` |
| `Shell.Rendering/Text/DWriteTextRenderer.cs` | `ITextRenderer` bằng DirectWrite (factory Shared); `DWriteTextLayoutLease` implement `ITextLayout` + `INativeTextLayout` |
| `Shell.Rendering/Text/TextLayoutCache.cs` | LRU theo `(text, family, size, bold, maxWidth, trimming)`, đếm ref; layout bị loại còn lease thì sống tới lease cuối |
| `Shell.Rendering/D2DDrawContext.cs` | MỘT dòng: `DrawText` dùng `D2dDrawTextOptions.EnableColorFont` (emoji 📂/⚙ của toolbar vẽ màu như WPF) |
| `Shell.Win32/Overlay/OverlayElement.cs` | gốc phần tử: Measure/ArrangeIn (Margin, căn lề, MaxWidth, ẩn = collapsed), `Opacity` kẹp 0..1, `HitTest`, `FindAt`, `Find` |
| `PanelElement.cs` / `TextElement.cs` / `ButtonElement.cs` | `Border`+`StackPanel`, `TextBlock` (giữ lease tới khi đổi chữ/style/rộng), nút toolbar (nền theo trạng thái, `Clicked`) |
| `OverlayHost.cs` | z-order, `Arrange`/`Render`/`HitTest` (phần tử sâu nhất trên cùng), `Resolve(id)` cho animator, `Dispose` trả lease |
| `Animator.cs` | `IAnimator` + `IOpacityTarget`; đồng hồ khung tiêm được, `ticksPerSecond` tiêm được (test: 1 tick = 1 ms) |
| `DarkPalette.cs` | 35 màu `Dark.*` sao từ `DarkPalette.xaml` |

## 2. Quy ước đã chọn (lệch/cụ thể hoá so với thẻ)

1. **Cắt chữ:** `CharacterEllipsis` = MỘT dòng (NoWrap) cắt theo ký tự + "…" (như `TextBlock TextTrimming=CharacterEllipsis`).
   `None` + `maxWidth` hữu hạn = ngắt dòng theo từ; `maxWidth` NaN/vô hạn/>= 1e6 = không giới hạn. Hợp đồng chỉ có hai giá trị
   `TextTrimming`, nên "không cắt, không ngắt" biểu diễn bằng `maxWidth = +vô hạn`.
2. **Mốc t = 0 của tween** là `Timestamp` khung ĐẦU TIÊN sau `FadeTo` (không phải lúc gọi): khớp `BeginAnimation` của WPF (bắt đầu ở
   khung kế) và cho phép test tất định chỉ bằng tick giả. Đồng hồ chỉ được đăng ký khi có tween.
3. **FadeTo cùng đích với tween đang chạy = không làm gì** (như `FadeTargetGate`); khác đích = huỷ tween cũ, chạy tiếp từ opacity hiện tại.
4. **Layout chữ độc lập thiết bị:** DirectWrite không gắn với `ID2D1Device`; mất thiết bị D2D không làm vô hiệu cache chữ (có test).
5. **Bố cục theo DIP, không phụ thuộc DPI** (WPF cũng đo DIP; G-OVL ghi cùng hộp DIP ở 1,0 và 1,5): `OverlayLayoutContext.DpiScale` chỉ
   chuyển tiếp, DPI tới lớp vẽ qua `SetDpi` của D2D.
6. **Phần tử chữ mặc định không nhận chuột** (`IsHitTestVisible=false`); panel chứa nó nhận (như TextBlock trong Border).
7. **Màu chữ + nền:** `PanelElement.Background` mặc định `Dark.Overlay` (alpha thẳng #B0181818), `TextElement.Color` mặc định `Dark.TextBright`.
8. **Chưa có `ToolbarPanel`:** nút WPF có chrome theo `DarkControls.xaml` và chuỗi địa phương hoá - thuộc WP-23. `ButtonElement` có
   `Padding` (8,4) + viền 1 DIP làm điểm khởi đầu; WP-23 chỉnh theo golden Toolbar (210,75 x 40 DIP).

## 3. Kiểm chứng

- `Shell.Tests` (Text + Overlay): chữ tiếng Việt có dấu đo/vẽ trong hộp, cắt "…" <= maxWidth và `IsTrimmed`, ngắt dòng, đậm rộng hơn,
  chữ rỗng, mực co theo DPI, cache hit/miss theo từng thành phần khoá, LRU + lease, Dispose renderer khi còn lease, 400 layout không
  rò, sống qua `RecreateDevice`; animator 0/50/100/200 ms, easing, huỷ/không khởi động lại, ngừng nghe đồng hồ, tick lùi, Stopwatch
  mặc định; layout/hit-test/z-order với bộ đo chữ giả; `DarkPaletteTests` hai chiều với XAML.
- **G-OVL:** `OverlayGoldenTests` dựng đúng cây `MainWindow.xaml` (Margin 8, Padding 6,4, StatusText + ExifInfoText font-1 Margin 0,2,0,0,
  FolderInfo MaxWidth 640, badge Margin 8,44,8,8, ComparePanel) với DirectWrite thật: 60 ca (StatusPanel, FolderInfoPanel,
  ZoomIndicatorPanel, CapturePairBadge, ComparePanel x 3 cửa sổ x 2 DPI x 2 cỡ chữ) lệch tối đa **0,017 DIP** (ngưỡng thẻ 2 DIP).
  Segoe UI + DirectWrite cho cùng độ rộng/cao dòng (1,33 em) như WPF Ideal. Không cần ghi lại golden: WP-10 đã đủ.
- Mutation thủ công: xem mục 4.

## 4. Đột biến

74 đột biến thủ công (sub-agent sonnet, `Shell.Tests` lọc Text|Overlay): animator (easing, cùng đích, huỷ tween, mốc kết thúc, clamp, ngừng nghe đồng hồ, tick lùi, target NaN/ngoài 0..1), bố cục (căn Right/Center/Bottom, Margin, MaxWidth ở Measure/Arrange, Padding, xếp dọc/ngang), hit-test (opacity 0, ẩn, z-order panel/root/con), TextElement (tái dùng layout, rò lease, khoá width/renderer), cache (LRU, loại, đếm ref, `_pending`), DirectWrite (NoWrap, trimming, granularity, bold/size/family trong khoá, NormalizeWidth), DarkPalette (kênh, alpha), ButtonElement.
Lần đầu 10 sống sót không tương đương -> viết 10 test lấp (`FadeEnd_LandsExactlyOnTarget...`, `VerticalCenter_...`, `MaxWidth_CapsTheDesiredWidth...`, `StretchPanel_WithMaxWidth...`, `HorizontalPanel_PlacesChildrenSideBySide`, `FindAt_PrefersTheLaterSibling...`, `TextElement_IsClickThroughByDefault`, `TextElement_RemeasuresWithANewRenderer...`, `HitTest_PrefersTheLaterRoot...`, `RendererDispose_ReleasesTheDirectWriteFactoryImmediately`), chạy lại cả mười đều bị bắt. Kết quả: 68 bị bắt, 6 sống sót ghi nhận:
- Tương đương: `MeasureCore` trừ `Padding.Vertical` khỏi `innerH` (không phần tử nào đọc `available.Height` khi đo); `NormalizeWidth` `<= 0` vs `< 0` và `>=` vs `>` (`(float)0 == 0f`, `(float)1e6 == Unbounded`).
- Không quan sát được qua API: không Release `layout` trong `finally` (đường thành công đã gán null), không Release `format` (wrapper UniqueInstance vẫn được finalizer trả), không `Marshal.Release(sign)` (con trỏ thô nội bộ, không có bộ đếm) - rò COM thật nếu bị xoá nhưng không có điểm quan sát.

## 5. Việc mở / cho gói sau

- WP-23: `ToolbarPanel` (nút/tooltip), nối `Localizer.CurrentChanged` -> `TextElement.Text`, auto-hide dùng `Animator.FadeTo`
  (150 ms vào / 200 ms ra), kiểm hộp Toolbar với golden.
- WP-21: dùng `OverlayHost.Render` sau lớp ảnh trong cùng `BeginDraw/EndDrawAndPresent`; gọi `Animator.Changed` để xin khung.
- Chưa có đường vẽ cho "view list" (nút skipped) - thẻ WP-23 ghi bỏ qua.
