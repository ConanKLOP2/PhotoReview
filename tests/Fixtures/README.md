# Test Fixtures & Image Quality Metrics (WC3)

Tài liệu này mô tả hệ thống fixture ảnh mẫu và các công cụ đo chất lượng giải mã ảnh phục vụ đợt thay thế/tối ưu decoder (nhánh WC3: WpfBitmap, WicDirect, TurboJpeg).

---

## 1. Nguyên tắc thiết kế fixture

1. **Không commit ảnh lớn vào Git repo:**
   - Mọi fixture ảnh kiểm thử được tạo tự động khi chạy test thông qua `PhotoReview.Imaging.Tests.Fixtures.FixtureGenerator` trong thư mục tạm `Path.GetTempPath()`.
   - Các file tạm được dọn dẹp sạch sẽ sau khi test hoàn thành qua giao diện `IDisposable`.

2. **Dạng mẫu ảnh gradient + checkerboard:**
   - **Gradient:** Kênh Đỏ (Red) biến thiên theo trục ngang $x$, kênh Lục (Green) biến thiên theo trục dọc $y$. Giúp phát hiện lỗi banding, lượng tử hóa (quantization), hoặc sai lệch màu giữa các thuật toán giải mã.
   - **Checkerboard:** Ô bàn cờ xen kẽ màu Xanh dương (Blue) với tần số cao. Giúp đánh giá độ nét và độ chi tiết ở các cạnh tương phản cao (high-frequency detail).
   - **4 góc phân biệt:**
     - Góc trên-trái (Top-Left): Vàng (Yellow: R=255, G=255, B=0)
     - Góc trên-phải (Top-Right): Lục lam (Cyan: R=0, G=255, B=255)
     - Góc dưới-trái (Bottom-Left): Cánh sen (Magenta: R=255, G=0, B=255)
     - Góc dưới-phải (Bottom-Right): Trắng (White: R=255, G=255, B=255)
     Đặc điểm này là cốt lõi để xác minh tính đúng đắn khi áp dụng xoay/lật EXIF orientation (T83).

3. **Kích thước tiêu chuẩn:**
   - **64×48:** Ảnh thu nhỏ, kiểm tra nhanh (smoke test, thumbnail).
   - **4000×3000 (~12 MP):** Kích thước phổ biến của ảnh smartphone/máy ảnh tiêu chuẩn.
   - **8000×6000 (~48 MP):** Kích thước ảnh độ phân giải cao, đo đạc độ trễ giải mã nặng và áp lực bộ nhớ RAM.

---

## 2. Các danh mục kiểm thử đặc thù

- **Định dạng file:**
  - JPEG chất lượng 90 (`JpegBitmapEncoder`).
  - PNG 24-bit (`PixelFormats.Bgr24`) và PNG 32-bit (`PixelFormats.Bgra32`).
- **EXIF Orientation:**
  - Hỗ trợ các giá trị từ 1 đến 8 theo chuẩn TIFF/EXIF tag `0x0112` (`/app1/ifd/{ushort=274}`).
  - Kiểm tra khả năng xoay đúng hướng của các backend decoder khác nhau.
- **Profile màu ICC:**
  - Fixture dùng `ColorProfiles/DisplayP3-v4.icc` cố định, SHA-256 `CB51DE38E482EE974C0C76B9689E16AAD04BAD16E226FED2F30C842D15FF3A3D`.
  - Profile lấy từ `saucecontrol/Compact-ICC-Profiles`, phát hành public domain theo CC0-1.0; bản license đi kèm trong cùng thư mục.
  - WicDirect hiện từ chối ảnh có color context bằng `NotSupportedException`, để factory fallback sang WPF thay vì trả pixel chưa biến đổi sang sRGB.
- **File hỏng (Corrupt / Fault Injection):**
  - File rỗng 0 byte.
  - File JPEG bị cắt ngắn còn 50% byte (`truncated`).
  - File text thuần túy giả mạo đuôi `.jpg`.
  - Bộ giải mã phải xử lý an toàn bằng cách ném các ngoại lệ chuẩn (`FileFormatException`, `IOException`, `NotSupportedException`) thay vì làm crash tiến trình.

---

## 3. Thước đo chất lượng (Quantitative Quality Metrics)

Mọi phép so sánh được thực hiện trên pixel buffer chuẩn hóa `BGRA32` trích xuất qua `ImageCompare.ToBgra32(source)`:

1. **PSNR (Peak Signal-to-Noise Ratio):**
   $$\text{MSE} = \frac{1}{3N} \sum_{i=0}^{N-1} \left( (B_{1,i} - B_{2,i})^2 + (G_{1,i} - G_{2,i})^2 + (R_{1,i} - R_{2,i})^2 \right)$$
   $$\text{PSNR} = 10 \cdot \log_{10} \left( \frac{255^2}{\text{MSE}} \right) \quad (\text{dB})$$
   - Khi hai ảnh trùng khớp hoàn toàn ($\text{MSE} = 0$), $\text{PSNR} = \infty$.
   - Giá trị kỳ vọng cho ảnh giải mã chất lượng cao: $\text{PSNR} \ge 35\text{ dB}$.

2. **Mean $\Delta E$ (CIE76):**
   - Chuyển đổi màu từ sRGB sang không gian màu cảm nhận CIE $L^*a^*b^*$ chuẩn D65.
   $$\Delta E^* = \sqrt{(\Delta L^*)^2 + (\Delta a^*)^2 + (\Delta b^*)^2}$$
   - Tính trung bình $\Delta E$ trên toàn bộ pixel của ảnh.
   - Khi hai ảnh trùng khớp: $\text{Mean}\Delta E = 0$.
   - Sai biệt mắt người khó phân biệt được khi $\Delta E < 1.0$.

3. **MaxChannelDiff:**
   $$\text{MaxDiff} = \max_{i, c} |C_{1,i} - C_{2,i}|$$
   - Sai lệch cực đại trên bất kỳ byte kênh màu nào giữa 2 ảnh.

---

## 4. Kiểm thử với ảnh thật (`PHOTOREVIEW_FIXTURE_DIR`)

Để chạy test hoặc benchmark so sánh trên tập ảnh chụp thực tế:
1. Đặt biến môi trường `PHOTOREVIEW_FIXTURE_DIR` trỏ tới thư mục chứa ảnh:
   ```powershell
   $env:PHOTOREVIEW_FIXTURE_DIR = "D:\Photos\RealDataset"
   ```
2. Các test sử dụng ảnh thật được gắn `[Trait("Category", "Manual")]` để không làm chậm luồng kiểm thử tự động (CI/verify-all).
