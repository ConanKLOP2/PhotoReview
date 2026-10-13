---
id: NOWPF-FIX-COLOR-EMOJI
order: 1300
summary: |-
  Sửa lỗi L2 của WP-27 (CHƯA XÁC NHẬN): emoji hiện đơn sắc trong shell Win32 vì `D2DDrawContext.DrawText` gọi `DrawTextLayout` với `D2dDrawTextOptions.None`. Đổi sang `EnableColorFont` (D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT, đủ trên ID2D1DeviceContext). Test tự động (WARP, Native): emoji trên nền xám phải có điểm ảnh bão hoà (đỏ trên master: 0/4292), chữ thường vẫn đơn sắc. Hộp bao toolbar không đổi (số đo layout không phụ thuộc cờ vẽ).
---

# NOWPF-FIX-COLOR-EMOJI - emoji màu trong shell Win32 (2026-10-14)

Nguồn lỗi: [NOWPF-WP27-VISUAL-CHECK](NOWPF-WP27-VISUAL-CHECK.md) mục L2. Renderer: [NOWPF-WP15-D2D-RENDERER](NOWPF-WP15-D2D-RENDERER.md).
**CHƯA XÁC NHẬN** bởi người dùng trên desktop thật (lead/người dùng nhìn lại toolbar và dòng thư mục trên màn hình thật).

## Nguyên nhân

`D2DDrawContext.DrawText` (mọi chữ của shell: HUD, status, folder-info, toolbar, menu) truyền `D2dDrawTextOptions.None`. Với
`None`, Direct2D bỏ lớp màu (COLR/CPAL) của Segoe UI Emoji và vẽ glyph bằng màu brush => 0 điểm ảnh bão hoà (`emoji-en.png`).
Bản WPF vẽ glyph màu mặc định.

## Cách sửa

- `src/PhotoReview.Shell.Rendering/D2DDrawContext.cs`: `DrawTextLayout(..., D2dDrawTextOptions.EnableColorFont)`. Context đang dùng là
  `ID2D1DeviceContext` (Win8.1+), nên không cần `IDWriteFactory2/4` hay tự `TranslateColorGlyphRun`. Font fallback emoji do DirectWrite
  hệ thống chọn như trước; cờ chỉ quyết định có vẽ lớp màu hay không.
- Chữ không có lớp màu (Segoe UI thường, tiếng Việt có dấu) ra y hệt trước (test `PlainText_StaysMonochrome_...`).
- Số đo (`ITextLayout.Size`) là của IDWriteTextLayout, độc lập với cờ vẽ: hộp bao toolbar và golden `overlay-boxes.v1.json` không đổi
  (không tái ghi golden, không nới dung sai).

## Kiểm chứng

- Test mới (`tests/PhotoReview.Shell.Tests/Text/DWriteTextRendererTests.cs`, Category=Native, WARP offscreen, đường vẽ sản xuất
  `D2DDrawContext.DrawText`): `ColourEmoji_DrawnWithColour_NotMonochrome` (nền và mực đều xám nên mọi pixel bão hoà đến từ glyph màu;
  đồng thời khẳng định không toàn nền) và `PlainText_StaysMonochrome_WhenColourFontsAreEnabled`.
- Ảnh trước: [`../perf/wp27-visual/emoji-en.png`](../perf/wp27-visual/emoji-en.png) (0 pixel màu). Ảnh sau (`Hud_And_ColourEmoji`, offscreen):
  [`../perf/wp27-visual/emoji-en-after-fix.png`](../perf/wp27-visual/emoji-en-after-fix.png) (chromaPixels = 6513; 📂 📁 ⚙ 🔍 ✔ ❤ có màu, ★ ⋮ vẫn là ký tự văn bản).
- `Shell.Tests` (không Manual/Slow, desktop ẩn): 1789 xanh / 2 đỏ không liên quan (`NativeTaskDialog_ClosedWithWmClose_...` "TaskDialogIndirect is missing"
  và `RepeatedOpenAndClose_OfEveryDialog_LeaksNoGdiOrUserObjects` "USER objects leaked"): chạy riêng lại cả hai + `DWriteTextRendererTests` = 27/27 xanh,
  nên là nhiễu theo thứ tự/máy bận, không phải do cờ vẽ chữ.
- Hiệu năng (`TextOverlayPerfTests`, Manual, máy bận nhiều agent => nhiễu lớn): CPU submit khung overlay WARP P50/P95 0,314/0,570 ms (trước) so với
  0,316/0,596 ms (sau); GPU 0,283/0,489 (trước) so với 0,312/0,485 (sau); `CreateLayout` hit/miss không đổi (không đi qua cờ). Số "done (GPU)" lệch cả
  hai chiều giữa hai lần chạy (nhiễu), không kết luận được chênh lệch thật; đường CPU của chữ không đổi.
- Đỏ trên master trước khi sửa: `emoji drawn monochrome: only 0 saturated pixels of 4292 drawn`. Đột biến (đổi lại thành `None`) -> đỏ.
