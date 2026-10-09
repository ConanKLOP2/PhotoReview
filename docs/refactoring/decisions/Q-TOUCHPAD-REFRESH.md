---
id: Q-TOUCHPAD-REFRESH
order: 151
summary: |-
  Người dùng yêu cầu lại ngày 2026-10-09, ĐẢO quyết định DECLINED Q-R46 (Refresh) và Q-R51 (cử chỉ trackpad): vuốt hai ngón trên touchpad = kéo ảnh khi đang zoom, đổi ảnh theo quãng vuốt khi Vừa khung (chỉ dọc; `TouchpadSwipeEnabled` mặc định bật, `TouchpadSwipeDistancePerImage` mặc định 200 đơn vị con lăn); lệnh Refresh (phím mặc định `R`, không phải F5 vì F5 là phím của hành động Backup; mục menu chuột phải) nạp đủ độ phân giải cho khung đang xem và ép HighQuality, không đổi zoom/cuộn.
---

# Q-TOUCHPAD-REFRESH — vuốt touchpad hai ngón + lệnh Refresh

**Nguồn:** người dùng yêu cầu lại ngày **2026-10-09**. Quyết định này **đảo** hai mục DECLINED trong
[Q-R41..Q-R52](Q-R41-Q-R52-user-feedback.md): **Q-R46** (F5/Refresh) và **Q-R51** (cử chỉ trackpad hai ngón).
Phản hồi: pinch zoom đã chạy, nhưng hai ngón cùng hướng cũng zoom.

## Hiện trạng đã xác minh (trước khi sửa)

- App không có xử lý Manipulation/Touch/Stylus nào. Touchpad đi vào `MouseWheel`:
  `MainWindow.ImageScroll_PreviewMouseWheel` → `PointerInputController.OnWheelAsync` → `WheelGestureInterpreter.Handle`,
  chỉ xét `MouseWheelAction` + Ctrl. Mặc định `MouseWheelAction.Zoom` nên vuốt hai ngón (wheel không Ctrl) = zoom — đúng lỗi người dùng báo.
- WPF (.NET 10, `PresentationCore` 10.0.12) **không có** sự kiện wheel ngang: WM_MOUSEHWHEEL bị bỏ qua hoàn toàn.
- Pinch trên Precision Touchpad đến dưới dạng Ctrl+wheel → zoom (giữ nguyên).
- Refresh chưa có. F5 là phím mặc định của hành động "Backup" (`ReviewAction.Defaults`).

## A. Vuốt hai ngón

- **Nhận diện** (`Input/TouchpadGestures.cs`, hàm thuần, có test): một message wheel là touchpad khi Windows báo
  `GetCurrentInputMessageSource` = `IMDT_TOUCHPAD`, hoặc delta không phải bội của 120, hoặc nó đến trong vòng 250 ms sau một
  message touchpad (một lần vuốt có thể có đúng 120 tình cờ). Còn lại = chuột bánh xe thường → giữ nguyên hành vi `MouseWheelAction`.
- **Định tuyến** (`TouchpadGestureRules.Route`): Ctrl (pinch) → zoom như cũ; touchpad + ảnh đang zoom và lớn hơn khung → **kéo ảnh**
  (cả hai trục, 1 DIP/đơn vị delta, kẹp trong vùng cuộn); touchpad + Vừa khung hoặc nhỏ hơn → **vuốt dọc đổi ảnh**, vuốt ngang bỏ qua.
  Wheel ngang (WM_MOUSEHWHEEL) lấy từ hook `HwndSource` của cửa sổ, chỉ khi con trỏ nằm trong vùng ảnh và không mở Compare.
  Nghiêng bánh xe chuột (bội 120) vẫn bị bỏ qua như trước.
- **Đổi ảnh theo quãng đường** (`TouchpadSwipeNavigator`): ảnh đầu tiên của một lần vuốt đổi sau **nửa** ngưỡng (vuốt nhẹ = đúng 1 ảnh),
  sau đó mỗi **một** ngưỡng thêm 1 ảnh. Mỗi message tối đa 1 ảnh, phần dư lớn hơn một ngưỡng bị bỏ (không dồn ảnh, không "trôi").
  Đổi chiều hoặc dừng > 250 ms = lần vuốt mới. Đổi ảnh đi qua đúng `NextAsync`/`PreviousAsync` như phím mũi tên (preload, token điều hướng).
- **Cài đặt** (Chuột & thu phóng): `TouchpadSwipeEnabled` (mặc định **bật**), `TouchpadSwipeDistancePerImage` (mặc định **200**, khoảng 30..2400,
  `SettingsNormalizer` kẹp giá trị ngoài khoảng). Config cũ không có hai khóa này → mặc định, không báo sửa.
  Tắt = hành vi y như trước (wheel dọc theo `MouseWheelAction`, wheel ngang bỏ qua).

### Giới hạn (ghi rõ)

- **Không đo được "cm" thật.** Message wheel chỉ có delta (120 = một nấc), không có khoảng cách ngón tay; tỉ lệ delta/mm do driver
  touchpad quyết định. DPI màn hình không liên quan tới delta nên không dùng để quy đổi. Mặc định 200 là **ước lượng** ~1,5–2 cm trên
  Precision Touchpad thông thường (≈ 1 nấc/cm); vuốt ~6 cm ≈ 720 đơn vị → 4 ảnh (100, 300, 500, 700). Người dùng chỉnh trong Cài đặt.
  Khi bật log, mỗi lần đổi ảnh bằng vuốt ghi `Touchpad swipe: ... delta=... images=... hint=...` để hiệu chỉnh từ máy thật.
- Quán tính cuộn của Windows (sau khi nhấc ngón) cũng gửi wheel delta: được tính vào quãng vuốt (không phân biệt được).
- Chuột "cuộn mượt" độ phân giải cao cũng gửi delta lẻ → bị coi là touchpad. Cách tránh: tắt `TouchpadSwipeEnabled`.
- `IMDT_TOUCHPAD` chỉ là gợi ý bổ sung; nhiều driver không báo nó cho message wheel, nên heuristic delta là đường chính.
- **Cần người dùng thử bằng touchpad thật**: cảm giác ngưỡng mặc định, tốc độ kéo, quán tính, vuốt ngang.

## B. Refresh

- **Lệnh:** `ReviewCommandType.Refresh` (thêm cuối enum), phím tắt tùy chọn `ShortcutMappings.Refresh` mặc định **`R`** (rảnh trong toàn bộ
  phím mặc định). Không chiếm F5 (phím của hành động Backup). Config cũ đã có hành động/phím dùng `R` → `DisableConflictingOptionalShortcuts`
  tắt Refresh, binding của người dùng thắng. Gán lại được trong Cài đặt → Phím tắt (nhóm Xem). Không lặp khi giữ phím.
- **Menu chuột phải:** mục `RefreshMenuItem` ("Làm mới") ngay sau submenu Zoom, luôn hiện (không thuộc cụm zoom ẩn được). Tên định danh riêng,
  thay đổi `MainWindow.xaml` tối thiểu (một dòng menu + binding `EffectiveScalingQuality`) để dễ merge với nhánh `feat/context-menu-customization`.
- **Làm gì** (`MainViewModel.RefreshView` → `ZoomDetailLoader.Refresh`): (1) ép `BitmapScalingMode.HighQuality` cho ảnh đang xem
  (`ViewerState.ForceHighQualityScaling`, hết khi sang ảnh khác; cài đặt `ScalingQuality` không đổi); (2) nếu bitmap đang hiện có ít pixel hơn
  số pixel thiết bị mà khung đang hiện cần (zoom × chiều rộng gốc; ở Fit dùng Fit zoom) thì giải mã/hiện ảnh gốc — **kể cả ở Fit** (giữ đến khi
  zoom thật sự đổi hoặc sang ảnh khác); (3) thử lại một lần giải mã gốc đã lỗi. Không đổi zoom, không cuộn; ảnh gốc thay bản xem trước ở cùng
  kích thước gốc (ADR 0008), RAW lệch vài pixel được neo lại như khi zoom. `ApplyFitViewAsync`/T89 không bị đụng tới.

### Có thực sự cần? (bằng chứng đo)

Pipeline hiện tại **đã** render chất lượng cao trong phần lớn trường hợp: mặc định `ScalingQuality = HighQuality` (Fant), bản xem trước được
giải mã theo khung × DPI × 1,15, và khi zoom vượt quá số pixel của bản xem trước thì `ZoomDetailLoader` tự giải mã ảnh gốc. Refresh chỉ thay đổi:

| Trường hợp | Trước | Sau Refresh |
|---|---|---|
| Mặc định, không đổi kích thước cửa sổ | HighQuality, đủ pixel | **Không đổi gì** (`PreviewSufficient`, `ScalingChanged = false`) |
| Cửa sổ to lên sau khi ảnh đã hiện (phóng to, F11) ở Fit | Bản xem trước bị phóng to | Ảnh gốc, vẫn Fit |
| `ScalingQuality = Linear` | Linear | HighQuality cho ảnh này |
| Giải mã gốc đã lỗi trên ảnh này | Chặn thử lại đến khi đổi ảnh | Thử lại một lần |

Đo trên máy này (`MainWindowZoomDetailTests.Measure_RefreshAtFit_AfterTheViewportGrew`, Manual, ảnh 6000×4000, DPI 1):
khung 900×600 → bản xem trước **1280 px**, Fit cần 900 px (đủ). Khung tăng lên 2400×1600 → vẫn bản xem trước 1280 px nhưng Fit cần
**2400 px (phóng ×1,88)**. Refresh → `LoadingOriginal`, sau đó hiện **6000 px**, vẫn Fit.

## Test

`TouchpadGesturesTests` (chuỗi delta → hành động), `PointerInputControllerTests.Touchpad` (qua controller thật + surface giả),
`ZoomDetailLoaderGapTests.Refresh`, `MainViewModelZoomSwapTests.Refresh`, `RefreshShortcutRouterTests`, `ViewerStateRefreshScalingTests`,
`TouchpadRefreshSettingsTests`, `MainWindowZoomDetailTests.Refresh_WhileZoomed_KeepsLayoutAndScroll_AndForcesHighQualityScaling`
(cửa sổ thật: layout + cuộn giữ nguyên, nguồn ảnh giữ nguyên, scaling đổi sang HighQuality). Mutation check ghi trong PR.
