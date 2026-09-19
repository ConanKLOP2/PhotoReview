# Tiến độ

## Cập nhật mới nhất — 2026-09-19 (đồng bộ trạng thái)

- Branch làm việc hiện tại `codex/w6-performance`, sau khi cherry-pick T62/T63/T64 là `192fa6f` (T62), `ec2dd64` (T63), `980649a` (T64), working tree đang sạch; chưa push. MR01–MR06/F01–F07 đã DONE, PR #5 đã merge vào `master`; validation chi tiết ở `docs/refactoring/results/merged-code-review-validation.md`.
- T88 (ScalingQuality) DONE, chờ nhận xét thủ công zoom 400% ở hai chế độ.
- **T47 DONE (an toàn):** xoá 60 test đã có thay thế thật, giữ 10 test chưa có thay thế; chi tiết ở `test-parity.md` mục "T47 outcome". 
- **T52 IN PROGRESS:** đã bỏ Folder2Name, chuyển AppConstants → Core `PerformanceOptions`, và thêm comment cho `catch { }`; còn marker tương thích T46d và bind PerformanceOptions từ settings. T50a/b, T51, T53a/b, T60, T61 đã DONE.
- T50a, T50b, T51, T53a, T53b, T60, T61, T62, T63, T64 DONE (CLI ở `tools/PhotoReview.Benchmark.Cli`; đã bỏ WinForms). T62 dùng tail-read journal; T63 truyền metadata catalog để tránh stat lặp; T64 áp dụng RAM budget policy. Còn kiểm tra thủ công hộp thoại folder/file và placement đa màn hình. Tiếp theo: chạy D07 rút gọn, rồi T87 (setting + fallback, Wpf giữ mặc định tới T66/GUI) và T65 (feature flag, chưa bật mặc định tới T66). Q5, Q8 và phạm vi D07 đã được chốt.
- **Quyết định mới:** Q5 giữ SourceBytesCache tắt mặc định cho tới T66; Q8 tích hợp setting/fallback trước, giữ Wpf mặc định tới khi T66 + GUI acceptance đạt; D07 chạy đợt rút gọn S1/S2/S3/S6/S9 với warm/cold-app+cold-diskcache và biến thể worker/disk-cache/PREREAD.
- Bảng `REFACTOR-TASKS.md` đã đồng bộ T25b/T26a/T26b/T32/T33a/T33b sang DONE theo mô tả Wave 3 ở dưới; nên đối chiếu lại với code.

- Validation sau hợp nhất T62–T64: OperationJournalTests 14/14, FolderLoadCoordinatorTests 6/6, RamBudgetPolicyTests 5/5. Full `verify-all.ps1` và publish Release còn phải chạy lại trên branch này trước bàn giao.
- D07 probe đã xác định thêm lỗi idle: `ReviewMetrics.Snapshot()` tạo collection mới, nên `record.Equals` luôn false dù counter đứng yên; `WaitIdleAsync` reset ổn định liên tục. Commit `2bedf09` so sánh metric theo giá trị. Probe S1/F1/Preview/cold-app chạy pass, `idleTimeouts=0`, `errors=[]`, 58 ảnh, idle sau 1.043 giây. Harness hiện đủ điều kiện để chạy ma trận rút gọn.


## Review mới nhất — 2026-09-18

- Baseline đã review: `refactor/integration` / `afb2f77` (khác `master`). Mục tiêu: review code đã merge, chỉ lập plan/task fix.
- Đã đọc App/DI/MainWindow, Imaging/preload/cache/decoder, settings/journal, test/CI và bốn tài liệu refactor/diagnosis. Tạo `docs/refactoring/MERGED-CODE-REVIEW-PLAN.md`, `MERGED-CODE-REVIEW-TASKS.md`; thêm link ở `REFACTOR-TASKS.md`.
- Findings F01–F07: memory probe giả; dispose không cancel; gate che lỗi/CI thiếu App.Tests; thiếu ICC WicDirect + quality gate yếu; Turbo fallback exception sai; Turbo metadata backend sai; cache decode đọc backend khác snapshot key. Chi tiết và mức chứng cứ trong plan.
- Validation lịch sử của review baseline: **615 passed, 1 skipped, 0 failed** (Imaging 143). Sau các task T50–T61, VERIFY gần nhất đạt **728 passed, 0 failed, 0 skipped** trên 5 project test; smoke, fault injection, publish và verify-release PASS. GUI thủ công, D07 và CI remote chưa có bằng chứng mới trong hồ sơ local.
- Bàn giao tài liệu: người dùng đã yêu cầu commit/push; branch `codex/merged-code-review-plan` từ `afb2f77` (đã fetch và khớp origin/refactor/integration). Máy khác: `git fetch origin`, `git switch --track origin/codex/merged-code-review-plan`. Log/probe work/ chỉ local; bằng chứng tóm tắt có trong plan.
- Tiếp tục: MR01–MR06 đã tích hợp xong. Các số test và ghi chú trong phần handoff cũ bên dưới là lịch sử, không dùng làm kết quả hiện tại.

## Handoff trước review (giữ làm bối cảnh)

- **Cập nhật:** 2026-09-19 | **Branch làm việc:** `refactor/T46d-mainwindow-binding` -> `refactor/integration` · VERIFY đạt, xUnit 746/746 (Core: 245, Imaging: 144, Integration: 8, Architecture: 6 (Rule 6 K-2 enabled, 0 skipped), App.Tests: 87, Tests.Unit: 256)
- **Môi trường hiện tại:** **Máy 1 (Máy chính)** · Intel Core i7-9750H, 32 GB RAM, NVMe SSD · Đầy đủ fixture ảnh thật (F1–F4) tại `work/diag/fixtures.local.json` và công cụ chẩn đoán (dotnet-counters, dotnet-trace, Process Monitor).
- **Mục tiêu:** Tái cấu trúc B + C3 (`docs/refactoring/REFACTOR-PLAN.md`, `REFACTOR-TASKS.md`) kết hợp chẩn đoán hiệu năng WD (`PERF-DIAGNOSIS-PLAN.md`, `PERF-DIAGNOSIS-TASKS.md`). **ĐÃ HOÀN TẤT WAVE 1, WAVE 2, WAVE 3 (T30–T35), TOÀN BỘ NHÁNH C3 (T80–T86) VÀ HOÀN TẤT T40–T46d TRONG WAVE W4 (COMPOSITION ROOT DI + MVVM + MAINWINDOW BINDING)**.

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

- **Refactor:** T00, T02, T03, T04, T05, T10, T11, T12, T13a, T13b, T14a, T14b, T14c, T14d, T20, T21a, T22a, T22b, T22c, T23a, T23b, T21b, T21c, T24, T25a, T25b, T26a, T26b, T26c, T30, T31a, T31b, T32, T33a, T33b, T31c, T34, T35 (Wave 1, Wave 2, Wave 3 hoàn tất 100%), **T80** (procedural fixture generator, pixel metrics PSNR/DeltaE Lab D65/MaxDiff, image comparison; xUnit: 37/37), **T83** (ExifOrientation 1..8, xoay ảnh và swap width/height thị giác tự động cho WpfBitmapImageDecoder, ImageInfo, ImageCacheKey, PreviewImageService, ThumbnailCache; xUnit: 71/71), **T84** (IImageDecoderFactory, FallbackImageDecoder bắt lỗi fallback và ném FileNotFound an toàn theo INV-12, ghi nhận ActualBackend và metrics, DecoderBackend trong ImageCacheKey và PreviewImageService; xUnit: 83/83), **T82** (WicDirectDecoder qua native COM Interop WindowsCodecs.dll, DCT transform, IWICBitmapScaler HighQualityCubic/Fant, IWICFormatConverter 32bppBGRA, IWICBitmapFlipRotator, pinned GCHandle buffer copy INV-8; xUnit: 105/105), **T81** (`--decoder-bench`: công cụ CLI benchmark phân tích và so sánh P50, P95, Mean, Throughput MP/s, Peak RAM giữa các backend giải mã), **T85** (`TurboJpegDecoder`: tích hợp libjpeg-turbo 3.x P/Invoke, DCT scaling factor, APP1 orientation, APP2 ICC profile detection & fallback, dynamic factory discovery, auto-fetch native DLL trong MSBuild target và CI workflow), **T86** (Cổng chất lượng `DecoderQualityGateTests.cs` đạt 14/14 test PASS, benchmark `--decoder-bench` 2,088 phép đo trên 58 ảnh 24 MP, tài liệu quyết định kiến trúc ADR 0001 chọn `WicDirect` làm default backend, `Wpf` làm fallback an toàn INV-12, `TurboJpeg` làm tùy chọn phụ; xUnit: 607/607), **T40** (Composition Root: cấu hình Central Package Management `CommunityToolkit.Mvvm` 8.4.0 và `Microsoft.Extensions.DependencyInjection` 10.0.0; triển khai `ConfigureServices` đăng ký đủ 19 dịch vụ và factory; DI constructor cho `MainWindow` bảo toàn 100% seam kiểm thử T14a; tạo mới project kiểm thử `PhotoReview.App.Tests` với 4/4 test PASS; xUnit: 616/616, `verify-all.ps1` PASS 100%), **T41a** (`ReviewCatalog` + `CatalogEntry`: mô hình hóa danh mục duyệt ảnh độc lập trong Core, hỗ trợ Remove nextIndex, Restore clamp, ReplaceOrder xác thực tập file, InsertSorted, Snapshot; xUnit Core: 211/211; tổng xUnit: 628/628, `verify-all.ps1` PASS 100%), **T41b** (`GenerationClock`: bộ đếm thế hệ Navigation, Folder, Interaction thread-safe cho Core, hỗ trợ StopForAction; xUnit Core: 219/219; tổng xUnit: 636/636, `verify-all.ps1` PASS 100%), **T42a** (`FileActionService`: dịch vụ điều phối Move/Copy/Recycle độc lập với UI/Catalog, cổng gate bận INV-4, xác thực đường dẫn và kiểm tra kích thước hậu kiểm, ghi OperationJournal; xUnit Core: 229/229; tổng xUnit: 646/646, `verify-all.ps1` PASS 100%), **T42c** (`DuplicateFinder`: thuật toán tìm kiếm và lọc tệp trùng lặp 3 bước theo kích thước, hash và hậu tố bản sao, hỗ trợ CancellationToken; xUnit Core: 236/236; tổng xUnit: 653/653, `verify-all.ps1` PASS 100%), **T44** (`FolderLoadCoordinator`: đóng gói luồng nạp thư mục bất đồng bộ độc lập với UI, bảo toàn INV-7 Explorer snapshot trễ, INV-9 mở trực tiếp file ban đầu, tương tác qua `IFolderLoadSink`; xUnit App.Tests: 10/10; tổng xUnit: 659/659, `verify-all.ps1` PASS 100%), **T42b** (`UndoService`: quản lý hoàn tác Move và Recycle độc lập UI, cổng bận INV-4, nạp lịch sử từ journal, sửa lỗi P12 so khớp case-insensitive, bảo toàn fingerprint; xUnit Core: 245/245; tổng xUnit: 668/668, `verify-all.ps1` PASS 100%), **T43a** (`ShortcutRouter`: định tuyến phím tắt phím đơn/tổ hợp độc lập UI, bảo toàn thứ tự ưu tiên Window_KeyDown, phân biệt Alt+F11/SystemKey, Esc, Ctrl+Z, Compare, custom actions; xUnit App.Tests: 24/24; tổng xUnit: 682/682, `verify-all.ps1` PASS 100%), **T43b** (`ViewerState`: quản lý mức Zoom 0.25..4.0, enum riêng ViewerStretchMode tuân thủ K-2, IsFit, IsFullscreen, MaxWidth/Height kẹp viewport, ZoomIn/Out, WheelZoom, ResetFit; xUnit App.Tests: 35/35; tổng xUnit: 693/693, `verify-all.ps1` PASS 100%), **T45a** (`StatusFormatter`: gom toàn bộ định dạng chuỗi thanh trạng thái và kích thước tệp tin nguyên bản từ MainWindow thành lớp tĩnh độc lập UI; xUnit App.Tests: 46/46; tổng xUnit: 704/704, `verify-all.ps1` PASS 100%), **T45b** (`CompareViewModel`: điều phối so sánh 2 ảnh song song tuân thủ K-2, nạp preview và MD5 hash song song với Task.WhenAll, kiểm tra hủy theo thế hệ token bảo toàn INV-1; xUnit App.Tests: 56/56; tổng xUnit: 714/714, `verify-all.ps1` PASS 100%), **T45c** (`ImagePresenter`: đóng gói toàn bộ luồng trình diễn ảnh và xóa file mất độc lập UI, tương tác qua IPresentationSink/IPreloadController, tích hợp CompareViewModel và SessionStore, bảo toàn INV-1 tại mọi điểm await; xUnit App.Tests: 64/64; tổng xUnit: 722/722, `verify-all.ps1` PASS 100%), **T46a** (`MainViewModel`: điều phối mở folder/drag-drop, điều hướng Next/Prev/First, Skip lưu session, Next/Prev anh em kiểm tra folder generation, ToggleFit/Zoom/Fullscreen, tăng thế hệ Interaction trên GenerationClock tại mọi tương tác, tuân thủ tuyệt đối K-2 không phụ thuộc WPF UI types, kích hoạt chính thức Rule 6 trong LayerDependencyTests đạt 6/6 test; xUnit App.Tests: 70/70; tổng xUnit: 729/729, `verify-all.ps1` PASS 100%), **T46b** (`MainViewModel`: thực thi action profiles Move/Copy/Recycle, hoàn tác Move `UndoAsync` Ctrl+Z, hoàn tác thao tác gần nhất `UndoLastAsync`, bảo toàn INV-3 trình diễn trước I/O, INV-4 gate bận, INV-5 khôi phục catalog khi thất bại, stale folder guard, tạo WpfDialogService; xUnit App.Tests: 79/79; tổng xUnit: 738/738, `verify-all.ps1` PASS 100%), **T46c** (`MainViewModel`: tìm kiếm xử lý ảnh trùng lặp `RemoveDuplicatesAsync` bảo vệ stale folder guard và reload thư mục, xóa sạch bộ nhớ đệm `ClearCacheAsync` và nạp lại ảnh hiện tại, điều phối mở các cửa sổ tiện ích độc lập `ShowRecovery()`, `ShowDiagnostics()`, `ShowBenchmark()`, `ShowSettings()` tự động reload khi đổi LoadingMode, mở folder qua dialog `PickAndOpenFolderAsync()`; hoàn thiện `WpfDialogService` hiện thực đủ 9 method `IDialogService`; xUnit App.Tests: 87/87; tổng xUnit: 746/746, `verify-all.ps1` PASS 100%), **T46d** (`MainWindow` binding, rút gọn code-behind: hoàn thiện MVVM Data Binding cho `MainWindow.xaml`, rút gọn `MainWindow.xaml.cs` xuống 244 dòng (≤ 250 dòng), bảo toàn 100% reflection harnesses INV-3, INV-4, INV-5, INV-7, INV-9, sửa fallback Explorer snapshot trong `FolderLoadCoordinator`, thêm compatibility markers cho `SourcePresenceTests`; xUnit: 746/746 tests PASS, `verify-all.ps1` PASS 100%).
- **Chẩn đoán:** D00, D03, D04, D05, D06, D10, D11 (nhật ký trong `PERF-DIAGNOSIS-TASKS.md`).
- **Công cụ đo có trong repo:**
  - Event `PhotoReview-Perf` + CSV (`PHOTOREVIEW_PERF_TRACE`).
  - Các biến `PHOTOREVIEW_DIAG_PREREAD`, `PHOTOREVIEW_DIAG_PRELOAD_WORKERS`, `PHOTOREVIEW_DIAG_DISABLE_DISKCACHE`.
  - `--io-decode-split`.
  - `--perf-session` (driver trong process; xem `docs/refactoring/diagnosis/perf-session.md`).
  - `tools/diag/run-matrix.ps1`.
  - `--perf-analyze` + `tools/diag/rules.json`.
  - `tools/diag/parse-applog.ps1`, `tools/diag/procmon-summary.ps1`.
  - `--decoder-bench` (so sánh hiệu năng và bộ nhớ giữa các decoder backend: Wpf, WicDirect, TurboJpeg).

## Số liệu sơ bộ (máy cũ)

- `docs/refactoring/diagnosis/io-decode-split.md`:
  - Decode chiếm 98–100% thời gian so với đọc file (file đã nằm trong cache OS).
  - JPEG 48 MP decode khoảng 1,3 s dù hạ width.
  - Disk cache PNG lỗ với ảnh decode rẻ, lãi 2,7–12× với ảnh decode đắt.
- Chạy thử S3 warm trên F1: P50 5,2 ms, hit 99,5%. Trong 10% lần chậm nhất, `t_render` chiếm khoảng 92% (chưa kết luận; có thể do cửa sổ test không ở foreground).
- **Benchmark decoder tổng hợp (T86 trên Fixture F1 24 MP):** `WicDirect` P50 = 7.67–8.11 ms trên mọi độ phân giải (nhanh gấp **4.45x** ở 1080p, **6.55x** ở 1440p, **12.52x** ở 4K so với `Wpf`, và nhanh gấp **3.5x** so với `TurboJpeg`).

## Việc tiếp theo

1. **Lựa chọn task tiếp theo:**
   - **T47 (`Xóa test source-presence`):** Thay thế/xóa các test source presence theo test-parity.md.
   - **T88 (`BitmapScalingMode` chất lượng cao + setting):** Thêm cài đặt chế độ scale ảnh và chất lượng hiển thị.
   - Hoặc chạy đo hiệu năng **D07**.
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
- **Lỗi lịch sử đã xử lý:** `PreloadScheduler` từng gọi `Dispatcher.Yield()` trên đường CLI/không có Dispatcher; T32 và MR01 đã chuyển đường yield sang cơ chế không phụ thuộc UI Dispatcher. Không dùng số liệu preload cũ trước các thay đổi này cho D12.
- **Quy trình:**
  - Task giao theo bảng model/review (mục 0 của `REFACTOR-TASKS.md`).
  - Worker không sửa file task, không `git add -A`, không push.
  - Coordinator merge vào `refactor/integration`, chạy `tools/verify-all.ps1`, rồi push.
  - Chỉ vào `master` qua PR (AGENTS.md, T05).
- **Bài học review:** Haiku từng bịa tên method (T10), sửa ngoài phạm vi (T12), gắn Trait làm test bị loại khỏi CI (T13a). Coordinator luôn phải đọc diff. Task tra cứu tên phải có script kiểm.
- **Giới hạn sử dụng:** agent từng bị dừng vì HTTP 429 (giới hạn chi tiêu). Nếu reviewer R3 không chạy được thì Coordinator tự review và ghi vào nhật ký.
- **Thư mục chỉ có trên máy cũ** (không cần mang theo): `work/diag/io-split`, `work/diag/runs-smoke` (bản tóm tắt đã có trong `docs/`), `work/diag/config.backup.json` (config đã được khôi phục).
