# Tiến độ

- **Cập nhật:** 2026-09-18 | **Branch làm việc:** `refactor/integration` · VERIFY đạt, xUnit 573/573 (Core: 199, Imaging: 105, Integration: 8, Architecture: 5 (+1 skip), Tests.Unit: 256)
- **Môi trường hiện tại:** **Máy 1 (Máy chính)** · Intel Core i7-9750H, 32 GB RAM, NVMe SSD · Đầy đủ fixture ảnh thật (F1–F4) tại `work/diag/fixtures.local.json` và công cụ chẩn đoán (dotnet-counters, dotnet-trace, Process Monitor).
- **Mục tiêu:** Tái cấu trúc B + C3 (`docs/refactoring/REFACTOR-PLAN.md`, `REFACTOR-TASKS.md`) kết hợp chẩn đoán hiệu năng WD (`PERF-DIAGNOSIS-PLAN.md`, `PERF-DIAGNOSIS-TASKS.md`). **ĐÃ HOÀN TẤT WAVE 1, WAVE 2, WAVE 3 (T30–T35) VÀ TIẾN TRIỂN MẠNH NHÁNH WC3 VỚI T80, T83, T84, T82 (WicDirectDecoder qua COM Interop WIC trực tiếp)**.

## Chuyển sang máy khác (làm theo thứ tự)


1. **Lấy code**
   ```powershell
   git clone https://github.com/ConanKLOP2/PhotoReview.git
   cd PhotoReview
   git switch refactor/integration
   ```
2. **SDK:** cài .NET SDK 10.0.401 hoặc mới hơn (`global.json` dùng `rollForward=latestFeature`).
3. **Kiểm tra máy mới:**
   ```powershell
   .\tools\verify-all.ps1
   ```
   Kỳ vọng xUnit 250/250 và dòng `PASS: all PhotoReview verification gates`.
4. **Fixture ảnh:** copy `tools/diag/fixtures.example.json` thành `work/diag/fixtures.local.json`, rồi sửa đường dẫn F1/F1b/F2/F3/F4 theo máy mới.
   - Ghi file bằng `ConvertTo-Json` hoặc editor; mỗi backslash phải viết thành `\\`.
   - Máy cũ dùng một thư mục ảnh 12,9 GB, gồm 1.842 ảnh (1.820 JPEG, 22 PNG).
   - Máy cũ không có F5, F6, F7.
   - **Không commit tên hay đường dẫn thư mục ảnh.**
5. **Công cụ đo** (hỏi người dùng trước khi tải hoặc cài):
   - `dotnet tool install -g dotnet-counters`
   - `dotnet tool install -g dotnet-trace`
   - Process Monitor: tải `https://download.sysinternals.com/files/ProcessMonitor.zip`, giải nén vào `work/tools/procmon/`, kiểm chữ ký Microsoft bằng `Get-AuthenticodeSignature`.
   - `wpr` có sẵn trong Windows.
6. **Ghi lại môi trường:** chạy lại phần "thông tin máy" của D00 và thêm một mục **"Máy 2"** vào `docs/refactoring/diagnosis/env.md`. Không ghi đè số liệu của máy cũ: số liệu D05/D11 hiện có được đo trên máy cũ (i7-9750H, 32 GB, NVMe), nên **không trộn số liệu giữa hai máy** khi so sánh.
7. **Kiểm tra nhanh công cụ đo:**
   ```powershell
   .\tools\diag\run-matrix.ps1 -Scenarios s3-next-burst -Modes Preview -Conditions warm -Repeat 1 -FixtureAlias F1 -OutRoot work/diag/runs-smoke
   dotnet run --project tests/PhotoReview.Tests -c Release -- --perf-analyze <thư mục batch vừa tạo>
   ```

## Đã xong

- **Refactor:** T00, T02, T03, T04, T05, T10, T11, T12, T13a, T13b, T14a, T14b, T14c, T14d, T20, T21a, T22a, T22b, T22c, T23a, T23b, T21b, T21c, T24, T25a, T25b, T26a, T26b, T26c, T30, T31a, T31b, T32, T33a, T33b, T31c, T34, T35 (Wave 1, Wave 2, Wave 3 hoàn tất 100%), **T80** (Wave C3 bắt đầu: procedural fixture generator, pixel metrics PSNR/DeltaE Lab D65/MaxDiff, image comparison; xUnit: 37/37), **T83** (ExifOrientation 1..8, xoay ảnh và swap width/height thị giác tự động cho WpfBitmapImageDecoder, ImageInfo, ImageCacheKey, PreviewImageService, ThumbnailCache; xUnit: 71/71), **T84** (IImageDecoderFactory, FallbackImageDecoder bắt lỗi fallback và ném FileNotFound an toàn theo INV-12, ghi nhận ActualBackend và metrics, DecoderBackend trong ImageCacheKey và PreviewImageService; xUnit: 83/83), **T82** (WicDirectDecoder qua native COM Interop WindowsCodecs.dll, DCT transform, IWICBitmapScaler HighQualityCubic/Fant, IWICFormatConverter 32bppBGRA, IWICBitmapFlipRotator, pinned GCHandle buffer copy INV-8; xUnit: 105/105; nhật ký chi tiết trong `REFACTOR-TASKS.md`).
- **Chẩn đoán:** D00, D03, D04, D05, D06, D10, D11 (nhật ký trong `PERF-DIAGNOSIS-TASKS.md`).
- **Công cụ đo có trong repo:**
  - Event `PhotoReview-Perf` + CSV (`PHOTOREVIEW_PERF_TRACE`).
  - Các biến `PHOTOREVIEW_DIAG_PREREAD`, `PHOTOREVIEW_DIAG_PRELOAD_WORKERS`, `PHOTOREVIEW_DIAG_DISABLE_DISKCACHE`.
  - `--io-decode-split`.
  - `--perf-session` (driver trong process; xem `docs/refactoring/diagnosis/perf-session.md`).
  - `tools/diag/run-matrix.ps1`.
  - `--perf-analyze` + `tools/diag/rules.json`.
  - `tools/diag/parse-applog.ps1`, `tools/diag/procmon-summary.ps1`.

## Số liệu sơ bộ (máy cũ)

- `docs/refactoring/diagnosis/io-decode-split.md`:
  - Decode chiếm 98–100% thời gian so với đọc file (file đã nằm trong cache OS).
  - JPEG 48 MP decode khoảng 1,3 s dù hạ width.
  - Disk cache PNG lỗ với ảnh decode rẻ, lãi 2,7–12× với ảnh decode đắt.
- Chạy thử S3 warm trên F1: P50 5,2 ms, hit 99,5%. Trong 10% lần chậm nhất, `t_render` chiếm khoảng 92% (chưa kết luận; có thể do cửa sổ test không ở foreground).

## Việc tiếp theo

1. **Lựa chọn task tiếp theo:**
   - **Nhánh WC3 (Thay decoder C3):**
     - **T81** (`--decoder-bench`: CLI benchmark so sánh decode ms P50/P95, memory peak giữa các backend).
     - Hoặc **T85** (`TurboJpegDecoder`: libjpeg-turbo C3 decoder).
     - Hoặc **T86** (Cổng chất lượng + benchmark + ADR 0001).
   - **Nhánh W4 (Application architecture):** Bắt đầu bằng **T40** (`Composition root DI + Mvvm`).
   - Hoặc chạy đo hiệu năng D07.
2. **D07 (ma trận đo trên máy 1):** Đã sẵn sàng trên máy chính (đầy đủ fixture F1..F4 và công cụ đo). Chờ người dùng quyết định:
   - Phạm vi: rút gọn (khoảng 1 giờ), đầy đủ (vài giờ), hoặc tiếp tục refactor trước rồi đo sau.
   - Có cho xóa cache preview/thumbnail của app để đo cold-diskcache không.
   - Process Monitor (cần bấm UAC) cho phần đo lại D02.
   - Có reboot để đo cold-OS không.
3. **D01/D02 (BLOCKED, chỉ máy 1):** đo lại trong D07 bằng `--perf-session` (và Procmon cho D02). Không dùng `SendInput`.
4. **Sau đó (máy 1):** D08 (WPR/PresentMon; WPA và PresentMon chưa cài), D09 (dotnet-counters), rồi D12 (báo cáo và quyết định), D13 (cập nhật plan).

## Lưu ý quan trọng

- **Quy tắc 4 (`PERF-DIAGNOSIS-TASKS.md`):** không bao giờ gửi input ở mức OS (`SendInput`, `SendKeys`, `SetForegroundWindow`, …) và không đụng clipboard. Ngày 2026-09-16, phím mô phỏng của D01 đã rơi vào cửa sổ Claude Code.
- **Config thật của người dùng** có action Enter = Move vào một thư mục ảnh thật. `--perf-session` thay toàn bộ action trong bộ nhớ và chỉ chạy action trên bản sao tạm. Không ghi `config.json` thật.
- **Lỗi đã biết, chưa sửa:** `PreloadScheduler` gọi `Dispatcher.Yield()` khi không có Dispatcher, nên preload dừng sau lô đầu (benchmark CLI và BenchmarkWindow). Đã chuyển cho T32; D12 không dùng số liệu preload benchmark cũ.
- **Quy trình:**
  - Task giao theo bảng model/review (mục 0 của `REFACTOR-TASKS.md`).
  - Worker không sửa file task, không `git add -A`, không push.
  - Coordinator merge vào `refactor/integration`, chạy `tools/verify-all.ps1`, rồi push.
  - Chỉ vào `master` qua PR (AGENTS.md, T05).
- **Bài học review:** Haiku từng bịa tên method (T10), sửa ngoài phạm vi (T12), gắn Trait làm test bị loại khỏi CI (T13a). Coordinator luôn phải đọc diff. Task tra cứu tên phải có script kiểm.
- **Giới hạn sử dụng:** agent từng bị dừng vì HTTP 429 (giới hạn chi tiêu). Nếu reviewer R3 không chạy được thì Coordinator tự review và ghi vào nhật ký.
- **Thư mục chỉ có trên máy cũ** (không cần mang theo): `work/diag/io-split`, `work/diag/runs-smoke` (bản tóm tắt đã có trong `docs/`), `work/diag/config.backup.json` (config đã được khôi phục).
