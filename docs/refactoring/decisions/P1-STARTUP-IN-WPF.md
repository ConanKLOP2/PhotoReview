---
id: P1-STARTUP-IN-WPF
order: 191
summary: |-
  P-1 "WPF nhanh" (nhánh perf/p1-startup-in-wpf, base 2.0.383): decode ảnh khởi chạy ngay sau khi đọc config.json, prefetch placement, làm nóng parser settings, bỏ JIT index so sánh và hoãn preload kick đầu; đo xen kẽ 8 lần, ảnh đầu (RenderedFrame) R2R 1127 -> 782 ms (-31 %), build 1321 -> 915 ms (-31 %); mục tiêu NW-5 (<= 600 ms, publish R2R) CHƯA đạt; CÒN MỞ: chọn tiếp - self-contained + composite R2R (-140..-160 ms đo được, 174 MB), hoãn XAML chrome tới sau khung đầu (~-100 ms ước, đổi hành vi nhìn thấy), hay dừng và làm spike S1 (Win32/Native AOT).
---

# P1-STARTUP-IN-WPF - giai đoạn P-1 "WPF nhanh": kết quả và bước tiếp theo

Số liệu đầy đủ: [`../perf/2026-10-10-p1-startup-in-wpf.md`](../perf/2026-10-10-p1-startup-in-wpf.md).
Kế hoạch gốc: `decisions/NO-WPF-MIGRATION-PLAN.md` (PR #395 trên `master`; P-1 và cổng NW-5).

## Đã làm (nhánh `perf/p1-startup-in-wpf`, base `f3f62728` = 2.0.383)

1. **Decode ảnh khởi chạy sớm:** ngay sau khi đọc `config.json` (thread pool), dùng hộp decode đoán từ placement đã lưu (`InitialViewportPredictor`); chỉ bắt đầu sau khi
   instance này giữ khoá; presenter nhập vào decode đang chạy. Hộp đoán khớp hộp thật 100 % lần chạy; sai thì chỉ thêm một decode nền, không ảnh sai; RAW không decode sớm. **-162 ms.**
2. **Prefetch placement** (`WindowPlacementService.Prefetch`) trên pool; `Show()` dùng lại kết quả.
3. **Làm nóng parser settings** (`StartupWarmup.WarmSettingsParser`, từ static ctor của `App`): UI không còn chờ ~75-95 ms. **-96 ms.**
4. **Index so sánh** (`ComparePairService`): `GeneratedRegex` + không LINQ. **-20 ms.**
5. **Hoãn preload kick đầu** tới sau khung ảnh đầu (`ImagePresenter.DeferNextPreloadKick`, chỉ ảnh khởi chạy). **-60 ms.**
6. **Kết quả:** R2R 1127 -> **782** ms, build 1321 -> **915** ms (lô yên tĩnh); lô xác nhận mã cuối (máy bận): R2R 849, build 973. Không đổi hành vi nhìn thấy; `Window.Icon` đã thử và hoàn lại.
7. **Thí nghiệm triển khai (chưa áp dụng):** self-contained + `PublishReadyToRunComposite` cho 707 / 704 ms so với 867 / 841 ms của publish hôm nay cùng lô (-160 / -137 ms), 174 MB thay vì 15 MB.

## Còn mở: làm gì tiếp để tới (hoặc bỏ) mục tiêu 600 ms

**Hiện trạng và vì sao quan trọng:** NW-5 đặt mục tiêu ảnh đầu <= 600 ms với publish R2R; nhánh này đạt 782 ms (lô yên tĩnh) / 849 ms (lô bận). Đường găng còn lại: runtime + WPF
~170, render thread ~90, XAML MainWindow ~205, `Show()` ~165 (ms, chi tiết ở tài liệu perf). Phần đo được rẻ nhất là cách triển khai (composite); phần còn lại là XAML chrome
hoặc bỏ WPF. Hôm nay Explorer chạy bản **build** (915 / 973 ms), kể cả quyết định `P-STARTUP-FIRST-IMAGE` (B) chưa được người dùng chốt.

| | Cách | Ưu | Nhược |
|---|---|---|---|
| A | **Dừng ở mã đã làm.** Explorer chạy bản build như hôm nay (915 ms), hoặc publish R2R phụ thuộc framework theo `P-STARTUP-FIRST-IMAGE` B (782 ms) | Không rủi ro mới, không việc gì thêm; không động tới dữ liệu người dùng; 15 MB | Không đạt 600 ms; nếu giữ build thì mất thêm ~130 ms so với R2R |
| B | **Bản Explorer chạy là self-contained + composite R2R** (P-STARTUP B nâng lên: publish `--self-contained true -p:PublishReadyToRunComposite=true` vào một thư mục riêng, đổi liên kết `.jpg` sang đó; `AGENTS.local.md` chạy publish sau mỗi build) | Đo được -140..-160 ms (R2R ~620-650 ms ước); không đổi mã app, không đổi hành vi, không chạm dữ liệu | 174 MB thay vì 15 MB; runtime đi kèm nên cập nhật .NET = publish lại; publish ~47 s mỗi lần; bẫy obj (phải dọn `obj\Release\...\win-x64` giữa publish composite và thường, nếu không exe crash 0xc0000602); vẫn chưa chắc <= 600 ms. Cần test: mở ảnh từ Explorer, F11, settings/log, cập nhật qua build mới |
| C | **B + hoãn XAML chrome nhìn thấy** (toolbar, overlay, context menu, compare panel) tới sau khung ảnh đầu | Ước thêm ~-100 ms (R2R còn ~520-550 ms ước, vượt 600 ms) | **Đổi hành vi nhìn thấy:** toolbar/overlay xuất hiện ~100 ms sau ảnh; chạm `MainWindow.xaml` và binding (rủi ro hồi quy test UI, phím tắt/menu chưa nạp khi bấm quá sớm); số ước chưa đo; cần người dùng đồng ý nhìn thấy khác đi. Kèm (c) catalog ngôn ngữ song song (~-15 ms) và (d) bỏ `DUCECompatiblePtr` (~-20 ms, chạm decoder/cache) nếu muốn thêm |
| D | **Dừng P-1, chuyển sang spike S1** (Win32 + Native AOT của `NO-WPF-MIGRATION-PLAN`) | Mục tiêu dài hạn: sàn ~180-400 ms (ước của kế hoạch); không phụ thuộc tối ưu WPF nữa | Chưa đo Native AOT (thiếu MSVC + Windows SDK; cần người dùng cho cài toolchain, NW-2); 12-33 người-tuần (ước của kế hoạch); mọi gain WPF ở P-1 vẫn giữ nhưng không thêm |

**Đề xuất: B** (rồi quyết C sau khi đo lại). Lý do: lợi ích đo được lớn nhất còn lại (-140..-160 ms) mà không đổi mã app hay hành vi và không có rủi ro dữ liệu; chi phí chỉ
là dung lượng/cách publish trên máy này. C đổi cái người dùng nhìn thấy nên chỉ làm khi người dùng chấp nhận; D là khoản đầu tư lớn, nên quyết sau khi biết B đưa ta tới đâu.

**Sau khi người dùng chọn:** (A) ghi nhận, không làm gì thêm; (B) viết câu lệnh publish composite + cách đổi liên kết `.jpg` (người dùng tự sửa `AGENTS.local.md`/registry), chạy lại lô
xen kẽ trên bản publish đó, và cập nhật quyết định P-STARTUP B; (C) làm B rồi một PR riêng hoãn XAML chrome (test UI + đo lại, mutation check nhánh mới); (D) mở spike S1 theo NW-2
(xin phép cài toolchain) và đóng P-1.

## Cần người dùng xem bằng mắt

Danh sách đầy đủ ở tài liệu perf; tối thiểu: icon cửa sổ không đổi; không khung trắng; F11; placement trên màn hình thứ hai/đã rút; mở thư mục lớn; mở file RAW máy ảnh;
mở từ Explorer khi đã có một instance chạy (không rò việc decode sớm).
