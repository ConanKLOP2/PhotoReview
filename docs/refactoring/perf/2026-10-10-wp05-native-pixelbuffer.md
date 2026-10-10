# WP-05 TurboJpeg ra PixelBuffer: độ lệch fine-scale và thời gian decode (2026-10-10)

Nhánh `feat/nowpf-wp05-native-pixelbuffer` (PR #413). Quyết định: [`NOWPF-WP05`](../decisions/NOWPF-WP05-NATIVE-PIXELBUFFER.md).
Tool: `TurboJpegWp05ManualTests` (Category=Manual, `tests/PhotoReview.Imaging.Tests/Pixels`), biến môi trường `PHOTOREVIEW_WP05_*`.

## Cách đo và độ tin cậy

- JPEG thật trong F4 (`C:\Xiuren\[[WALLPAPER]`): `Caviar-Fish-Mu-Mou-035` 4158x6237 = 25,9 MP (bench), thêm `Yang-Chenchen-007` 4160x6240 và `Seele-Mai-033` 4672x7008 (độ lệch).
- Bench: byte đọc sẵn vào RAM (không đĩa), 2 vòng khởi động + 12 vòng đo, xen kẽ đường WPF cũ (ctor không tham số) và đường PixelBuffer, `GC.Collect` giữa các vòng; báo P50 và min.
- **Máy bận:** CPU tổng 100 % suốt lúc đo (nhiều agent khác build/test song song, không điều khiển được). Số tuyệt đối cao hơn thường lệ 2-3 lần;
  chỉ so sánh trong cùng một lượt xen kẽ. Cần đo lại trên máy yên tĩnh trước khi WP-06 nối codec vào app.
- Base = `origin/master` @ 4add0edf (worktree riêng, cùng file test; ctor codec không tồn tại nên chỉ có cột WPF cũ).

## Độ lệch fine-scale so với WIC Fant (đường WPF), 3 ảnh thật

Kích thước luôn trùng WIC (15/15). MAE trên thang 0-255 (3 kênh), PSNR dB:

| Đích (rộng) | MAE (3 ảnh) | PSNR dB (3 ảnh) |
|---:|---|---|
| 1920 | 0,65 / 0,82 / 0,74 | 46,3 / 45,5 / 43,8 |
| 1280 | 0,62 / 0,66 / 0,73 | 46,3 / 46,0 / 43,7 |
| 800 | 0,61 / 0,58 / 0,77 | 46,0 / 45,9 / 43,3 |
| 400 | 0,79 / 0,73 / 1,12 | 43,0 / 43,2 / 40,6 |
| 150 | 0,28 / 0,26 / 0,40 | 51,2 / 51,6 / 49,4 |

Tệ nhất MAE 1,12 / PSNR 40,6 dB (ảnh nhiều chi tiết, đích 400): lệch 1 mức xám trung bình, không thấy được bằng mắt.
Con số fixture checkerboard (MAE <= 3,6) là cận trên cho nội dung cạnh sắc cực đoan, không phải ảnh thật.

## Thời gian decode (ms, 25,9 MP, đích = chiều rộng DecodeBox; DCT chọn tự động)

| Đích | Base master (WPF) P50 [min] | Nhánh, đường WPF cũ P50 [min] | Nhánh, đường PixelBuffer P50 [min] | PixelBuffer vs WPF cũ (P50) |
|---|---|---|---|---|
| đầy đủ (0) | 2027 [1841] | 2507 [1797] | 1993 [1567] | -20 % |
| 3000 (fine-scale, không DCT) | 1705 [1356] | 1873 [1599] | 3314 [2799] | +77 % |
| 1920 (DCT 1/2 + fine-scale) | 1093 [995] | 1421 [1165] | 2514 [1607] | +77 % |
| 800 (DCT 1/4 + fine-scale) | 852 [734] | 990 [704] | 1273 [819] | +29 % |

(Lượt đầu cùng máy, bận hơn: PixelBuffer vs WPF cũ +88 % / +48 % / +10 % theo min cho 3000/1920/800, -8 % cho đầy đủ.)

## Kết luận

- Không fine-scale (đầy đủ, hệ số DCT chính xác): đường PixelBuffer **nhanh hơn** WPF (bớt một lần copy BitmapSource.Create, WIC Fant, Materialize).
- Có fine-scale: bộ lọc diện tích số nguyên chạy một luồng, **chậm hơn WIC Fant 30-90 %** trên ảnh 26 MP: vượt ngưỡng 5 % của thẻ. App hiện vẫn dùng ctor không tham số
  (`DecoderProviders` chưa truyền codec), nên chưa có hồi quy đo được trong app; đường WPF cũ nhánh vs base lệch nằm trong nhiễu (khác biệt không nhất quán theo min).
- Khuyến nghị: trước khi WP-06 nối codec, thay fine-scale bằng IWICBitmapScaler Fant (nhanh hơn và giống từng byte với WPF) hoặc tối ưu `PixelAreaResampler`
  (SIMD/int32); xem decision.
