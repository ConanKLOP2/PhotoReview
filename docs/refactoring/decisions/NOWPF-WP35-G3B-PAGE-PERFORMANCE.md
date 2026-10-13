---
id: NOWPF-WP35-G3B-PERFORMANCE
order: 1150
summary: |-
  WP-35 GĐ3b (CHƯA XÁC NHẬN): trang Performance của Settings Win32 (4 thuộc tính: DecoderBackend, ImageCacheRamPercent, PreloadForwardCount, PreloadBackwardCount) trên khung GĐ3a. Thanh trượt cache RAM đổi khoảng theo cửa sổ preload và RAM máy; mục TurboJpeg thiếu = ghi chú + từ chối chọn. 40 ca test mới, 24 đột biến (0 sống), 0 cảnh báo ILC, không nạp WPF.
---

# NOWPF-WP35-G3B - trang Performance (2026-10-13)

Thẻ: WP-35 GĐ3b, theo [NOWPF-WP35-G3A-SETTINGS-SHELL](NOWPF-WP35-G3A-SETTINGS-SHELL.md) (mục 3 "cách thêm một trang"). Presenter/FieldMap không đổi.

## 1. Đã làm

| Phần | File (`src/`) | Nội dung |
|---|---|---|
| Trang | `Shell.Win32/Dialogs/SettingsUi/Pages/PerformanceSettingsPage.cs` | combobox DecoderBackend (thứ tự Wpf, WicDirect, TurboJpeg = chỉ số `DecoderBackendIndex`); thanh trượt cache RAM + chữ giá trị `RamCacheValueText` + ghi chú `RamCacheHintText`; hai ô preload trước/sau + ghi chú khoảng |
| Danh mục | `.../SettingsPageCatalog.cs` | MỘT dòng `["Performance"]` |
| Khung (CỘNG THÊM, nhỏ) | `.../SettingsPageContext.cs` | `DecoderFactory` (đọc `IsTurboJpegAvailable`) |
| Khung (CỘNG THÊM, nhỏ) | `.../SettingsDialog.cs` | `SettingsDialogOptions.PhysicalMemoryBytes` (mặc định null = đo máy thật) chuyển vào `SettingsPresenter`; chỉ để test có RAM tất định |

Không thêm chuỗi Localizer: mọi khoá đã có (`settings.decoderBackend.*`, `settings.ramCache.*`, `settings.preloadForward/Backward.*`, `settings.preloadWindow.hint`).

## 2. Khác biệt so với phần mô tả ban đầu của nhiệm vụ

WPF `PerformancePage` CHỈ có 4 điều khiển ở trên. Các mục RAW/WebP-HEIC/CompareHash/RawFullDecode/RawPairMode nằm ở trang **Files** (agent Files làm); dung lượng cache đĩa, số worker, giới hạn bộ nhớ, RAM reserve, source-bytes cache là `SettingsFieldMap.NotEditedInDialog` ("chỉ sửa tay config.json", Defaults đặt lại): trang Win32 không tạo điều khiển cho chúng (đúng WPF) và test ghim rằng Save giữ nguyên các giá trị đó.

## 3. Thiết kế

1. **Khoảng thanh trượt**: `presenter.RamCacheRange(textTrước, textSau)` mỗi lần đổi một ô preload (EN_CHANGE) -> `Context.SetSliderRange` (giữ/kẹp vị trí như WPF); ô không đọc được dùng giá trị đã lưu (presenter làm). Min = `RamBudgetPolicy.MinimumCachePercent`, max = 90.
2. **Mục TurboJpeg thiếu**: Win32 combobox không tắt từng mục. Trang hiện ghi chú `settings.decoderBackend.turboJpegUnavailable` và hoàn lại lựa chọn trước khi người dùng chọn mục này (CBN_SELCHANGE). Cấu hình đang có TurboJpeg vẫn hiện mục 2 (như WPF) để Save không âm thầm đổi.
3. **Không bắn sự kiện khi nạp**: `Load` đi qua cờ `_loading` của khung nên đặt chữ/vị trí không tự kích hoạt cập nhật khoảng.
4. `DecoderBackend.LibRaw`/ngoài khoảng đã được `SettingsNormalizer` sửa trước khi mở (test ghim).

## 4. Kiểm chứng

- `SettingsPerformancePageTests` 40 ca (kể cả `[Theory]`): thuộc tính thuộc FieldMap + nội bộ không có điều khiển, mỗi giá trị DecoderBackend (Load/Collect/Save, chọn mục -> backend), LibRaw, TurboJpeg thiếu/có/ngôn ngữ, preload biên (1/0, 500/500, 1/500, 500/0), ngoài khoảng bị từ chối với thông báo + focus đúng ô, tệp ngoài khoảng bị SettingsNormalizer chuẩn hóa, thanh trượt (nạp, kéo, biên 1/90, cửa sổ lớn nâng min và đẩy giá trị lên, ô hỏng lùi về giá trị đã lưu, giá trị đã lưu dưới min), chữ giá trị/hint theo ngôn ngữ, Save giữ cờ nội bộ + trang khác, Reset trang + Defaults, Tab, bố cục en/vi x 100 %/150 % x có/không ghi chú TurboJpeg, rò GDI/USER.
- Ảnh `PrintWindow` en/vi 100 %/150 % (`SettingsPerformanceSnapshotTests`, thủ công): đã xem, không cắt, không chồng; ghi chú 5 dòng tiếng Việt vừa khung.
- Đột biến 24 (script thủ công; bước 1 chỉ chạy lớp trang mới): chỉ số combobox cắt (Load/Collect), lệch 1 của cache RAM, ô forward/backward đảo (Load/Collect), khoảng bỏ qua ô/min cố định/không nạp khoảng/không nạp giá trị, EN_CHANGE bỏ ô sau, chấp nhận TurboJpeg khi thiếu, không nhớ lựa chọn hợp lệ, ghi chú hiện ngược/luôn khả dụng/không đổi ngôn ngữ, chữ giá trị không theo thanh trượt, FocusTarget lệch, hint đảo min/max, thứ tự mục đổi, bỏ dòng catalog, lựa chọn trước không theo giá trị nạp, bỏ RAM giả. Kết quả: 24 chết, 0 sống (hai lần chạy lại vì đột biến lỗi biên dịch/ghép chuỗi trong script, không phải kẻ sống).
- Cổng: `tools/check-aot-warnings.ps1 -RequireToolchain` = 0 cảnh báo ILC (15,1 MB); trang dựng lười (chỉ khi mở), không nạp WPF (`LazyLoadTests` giữ nguyên).

## 5. Chưa kiểm / quyết định

- CHƯA XÁC NHẬN: giữ cách "ghi chú + hoàn lại" thay vì mục bị tắt cho TurboJpeg (không có tooltip theo mục trên combobox Win32); nếu cần y hệt WPF phải owner-draw mục tắt (đắt, ích lợi nhỏ).
- Chưa kiểm bằng tay: RAM máy thật khi mở dialog ở cổng sản phẩm (test dùng RAM giả), Narrator, DPI hai màn hình.
- Hoàn tác: bỏ dòng `["Performance"]` trong `SettingsPageCatalog` (trang quay lại ghi chú chờ, Save vẫn giữ giá trị).
