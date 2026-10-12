---
id: NOWPF-WP26
order: 880
summary: |-
  WP-26 (đợt 3 NO-WPF, 2026-10-12): thanh cuộn tự vẽ của cửa sổ chính shell Win32 theo NE-3 (a) (CHƯA XÁC NHẬN, mặc định theo khuyến nghị): chiếm chỗ như WPF, nền `Dark.ScrollTrack`, con trượt bo 4 DIP (#4A4A4A / hover #6A6A6A / kéo #7A7A7A), ô góc, kéo con trượt (offset tỉ lệ, con trượt bám con trỏ, capture), nhấn rãnh = một trang rồi lặp theo nhịp bàn phím của hệ thống tới khi con trượt chạm con trỏ, nuốt nhấn trước ảnh (hộp ảnh đang zoom phủ cả thanh), ComparePanel che như WPF, wheel/touchpad vẫn qua WP-22. Hộp thanh/rãnh/con trượt/ô góc khớp ScrollViewer + DarkScrollBars.xaml THẬT: 2.400 ca, lệch lớn nhất 1e-13 DIP (thẻ cho phép 1 px). Sàn con trượt đo được là 8 DIP (không phải 17). App WPF không đổi.
---

# NOWPF-WP26 - thanh cuộn tự vẽ (2026-10-12)

Thẻ: WP-26 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); quyết định NE-3 trong [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md)
(phương án (a), xem [NOWPF-UNCONFIRMED-DECISIONS-20261010](NOWPF-UNCONFIRMED-DECISIONS-20261010.md)). Phụ thuộc: WP-16 (engine
viewport: độ dày thanh đã trừ vào vùng nhìn), WP-17 (`DarkPalette`), WP-21 (thứ tự vẽ), WP-22 (nhập liệu). App WPF
(`PhotoReview.App`) **không đổi**; liên kết `.jpg` không đổi.

## 1. Đã làm

| File | Nội dung |
|---|---|
| `Shell.Win32/Viewing/ScrollBarGeometry.cs` | Hình học thuần: thanh 10 DIP ở cạnh phải/dưới (chiếm chỗ), `Track` lề 1/2 DIP, con trượt dài `rãnh x viewport / (max + viewport)` (sàn 8 DIP), vị trí tỉ lệ offset (dọc: 0 ở trên), ô góc khi cả hai thanh hiện, hit-test (Thumb/TrackBefore/TrackAfter/Background/Corner, biên nửa mở), ánh xạ vị trí con trượt -> offset (kẹp [0, max]). |
| `Shell.Win32/Viewing/ScrollBars.cs` | `ShellScrollBars` (handler thông điệp + vẽ), `ScrollBarColors`, `ScrollBarOptions`. Kéo con trượt, nhấn rãnh + lặp, hover/pressed, mất capture/WM_CANCELMODE, WM_MOUSELEAVE; vẽ nền + con trượt + góc, ComparePanel che. |
| `Shell.Win32/Viewing/ScrollBarNative.cs` | `TrackMouseEvent(TME_LEAVE)` và nhịp lặp của hệ thống (`SPI_GETKEYBOARDDELAY/SPEED`, cùng công thức `RepeatButton` của WPF). |
| `Shell.Win32/ShellMainWindow.ScrollBars.cs` | Nối vào cửa sổ chính: `RegisterScrollBars` (đứng TRƯỚC `RegisterInput` trong `RegisterFeatures`), `DrawScrollBars`, đóng khi cửa sổ đóng. |
| sửa nhỏ | `ShellMainWindow.cs` (một dòng `RegisterScrollBars()`), `ShellMainWindow.Frame.cs` (`DrawContent` gọi thêm `DrawScrollBars` sau `DrawPresentation`, trước `DrawOverlay` của WP-23). |

## 2. Quyết định / khác bản WPF

1. **Thứ tự handler.** Hộp của ảnh đang zoom (`ImageOrigin`, kích thước thật) phủ cả vùng thanh cuộn, nên `ShellInput.IsOverImage` của
   WP-22 coi nhấn trên thanh là nhấn lên ảnh (pan/click-zoom). Thanh cuộn phải thấy chuột TRƯỚC: `RegisterScrollBars()` đứng trước
   `RegisterInput()`. Nếu WP-23 chèn handler overlay lên đầu chuỗi (`InsertMessageHandlerFirst`) thì thứ tự là overlay -> thanh cuộn ->
   ảnh, đúng với z-order của XAML. Test e2e đảo thứ tự sẽ đỏ (mục 4, E01).
2. **Nuốt mọi nhấn trái trên thanh/ô góc** (kể cả lề của rãnh và ô góc), như `ScrollBar`/`Rectangle` của WPF nằm trên `MainImage`.
   Nút phải không bị nuốt (menu ngữ cảnh của cả lưới là việc của WP-24); nút giữa trên thanh vốn không chạy `MiddleClickAction`
   (`IsInsideScrollBar` của WPF) và `ShellInput.IsInsideViewport` đã loại vùng thanh.
3. **Kéo con trượt.** `Thumb` của WPF phát `DragDelta` theo toạ độ con trỏ so với con trượt đang di chuyển, nên về mặt kết quả con
   trượt luôn bám con trỏ (điểm nắm giữ nguyên) và offset = vị trí con trượt trong rãnh x max / quãng; kẹp [0, max]. Cài đặt: ánh xạ
   tuyến tính không giữ trạng thái; test WPF thật (`Track.ValueFromDistance`) xác nhận hệ số `max / quãng`.
4. **Nhấn rãnh** = `PageUp/PageDown/PageLeft/PageRight` của `ScrollViewer` (một vùng nhìn, kẹp). Giữ chuột: lặp sau
   `(KeyboardDelay + 1) * 250` ms rồi mỗi `(31 - KeyboardSpeed) * 367 / 31 + 33` ms (test so với `new RepeatButton().Delay/Interval`);
   dừng khi con trượt chạm con trỏ (nút rãnh biến mất dưới con trỏ); ra rồi vào lại vùng rãnh khi còn giữ thì bộ đếm trễ tính lại.
   Dùng đồng hồ khung của viewport (`HookRenderFrame`), không có luồng/timer riêng.
5. **Hover/pressed.** Con trượt `#4A4A4A`; `IsMouseOver` -> `#6A6A6A`; `IsDragging` -> `#7A7A7A` (pressed thắng hover, như thứ tự trigger
   của template). Khi đang giữ rãnh (RepeatButton giữ capture) con trượt không hover. Hover tính theo vị trí con trỏ lúc vẽ nên
   theo kịp khi layout dời con trượt dưới con trỏ đứng yên; `WM_MOUSELEAVE` xoá hover.
6. **ComparePanel** (`Margin=8`, nền canvas) phủ lên `ImageScroll`: thanh cuộn chỉ còn thấy ở dải 8 DIP bên phải/dưới panel (và chỉ
   nhận chuột ở đó) - đúng như bản WPF, kể cả "lòi" ra cạnh panel. CHƯA XÁC NHẬN: bản đẹp hơn là ẩn thanh khi so sánh (xem
   [NOWPF-UNCONFIRMED-DECISIONS-20261010](NOWPF-UNCONFIRMED-DECISIONS-20261010.md), WP26-COMPARE).
7. **Crossfade**: thanh cuộn vẽ trên cả lớp ảnh cũ đang mờ (WPF: `OutgoingImage` nằm trên `ImageScroll`, chỉ che thanh khi ảnh cũ
   rộng hơn vùng nhìn, trong ~150 ms). Chấp nhận để không sửa `PresentationComposer` (vùng của WP-21).
8. **Sàn con trượt = 8 DIP.** `Track` của WPF dùng `Math.Floor(nút cuộn / 2)`; đo bằng ScrollViewer thật: 8 (nút 17 DIP ở 96 DPI,
   16,5-17 DIP ở mọi DPI hệ thống thường gặp nên vẫn 8), KHÔNG phải 17 như `SM_CYVTHUMB` (thẻ không nêu số này). Rãnh <= 8 DIP: con trượt ẩn,
   thanh vẫn hiện (khớp WPF ở cả ca client 14x14, 40x10).
9. **Fit width/height lệch 5 DIP** (nửa độ dày thanh, ghi ở handoff/WP-16): kiểm tra, KHÔNG sửa. Nguyên nhân nằm trong
   `PointerInputController` dùng chung (pass hiệu chỉnh đọc `ViewportCentre` khi cả hai thanh đang hiện); chiều rộng thanh cuộn tác động
   qua engine WP-16 (`ViewportWidth = client - 10` khi thanh dọc hiện, đã khớp WPF 0 DIP) và thanh cuộn vẽ không đổi gì ở đây.
   G-INPUT (91 kịch bản, WP-22) và G-VIEW giữ nguyên xanh; sửa chung ở một PR riêng như handoff đã ghi.
10. **Chính sách khác** (`Overlay`/`Hidden` của engine, NE-3 (b)/(c)): hình học đã hỗ trợ (test), chưa nối cấu hình vì NE-3 chọn (a).

## 3. Test

| Test | Chạy ở | Nội dung |
|---|---|---|
| `ScrollBarWpfParityTests` (`Category=UI`, STA, không cửa sổ) | Shell.WpfBridge.Tests | ScrollViewer Auto/Auto + `Themes/DarkScrollBars.xaml` THẬT: 18 client x 8 extent x 5x5 offset = 2.400 ca; hộp thanh, `Track`, con trượt và ô góc lệch lớn nhất **1e-13 DIP**; ca rãnh tí hon (24x24, 18x40, 14x14, 40x10, 27x31, 200x17); sàn con trượt; `Track.ValueFromDistance` và `PageDown/PageRight/PageUp`; màu/bán kính của `Dark.ScrollThumb` và trigger hover/dragging; `RepeatButton.Delay/Interval`. Đây là phần "pixel box so WPF" của G-VIEW cho thanh cuộn. |
| `ScrollBarGeometryTests` | Shell.Tests | Số đo, kích thước/vị trí con trượt, sàn, ẩn khi vừa khung, một thanh/hai thanh/thanh thứ hai do thanh đầu chiếm chỗ, ô góc, ánh xạ ngược, hit-test (biên nửa mở), Overlay/Hidden, đầu vào NaN. |
| `ShellScrollBarsTests` | Shell.Tests | Cửa sổ giả + viewport + `ShellInput` thật (đăng ký sau thanh cuộn): kéo dọc/ngang, kẹp, ra ngoài cửa sổ, nhả, nhấn rãnh, lặp theo đồng hồ giả (trễ, khoảng lặp, dừng khi tới con trỏ, ra/vào lại), nuốt nhấn trước ảnh (có bản đối chứng không có thanh cuộn), ẩn khi Fit, đổi kích thước/zoom/DPI 150 %, ComparePanel, hover/pressed, mất capture, WM_CANCELMODE, nhịp lặp. |
| `ScrollBarRenderingTests` (`Category=Native`) | Shell.Tests | D2D WARP thật, đọc lại pixel: nền #161616, con trượt #4A4A4A/#6A6A6A/#7A7A7A, ô góc, đầu bo tròn, ComparePanel che. |
| `ShellScrollBarEndToEndTests` (`Category=UI`) | Shell.Integration.Tests | `ShellStartup` thật + HWND thật: kéo con trượt dọc/ngang bằng WM_LBUTTONDOWN/WM_MOUSEMOVE/WM_LBUTTONUP, nhấn rãnh, nhấn lề thanh không kéo ảnh, thanh vắng ở Fit rồi xuất hiện sau zoom (vùng nhìn trừ 10 DIP), đổi kích thước cửa sổ, Ctrl+wheel đổi độ dài con trượt, khung thật vẽ thanh cuộn. Kỳ vọng tính từ hình học thật nên không phụ thuộc màn hình runner (1024x768, vùng làm việc nhỏ, viền vô hình). |
| `ScrollBarPerfReportTests` (`Category=Manual`) | Shell.Tests | Chỉ in số đo (mục 5). |

Không test nào dựa vào thời gian thật: nhịp lặp/độ trễ do đồng hồ khung giả và `Timestamp` tiêm; e2e chỉ chờ điều kiện (có giới hạn 30 s).

## 4. Đột biến

35 đột biến thủ công (mỗi cái: sửa một chỗ trong `Shell.Win32` -> build -> chạy `ScrollBar|Viewport|Input` của Shell.Tests, riêng E01/E02 chạy
`ShellScrollBarEndToEnd` trên desktop ẩn), **35/35 bị diệt**:

- Hình học (13): mẫu số kích thước con trượt, vị trí theo trọn rãnh thay vì quãng đi được, sàn `Max` -> `Min`, bỏ kẹp trên, bỏ kẹp dưới,
  thanh hiện khi ảnh vừa khung, rãnh trước/sau đảo nhau, lề `Track` lẫn trục, ô góc khi chỉ một thanh, hit-test con trượt bằng cả thanh,
  biên phải đóng, con trượt luôn hiện, chiều thanh dọc đảo.
- Điều khiển/vẽ (20): không capture khi nhấn con trượt, không nhả capture, DPI nhân thay vì chia, màu pressed = hover, hover khi đang giữ rãnh,
  ComparePanel không che, trang nửa vùng nhìn, lần lặp đầu chờ khoảng lặp thay vì độ trễ, nhấn lề thanh lọt xuống ảnh, nhấn thanh không
  dừng đà kinetic, bỏ điểm nắm (con trượt nhảy tới con trỏ), kéo đọc sai trục, hover không xoá khi rời cửa sổ, nhả nút không bị nuốt,
  lặp không dừng khi con trượt tới con trỏ, xin WM_MOUSELEAVE mỗi lần di chuyển, dải compare không cắt clip, trang đi sai hướng, màu hover
  lệch #6A6A6A, không gỡ đồng hồ khung sau khi nhả.
- Tích hợp (2): đăng ký thanh cuộn SAU `RegisterInput` (ảnh nuốt nhấn trên thanh; e2e đỏ ở "AndAPressOnTheBarNeverPansTheImage"), bỏ
  `DrawScrollBars` khỏi khung (e2e đỏ nhờ `DrawnFrames`).
- Lần chạy đầu của script đột biến cho ba cái SỐNG SÓT giả (C13/C14/C20) và hai cái không build (C15/C16): do lỗi của chính script
  (toán tử `+` đứng sau dấu phẩy làm hỏng mảng đột biến) và vì đột biến làm trường không còn được đọc (CS0414)/mã không tới được (CS0162).
  Sửa script/đột biến rồi chạy lại toàn bộ: 35/35 đỏ. Không có đột biến tương đương.
- Không phát hiện lỗi thật trong mã nguồn bằng đột biến; lỗi thật duy nhất trong gói là kiểm thử: `ScrollBarWpfParityTests` ban đầu tạo
  `System.Windows.Application` ở một luồng STA riêng khiến 4 test cầu cửa sổ phụ (WP-19b) trong cùng process đỏ ("different thread owns it");
  nay nạp XAML thật bằng `XamlReader` từ bản sao file nguồn, không tạo Application.

## 5. Số đo

Chi tiết: [perf/2026-10-12-wp26-scrollbars.md](../perf/2026-10-12-wp26-scrollbars.md). Máy dev, Release:

| Việc | Chi phí |
|---|---|
| Hit-test một điểm (tính hình học + hit) | ~2,2 us |
| Ghi lệnh vẽ thanh cuộn (không D2D) | ~2,7 us/khung |
| `WM_MOUSEMOVE` khi kéo (hình học + `ScrollTo` + layout, đồng bộ) | ~3,6 us (cửa sổ giả); trên HWND thật p50 0,044 / p95 0,063 ms |
| Vẽ thật WARP 1200x800 (gồm đọc lại pixel) | ~6,5 ms/khung (chủ yếu là đọc lại; 3 `Fill*` không đáng kể) |

Thanh cuộn không thêm gì vào đường nóng của khung ngoài 3-4 lệnh tô khi có thanh; khi ảnh vừa khung (Fit) hàm vẽ thoát ngay.

## 6. Việc còn lại / bàn giao

- NE-3 vẫn CHƯA XÁC NHẬN: đổi sang (b)/(c) là đổi `ScrollBarPolicy` ở `CreateViewportOptions` + `ShellMainWindow.ScrollBars.cs`; hình học Overlay/Hidden đã có test.
- WP-23: `OverlayHitTest` của `ShellInput` cần phủ cả vùng thanh cuộn nếu overlay nằm trên nó (toolbar/status không chạm cạnh phải).
- Trang bằng phím (PageUp/PageDown của bàn phím) không thuộc thanh cuộn: bản WPF không focus được thanh nên không có.
- Chạm/bút (WM_POINTER/WM_TOUCH) chưa có ở shell nói chung; thanh cuộn theo chuột.
