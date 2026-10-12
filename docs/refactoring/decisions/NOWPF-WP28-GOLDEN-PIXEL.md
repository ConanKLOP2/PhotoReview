---
id: NOWPF-WP28
order: 920
summary: |-
  WP-28 (đợt 3 NO-WPF, 2026-10-12): golden pixel G-PIX. 40 khung tham chiếu PNG (3,1 MB) ghi từ MainWindow WPF thật (RenderTargetBitmap, desktop ẩn) và bộ so sánh với khung cuối của shell Win32 (layout + composer + D2D WARP + thanh cuộn thật), 37 ca: Fit/zoom 25..400 %/100 % (DPI 1, 1,25, 1,5)/Linear/orientation EXIF 1..8/HUD bật-tắt; ngưỡng mục 7.3 (2) chạy trên CI (Category=Integration). ĐO THẬT (NE-9): ảnh chụp Fit PSNR 40,5-44,5 dB (ngưỡng 32), zoom khác 100 % 44-60 dB (ngưỡng 28), Linear giống tới ±1; 100 % DPI 1 giống từng byte, DPI 1,5 lệch ±1 mức (WPF); orientation 8/8 giống từng byte; hình học lệch 0 px (nửa pixel: 1 px); HUD hộp panel lệch 0-1 px. Mẫu "worst case" 1-2 px (WPF Fant không phải trung bình diện tích đúng ở 2:1): 21,8-26,8 dB, ghim sàn riêng. Bắt được 2 khác biệt thật: shell snap 100 % vào lưới pixel còn WPF làm mờ ở nửa pixel (WP28-HALFPIXEL); tham chiếu là bộ raster phần mềm của WPF (WP28-REFERENCE, CHƯA XÁC NHẬN).
---

# NOWPF-WP28-GOLDEN-PIXEL - golden pixel G-PIX (2026-10-12)

Thẻ: WP-28 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); ngưỡng: mục 7.3 (2) của [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md).
Tiền đề: [WP-10 recorder](NOWPF-WP10-GOLDEN-RECORDER.md) (golden từ WPF thật), [WP-15](NOWPF-WP15-D2D-RENDERER.md) (renderer, PSNR 47-56 dB so với
tham chiếu CPU), [WP-21](NOWPF-WP21-PRESENTATION.md), [WP-23](NOWPF-WP23-OVERLAY-HUD.md), [WP-26](NOWPF-WP26-SCROLLBARS.md). App WPF
(`PhotoReview.App`) và liên kết `.jpg` KHÔNG đổi; không sửa mã sản xuất (gói chỉ thêm test, công cụ và golden).

## 1. Đã làm

| Thành phần | File | Nội dung |
|---|---|---|
| Cảnh + ca | `tests/PhotoReview.TestSupport/PixelGolden/PixelGoldenScenes.cs`, `PixelGoldenCatalog.cs` | 3 cảnh sinh bằng số nguyên (không commit ảnh nguồn, byte giống hệt mọi máy): `photo` (gradient + kết cấu nhẹ + hình cạnh sắc), `chart` (vạch 1 px, ô cờ 2 px, thanh màu, vuông đồng tâm: ca xấu nhất cho bộ lọc), `orient` (4 góc khác màu + chữ F). 37 ca + 3 khung HUD-tắt |
| Công cụ đo | `PixelMetrics.cs`, `PixelGoldenComparer.cs`, `PngFile.cs`, `PixelDiffReport.cs` | PSNR, SSIM (luma, cửa sổ 8x8), MAE, max lệch kênh, số pixel lệch, dịch tốt nhất (+-3 px), hộp nội dung/vùng đổi (tìm panel HUD từ pixel), PNG đọc/ghi tất định không phụ thuộc thư viện ảnh, ảnh diff `chuẩn | shell | |lệch| x4` |
| Bộ ghi WPF | `tests/PhotoReview.Integration.Tests/Golden/PixelGoldenRecorder.cs`, `PixelGoldenRecordTests.cs` | `MainWindow` THẬT không hiện (như WP-10): client + DPI theo ca, bitmap 96 dpi thật trên `Image`, Fit/zoom/offset qua `ViewerState` + `ScrollViewer`, HUD bằng chuỗi cố định của G-OVL, `RenderTargetBitmap` -> `tests/Fixtures/golden/pixel/*.png`. `tools/diag/record-golden.ps1` ghi luôn (bộ lọc khớp tên lớp) |
| Bảo vệ golden | `PixelGoldenWpfConformanceTests` (`Category=UI`) | ghi lại trên WPF mỗi lần chạy, PSNR >= 42 dB so với PNG đã commit; tên file == danh mục; <= 5 MB |
| Test shell | `tests/PhotoReview.Shell.Tests/Golden/PixelGoldenTests.cs`, `PixelGoldenShellRenderer.cs` | `ScrollBarRig` (viewport + ViewerState thật) + `PresentationComposer`/`ImageLayer` + `D2DRenderSurface.CreateOffscreenSurface` (WARP) + `ShellScrollBars`; kích thước theo ca, không phụ thuộc màn hình runner. `Category=Integration` -> CHẠY TRÊN CI |
| Test HUD | `PixelGoldenHudTests.cs` (`Category=Native`: DirectWrite thật, như G-OVL shell) | hộp panel từ pixel, vùng ngoài panel, màu thân panel |
| Test công cụ | `PixelGoldenSupportTests.cs` (40 test, không cần GPU) | PSNR/SSIM/MAE/dịch/hộp/PNG/ảnh diff trên khung nhỏ làm tay + phán quyết ở biên ngưỡng |
| Báo cáo | `tools/diag/pixel-golden-report.ps1` | số đo từng ca + ảnh diff vào `work\pixel-golden-report` (`-IncludeHud` thêm HUD); test cổng cũng ghi ảnh diff vào `%TEMP%\PhotoReview-pixel-golden` (hoặc `PHOTOREVIEW_PIXEL_REPORT_DIR`) và nêu đường dẫn trong thông báo lỗi |

Tái tạo golden: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/diag/record-golden.ps1` (hoặc chỉ khung pixel:
`PHOTOREVIEW_GOLDEN_RECORD=1` + `tools/run-tests-hidden.ps1 <Integration.Tests.csproj> --filter FullyQualifiedName~PixelGoldenRecordTests`). `git diff
tests/Fixtures/golden/pixel` rỗng khi ghi lại hai lần liên tiếp (PNG tất định). Diff khác rỗng sau một thay đổi WPF là hành vi đã đổi: phải
xem ảnh, không ghi lại chỉ để test xanh.

## 2. Ngưỡng và số đo (máy dev, WARP; cùng số trên CI vì phía shell là sao chép/nội suy tất định)

Vùng so: ảnh nằm trong viewport (không có thanh cuộn), co vào 1 px mỗi mép (viền khử răng cưa do WPF và D2D phủ pixel phân số khác nhau).
Khung đầy đủ gồm canvas và thanh cuộn được so riêng ("chrome").

| Nhóm | Ngưỡng (thẻ 7.3 (2) + sàn đã ghim) | Số đo | Ca |
|---|---|---|---|
| 100 %, DPI 1, đặt đúng pixel | **giống từng byte** ở vùng ảnh; khung đầy đủ >= 60 dB | PSNR vô cực; khung 72,8-78,4 dB, thanh cuộn >= 59,9 dB | `z100-photo-origin/middle/end`, `z100-photo-small-centered`, `orient-1..8`, `hud-z100-nohud` |
| 100 %, DPI 1,5 | max lệch kênh <= 1, PSNR >= 50 | max 1, PSNR 54,7, MAE 0,22 (WPF tự làm +-1 trên ~20 % pixel) | `z100-chart-dpi150-end` |
| Fit, ảnh chụp | PSNR >= 32, SSIM >= 0,93, MAE <= 4 | PSNR **40,5-44,5** (Linear 55,3), SSIM >= 0,985, MAE 0,19-1,02 | `fit-photo-*` (landscape, portrait letterbox, upscale 2x, panorama, DPI 1,5, Linear) |
| Zoom != 100 %, ảnh chụp | PSNR >= 28, SSIM >= 0,88, MAE <= 6 | PSNR **44,2-49,6** (z200 Linear 59,1), SSIM >= 0,997; 100 % lệch pixel phân số: 36,7 dB; 100 % tâm nửa pixel: **28,3** dB (SSIM 0,951) | `z50/z75/z150/z200/z400-photo-*`, `z100-photo-fractional-offset`, `z100-photo-half-pixel-centered` |
| Mẫu 1-2 px ("worst case") | sàn ghim: PSNR >= 19, SSIM >= 0,40, MAE <= 17 (KHÔNG theo 7.3) | PSNR **21,8-26,8**, SSIM 0,49-0,95, MAE 6,3-14,0; Linear tại 50 %: giống từng byte | `fit-chart-*`, `z25-chart`, `z50-chart-middle`, `z200-chart-middle` (+ `z50-chart-linear`) |
| 100 % chart tại nửa pixel | hình học <= 1 px, PSNR >= 9 | dịch (0,-1), PSNR 11,6 | `z100-chart-dpi150-half-pixel` |
| Hình học | <= 1 px | dịch tốt nhất (0,0) ở mọi ca trừ hai ca nửa pixel (0,-1); sai số mép ảnh 0 (ca nửa pixel: 1) | tất cả |
| HUD (hộp panel từ pixel) | +-2 DIP | 5-6 panel/ca, lệch **0-1 px**; vùng ngoài panel PSNR 44,7 (Fit) / 79 (100 %) dB; màu thân panel lệch 0,5-1,8 mức (8 ở một mảnh của chỉ báo zoom vắt qua mép ảnh) | `hud-fit`, `hud-z100`, `hud-fit-dpi150` |

Chữ không được so (ClearType của WPF vs DirectWrite), theo 7.3 (2).

## 3. NE-9: D2D cubic/linear so với WPF Fant khi zoom != 100 % (kết quả đo)

- **Ảnh chụp:** không thấy khác biệt có ý nghĩa: Fit thu nhỏ 3x-4x 40-44 dB, phóng to 2x 42,5 dB, mọi zoom 25 %-400 % 44-60 dB. Ngưỡng của
  kế hoạch (32/28 dB) có biên 8-16 dB. Khuyến nghị (a) của NE-9 giữ nguyên: **không cần** thu nhỏ CPU bằng WIC Fant (b).
- **Mẫu 1-2 px:** thấp (21,8-26,8 dB) và đó là khác biệt THẬT của bộ lọc, không phải lệch hình học (dịch 0, mép 0). Nguyên nhân đo được:
  thu nhỏ đúng 2:1 một mẫu xen 250/60 cho WPF `HighQuality` (= `Fant`) ra 197,127,160,162,118,192,... (răng cưa), trong khi
  `TransformedBitmap` (WIC Fant) và `Linear` cho 155 phẳng (trung bình diện tích đúng); ở 0,4x cả hai cho 155. D2D `HighQualityCubic` cho
  kết quả mượt như trung bình diện tích. Nghĩa là ở các tỉ lệ mẫu khó, shell **sạch hơn** WPF, và PSNR thấp phản ánh răng cưa của WPF.
- **Linear:** giống tới +-1 mức ở mọi ca (55-59 dB) và giống từng byte ở 50 %.
- **100 %:** DPI 1 giống từng byte. DPI 1,5: WPF tự đưa +-1 mức (xem 2). Lệch thật duy nhất: đặt ở nửa pixel (mục 4).

## 4. Phát hiện / quyết định chưa xác nhận

1. **WP28-HALFPIXEL:** ở 100 % mà góc ảnh rơi vào nửa pixel thiết bị (client lẻ chiều rộng, hoặc offset cuộn phân số ở DPI > 1) WPF nội suy
   hai tuyến tính ảnh nên làm mờ, còn shell snap vào lưới pixel (`DrawGeometry.TrySnapPixelExact`, WP-15) nên giữ nét và lệch tối đa 0,5 px.
   Với ảnh chụp: 28,3 dB, SSIM 0,95 (đạt ngưỡng zoom). Với mẫu vạch 1 px: 11,6 dB. Giữ nguyên (sắc nét hơn, trong ngưỡng hình học 1 px).
   Hoàn tác (nếu người dùng muốn mờ như WPF): bỏ snap khi offset có phần 0,5 (một nhánh ở `D2DDrawContext`).
2. **WP28-REFERENCE:** golden là đầu ra của bộ raster PHẦN MỀM của WPF (`RenderTargetBitmap`), không phải đường phần cứng khi hiển thị lên màn
   hình. Với cùng mã Fant của WPF hai đường nên giống nhau, nhưng chưa so với ảnh chụp màn hình thật. Cách kiểm: một bước trong kịch bản
   thủ công WP-27b (mở cùng ảnh ở 50 %, so mắt hai bản). Nếu khác: ghi lại bằng chụp cửa sổ thật (đổi `Capture` ở recorder).
3. Chưa có G-PIX cho: so sánh hai ảnh (compare), crossfade giữa chừng, F11/DPI đổi giữa chừng, RAW (bitmap kích thước thay) - phần dựng
   hình đã có test WARP riêng ở WP-21 (opacity, ô, viền); G-PIX có thể thêm ca khi có nhu cầu (chỉ cần thêm dòng vào danh mục + ghi lại).

## 5. Kiểm chứng

- Build Release 0 cảnh báo / 0 lỗi; `PixelGolden*` 78 test xanh (Shell.Tests: 37 ca + 8x2 orientation + 3 HUD + kiểm danh mục/golden + 40 test công cụ);
  `PixelGoldenWpfConformanceTests` xanh trên desktop ẩn; Architecture, Shell, Shell.Integration, Core, Imaging, App, Integration (lọc CI) xanh.
- Đột biến thủ công: xem bảng ở PR/mục 6.

## 6. Đột biến

(bảng điền sau khi chạy; mỗi dòng: sửa gì, test nào đỏ, hoặc đột biến sống sót + test lấp)
