# PhotoReview — Task chẩn đoán hiệu năng (D00–D13)

- **Plan:** [`PERF-DIAGNOSIS-PLAN.md`](PERF-DIAGNOSIS-PLAN.md) · **Cập nhật:** 2026-09-16
- **Quy trình chung:** giống [`REFACTOR-TASKS.md`](REFACTOR-TASKS.md) mục 0 (một task mỗi phiên, chỉ sửa file trong **Files**, có VERIFY, ghi nhật ký theo mẫu).
- **Model và review:** theo bảng "Phân model và quy trình review" trong `REFACTOR-TASKS.md` mục 0:
  - D00, D01, D02, D07: Haiku 4.5, review R1.
  - D03, D05, D08, D09, D10, D11: Sonnet 5, review R2.
  - D04, D06, D12, D13: Opus 5, review R3.
- **Branch:** `diag/<ID>-<slug>`, tách từ `refactor/integration` (sau T00).
- **Đường dẫn:** các task này chạy **trước** T24, nên dùng layout cũ (`PhotoReview.App/`, `PhotoReview.Tests/`, `PhotoReview.Tests.Unit/`).
- **Quy tắc riêng:**
  1. Không tải hoặc cài công cụ khi chưa hỏi người dùng. Không đổi cài đặt hệ thống hay bảo mật. Các bước cần admin (RAMMap, reboot) do **người dùng** làm.
  2. Khi không có biến môi trường chẩn đoán, **hành vi app không được đổi**. Mọi test hiện có phải đạt.
  3. Không commit ảnh thật, đường dẫn cá nhân, hay file dữ liệu thô > 5 MB (chỉ commit bản tóm tắt).
  4. **Không bao giờ** gửi input ở mức hệ điều hành (`SendInput`, `SendKeys`, `keybd_event`, `mouse_event`, `AttachThreadInput`, `SetForegroundWindow`) và không đọc/ghi clipboard. Máy này là desktop dùng chung với ứng dụng Claude và các phiên khác: ngày 2026-09-16, phím mô phỏng của D01 đã rơi vào cửa sổ Claude Code. Chỉ điều khiển app trong chính process của agent (routed event, D06), hoặc nhờ người dùng tự thao tác.

## Bảng tổng quan

| ID | Tên | Phụ thuộc | ∥ | Sửa code | TT |
|---|---|---|---|---|---|
| D00 | Chuẩn bị môi trường và fixture | T00 | | Không | DONE |
| D01 | Phân tích nhanh từ AppLog (script xong; chạy lại dữ liệu sau D06) | D00, D06 | | Script | BLOCKED |
| D02 | Đếm truy cập file (script xong; chạy lại dữ liệu sau D06) | D00, D06 | | Không | BLOCKED |
| D03 | EventSource `PhotoReview-Perf` + listener CSV | D00 | ∥1 | Có | DONE |
| D04 | Gắn event vào các điểm đo | D03 | | Có | DONE |
| D05 | Tách đọc/decode + `--io-decode-split` | D04 | ∥2 | Có | DONE |
| D10 | Ghi đè số preload worker / tắt disk cache | D04 | ∥2 | Có | DONE |
| D06 | Driver kịch bản `--perf-session` | D04 | ∥2 | Có | DONE |
| D11 | Phân tích `--perf-analyze` | D04 | ∥2 | Có (CLI) | DONE |
| D07 | Chạy ma trận kịch bản | D05, D06, D10, D11 | | Không | TODO |
| D08 | ETW / PresentMon deep-dive | D07 | ∥3 | Không | TODO |
| D09 | GC và bộ nhớ | D07 | ∥3 | Không | TODO |
| D12 | Báo cáo, quyết định, đề xuất thứ tự task | D07, D08, D09, D01, D02 | | Không | TODO |
| D13 | Cập nhật plan/task refactor theo kết quả | D12 | | Docs | TODO |

**Chạy song song:** ∥1 gồm D01, D02, D03 · ∥2 gồm D05, D10, D06, D11 (sửa các file khác nhau, xem mục Files) · ∥3 gồm D08, D09.

---

### D00 — Chuẩn bị môi trường và fixture
- **TT:** DONE · **Files:** `docs/refactoring/diagnosis/env.md` (mới).
- **Làm:**
  1. Ghi cấu hình máy:
     - CPU, RAM, loại ổ và model (`Get-PhysicalDisk`), phiên bản Windows, GPU và driver, màn hình, DPI.
     - Power plan (`powercfg /getactivescheme`), trạng thái Defender (`Get-MpComputerStatus`, chỉ đọc).
     - Phiên bản .NET SDK và runtime.
  2. Xác định fixture F1–F7 (plan mục 5): số file, tổng dung lượng, kích thước pixel (lấy mẫu), số ảnh có EXIF orientation. Đường dẫn ghi dạng `<F1_ROOT>` và giữ mapping ở máy local.
  3. Kiểm tra công cụ đã có: `dotnet-counters`, `dotnet-trace`, PerfView, WPR/WPA, PresentMon, Process Monitor, RAMMap. **Liệt kê công cụ còn thiếu và hỏi người dùng** trước khi tải hoặc cài.
  4. Build Release tại commit hiện tại của `refactor/integration`. Ghi SHA.
- **Xong khi:** `env.md` đầy đủ, danh sách công cụ đã được người dùng duyệt.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `diag/D00-env` `de37808` · `env.md` + cài dotnet-counters/dotnet-trace 10.0.745401 + Process Monitor (SHA-256 80A6…1128, chữ ký Microsoft hợp lệ) · R1 (Opus): CHANGES (tên OS, đường dẫn công cụ, WPR không phải ADK); Coordinator đã sửa · fixture do Coordinator chọn từ thư mục người dùng cung cấp, mapping ở `work/diag/fixtures.local.json`: F1, F1b, F2, F3, F4 có; F5, F6, F7 phải sinh tạm.

### D01 — Phân tích nhanh từ AppLog ∥1
- **Files:** `tools/diag/parse-applog.ps1` (mới), `docs/refactoring/diagnosis/log-baseline.md` (mới).
- **Làm:**
  1. Bật `LoggingEnabled` qua Settings (đây là thay đổi config của người dùng; hỏi trước, và ghi lại để tắt sau). Xóa `%LOCALAPPDATA%\PhotoReview\logs\app.log` cũ (hoặc đổi tên) trước khi chạy.
  2. Chạy thủ công S1, S2 (khoảng 50 ảnh), S3 (giữ phím khoảng 10 s) trên F1 ở mode Preview, rồi Fast.
  3. Script đọc `app.log`, nhóm theo `token=`, tính: `start→cache-state`, `start→thumbnail-presented`, `start→preview-presented`. Đếm `ramReady=True/False`, số dòng `Preload paused for memory`, và các mốc Explorer (`query-start`, `native-read-complete`, `native order applied`). Xuất CSV và bảng P50/P95/max theo `ramReady`.
  4. Viết `log-baseline.md`, ghi rõ hạn chế: chưa có mốc render, log làm tăng độ trễ, timestamp chỉ chính xác tới ms.
- **Xong khi:** có bảng số liệu và danh sách giả thuyết H* được tăng hoặc giảm độ tin cậy.
- **Kiểm thử:** script chạy được trên file log mẫu (commit một đoạn log ≤ 200 dòng đã ẩn đường dẫn vào `tools/diag/samples/`).
- **Nhật ký:** 2026-09-16 · Sonnet 5 (nâng từ L1 vì phải điều khiển app) · `diag/D01-D02` `3bf7f01` · đã có `parse-applog.ps1`, `procmon-summary.ps1`, log mẫu, tài liệu · **BLOCKED**: `SendInput` rơi vào cửa sổ Claude Code trên desktop dùng chung (đã gõ A/B/C + Ctrl+A/Ctrl+C, không có Enter; clipboard bị ghi đè rồi xóa) · config người dùng đã khôi phục (SHA-256 khớp) · Procmon chưa chạy · Coordinator: không merge `drive-app.ps1`, thêm quy tắc 4 · chạy lại bằng `--perf-session` (D06) + Procmon, với `PHOTOREVIEW_DATA_ROOT` tạm và **không** sửa config thật nếu D06 set mode qua reflection · dữ liệu duy nhất (N=1, F1 Preview cold): cache-state 9 ms, thumbnail 253 ms, preview 905 ms, T0→T2 1.417 ms.

### D02 — Đếm truy cập file ∥1
- **Files:** `docs/refactoring/diagnosis/file-access.md` (mới), `tools/diag/procmon-filter.pmf` (tùy chọn).
- **Làm:**
  1. Process Monitor filter: `Process Name is PhotoReview.App.exe`, `Operation is CreateFile / ReadFile / QueryBasicInformationFile / QueryNetworkOpenInformationFile`.
  2. Với mỗi mode (Fast / Preview / Original) × (disk cache trống / có sẵn): mở F1 và Next đúng 10 ảnh **chậm** (cách nhau 3 s), sau đó 10 ảnh **nhanh**.
  3. Export CSV. Đếm cho mỗi ảnh nguồn: số `CreateFile`, tổng byte `ReadFile`, số lần stat. Đếm truy cập file trong `cache\*.png`, `thumbnails\*.png`, `Sessions\*.json`, `operations.jsonl`.
  4. Kết luận cho H1, H2, H3 (tần suất ghi session), H9 (có đọc PNG cache không).
- **Xong khi:** có bảng "số lần mở và byte đọc mỗi ảnh" theo mode và trạng thái cache.
- **Nhật ký:** 2026-09-16 · Sonnet 5 (nâng từ L1 vì phải điều khiển app) · `diag/D01-D02` `3bf7f01` · đã có `parse-applog.ps1`, `procmon-summary.ps1`, log mẫu, tài liệu · **BLOCKED**: `SendInput` rơi vào cửa sổ Claude Code trên desktop dùng chung (đã gõ A/B/C + Ctrl+A/Ctrl+C, không có Enter; clipboard bị ghi đè rồi xóa) · config người dùng đã khôi phục (SHA-256 khớp) · Procmon chưa chạy · Coordinator: không merge `drive-app.ps1`, thêm quy tắc 4 · chạy lại bằng `--perf-session` (D06) + Procmon, với `PHOTOREVIEW_DATA_ROOT` tạm và **không** sửa config thật nếu D06 set mode qua reflection · dữ liệu duy nhất (N=1, F1 Preview cold): cache-state 9 ms, thumbnail 253 ms, preview 905 ms, T0→T2 1.417 ms.

### D03 — EventSource và listener ∥1
- **Files:** `PhotoReview.App/Diagnostics/PhotoReviewPerf.cs` (mới), `PhotoReview.App/Diagnostics/PerfCsvListener.cs` (mới), `PhotoReview.App/App.xaml.cs` (khởi tạo listener khi có biến môi trường), `PhotoReview.Tests.Unit/PerfTraceTests.cs` (mới).
- **Làm:**
  1. `[EventSource(Name = "PhotoReview-Perf")] sealed class PhotoReviewPerf : EventSource`, singleton `Log`. Tạo method `[Event]` cho mọi event trong plan mục 6. Tham số chỉ dùng kiểu nguyên thủy (`long nav`, `int`, `double ms`, `string kind`). Path được đưa vào dưới dạng `pathId`: hash 8 ký tự hex của path chuẩn hóa. Helper `PathId(string)` chỉ tính khi `IsEnabled()`.
  2. `PerfCsvListener : EventListener`: bật provider khi `PHOTOREVIEW_PERF_TRACE` là thư mục hợp lệ. Dùng `Channel` có giới hạn 100k dòng (khi đầy thì đếm số dòng bị bỏ). Writer thread ghi CSV với cột `utcTicks, qpcTicks, thread, event, nav, pathId, a, b, c, d, text` và flush mỗi giây. Có `Dispose` khi `Exit`. Dòng đầu file ghi header, commit SHA (lấy từ `AssemblyInformationalVersion`) và danh sách biến môi trường diag đang bật.
  3. Timestamp dùng `Stopwatch.GetTimestamp()` (QPC) để tính thời gian tương đối.
  4. Test:
     - Không đặt biến môi trường thì không tạo file và `Log.IsEnabled()` là false (khi không có listener khác).
     - Đặt biến môi trường thì ghi đúng cột.
     - Channel đầy thì không block.
- **Không làm:** gắn event vào code nghiệp vụ (thuộc D04).
- **Xong khi:** test đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-16 · Sonnet 5 · `diag/D03-perf-eventsource` `1c9b8e6` · 24 event (id 1–24, cố định) · CSV map: nav/gen→nav, pathId, số→a..d theo thứ tự, chuỗi→text · `WriteEvent(params object[])` có guard `IsEnabled()` · xUnit 195/195 × 5 lần · chạy app không đặt biến thì không tạo file · review (Opus): APPROVE · đã merge.

### D04 — Gắn event vào các điểm đo
- **Files:** `PhotoReview.App/MainWindow.xaml.cs`, `PreviewImageService.cs`, `ThumbnailCache.cs`, `PreloadScheduler.cs`, `FileHashService.cs`, `SessionStore.cs`, `ExplorerOrderService.cs` (chỉ mốc tổng), `App.xaml.cs` (`Dispatcher.Hooks`), `PhotoReview.Tests.Unit/PerfTraceTests.cs` (bổ sung).
- **Ghi chú (2026-09-16):** Coordinator chỉ dẫn không sửa `ExplorerOrderService.cs` (mốc Explorer lấy ở `LoadFolderAsync`), `SessionStore.cs` (đo ở call site) và `FileHashService.cs` (đo ở call site).
- **Làm** (mọi đoạn đo được bọc trong `if (PhotoReviewPerf.Log.IsEnabled())`; không đổi thứ tự lệnh hay kết quả):
  1. `Window_KeyDown`, dòng đầu: `KeyInput(key, Environment.TickCount - e.Timestamp)`. **Xác minh** `e.Timestamp` và `Environment.TickCount` cùng gốc; nếu không, dùng `GetMessageTime` hoặc bỏ số liệu này và ghi nhật ký.
  2. `ShowImageAsync`:
     - Ghi `ShowStart` sau khi có token.
     - `Stat` bao quanh `TryGetCurrentFileInfo` và `GetCurrentCacheKey`.
     - `Lookup(ramHit | inflight | miss)`, trong đó `inflight` lấy từ `HasInflightPreview`.
     - `ThumbStart/End`.
     - `Assign` bao quanh `MainImage.Source = image` (kèm pixel W/H).
     - Đăng ký `CompositionTarget.Rendering` một lần để ghi `Rendered(msSinceAssign)` và `Presented(kind)`.
     - `PostStart/End` cho compare, hash, dims, session.
  3. `PreviewImageService.DecodeAndCacheAsync`:
     - `DiskCacheRead(ms, bytes)` khi đọc từ cache.
     - `Decode(ms, targetWidth, downscaled, fallback)`. `fallback=true` khi `DecodeWithFallback` phải decode full-res.
     - `Verify(ms)`.
     - `JoinStart/End` trong `GetPreviewAsync` khi caller không phải bên thắng (`!ReferenceEquals`).
     - `nav` lấy từ `AsyncLocal<long>` do `ShowImageAsync` đặt (preload đặt `nav=-1`).
  4. `PreloadScheduler.PreloadOneAsync`: `PreloadItem(worker slot, queueWaitMs, kind = hit|decoded|skipped, ms)`. `PreloadPaused(loadPct, availMB)` ở nhánh memory pause. `PreloadCancel` trong `Cancel()`.
  5. `SessionStore.Save`: `PostStart/End(session)` kèm ms. `FileHashService`: `PostStart/End(hash)`.
  6. `LoadFolderAsync`: `Folder(phase)` cho T0 (bắt đầu), T1 (scan xong), T2 (ảnh đầu tiên được present, lấy từ event `Presented` đầu tiên sau T0), Explorer applied (T3), kèm `interaction ignored` nếu có.
  7. `App.xaml.cs`: khi bật trace, `Dispatcher.Hooks.OperationStarted/Completed` đo thời gian và ghi `DispatcherLongOp` nếu > 16 ms (tên lấy từ `Operation.Priority` + delegate method name).
  8. Test: dùng `EventListener` trong test, gọi `PreviewImageService.GetPreviewAsync` trên ảnh tạm, assert có `Decode` và `Verify`.
- **Không làm:** thay đổi logic. Không thêm `await` mới vào đường nóng.
- **Kiểm thử:** VERIFY. So sánh `--ui-next-probe` khi có và không có biến môi trường (chênh lệch P50 ≤ 5%; ghi số).
- **Xong khi:** chạy app với biến môi trường, CSV có đủ event cho S2 thủ công.
- **Nhật ký:** 2026-09-16 · Opus 5 · `diag/D04-instrument` `565dd67` · điểm đo: KeyInput, Folder (9 phase), ShowStart/Stat/Lookup/Thumb*/Assign/Rendered/Presented/Post*(preloadKick, compare, hash, dims, session), Join*/DiskCacheRead/Decode/Verify, ThumbEnd(ram/disk/decode), PreloadItem/Paused/Cancel, DispatcherLongOp, DiagMode · input timestamp: `GetMessageTime` cùng gốc với TickCount (độ phân giải ~15,6 ms) · overhead ~24 µs/lần Next (~0,5%; probe chỉ báo ms nguyên) · xUnit 199/199 × 10 · phát hiện `--ui-next-probe` hỏng từ trước (chuyển cho D06) · **R3: reviewer Opus bị dừng vì giới hạn chi tiêu (HTTP 429)**, Coordinator (Opus) tự review toàn bộ diff: APPROVE · VERIFY đạt.

### D05 — Tách đọc/decode ∥2
- **Files:** `PhotoReview.App/Diagnostics/DiagOptions.cs` (mới, đọc biến môi trường một lần), `PhotoReview.App/PreviewImageService.cs` (**chỉ** nhánh `DecodeSource` khi `DiagOptions.PreRead`), `PhotoReview.Tests/IoDecodeSplit.cs` (mới), `PhotoReview.Tests/Program.cs` (thêm dispatch), `docs/refactoring/diagnosis/io-decode-split.md` (mới).
- **Làm:**
  1. `DiagOptions`: `PreRead`, `PreloadWorkers?`, `DisableDiskCache` (hai trường sau do D10 dùng; D05 chỉ tạo khung). Khi có biến nào được bật, log `AppLog.Error("DIAG MODE ...")` (ép ghi như `LogStartupErrorForced`) và thêm " · DIAG" vào title.
  2. `DecodeSource` khi `PreRead`: `File.ReadAllBytes`-tương đương với `FileStream` (ReadWrite|Delete, SequentialScan), ghi `SourceRead(ms, bytes)`, sau đó decode từ `MemoryStream` và ghi `Decode`.
  3. CLI `--io-decode-split <folder> <out> [widths=0,1920,2560,3840] [max=60]`. Với mỗi file:
     - `readMs`: đọc toàn bộ vào RAM.
     - `decodeFromMemMs[w]`.
     - `decodeFromFileMs[w]`.
     - `headerOnlyMs`: `BitmapDecoder` với `DelayCreation`, giống `GetOriginalDimensionsAsync`.
     - `pngEncodeMs` và `pngDecodeMs` của preview 2560 (mô phỏng disk cache).
     - Mỗi phép đo chạy 3 lần; ghi lần đầu (cold) riêng.
     - Xuất CSV và Markdown: đường cong decode theo width (H8), `pngDecode` so với `read + decode` (H9), tỷ lệ read/decode.
  4. Chạy trên F1, F2, F3; nếu có thì thêm ổ chậm (S10).
- **Xong khi:** `io-decode-split.md` có bảng và kết luận H8, H9, và tỷ lệ I/O/decode. Khi không có biến môi trường, VERIFY đạt.
- **Nhật ký:** 2026-09-17 · Sonnet 5 (bị gián đoạn vì giới hạn, đã tiếp tục) · `diag/D05-io-decode` `0e78292` · `DiagOptions` + PREREAD + `--io-decode-split` · warm P50 (ms), theo thứ tự read / decode@0 / @1920 / @2560 / @3840 / pngDecode:
  - F1 (24 MP nén mạnh): 0,2 / 7,6 / 29,3 / 48,7 / 89,9 / 102
  - F1b (27 MP): 4,4 / 242 / 227 / 283 / 340 / 107
  - F2 (48 MP): 2,8 / 1359 / 1354 / 1328 / 1462 / 111
  - F3 (PNG): 0,4 / 16,5 / 37,6 / – / – / 70
  - **Kết luận sơ bộ:** decode chiếm 98–100%. I/O không đáng kể **khi file đã nằm trong cache của OS** (cần đo lại khi cache OS lạnh). H8 đúng một phần: với 48 MP, hạ width không giảm thời gian. H9 tùy ảnh: PNG disk cache lỗ với ảnh rẻ, lãi 2,7–12× với ảnh đắt.
  - Review (Opus): APPROVE; Coordinator sửa `--perf-analyze` để trừ `t_read` khỏi `Decode` khi PREREAD; giải quyết conflict `Program.cs` với D11 · 245/245 test.

### D10 — Preload worker / tắt disk cache ∥2
- **Files:** `PhotoReview.App/PreloadScheduler.cs` (số worker lấy từ `DiagOptions.PreloadWorkers ?? AppConstants.PreloadWorkerCount` cho **cả** `SemaphoreSlim` **lẫn** hằng `workers` trong vòng lặp), `PhotoReview.App/PreviewImageService.cs` (bỏ qua đọc và persist disk cache khi `DisableDiskCache`), `PhotoReview.Tests.Unit/PerfTraceTests.cs` (test override), `docs/refactoring/diagnosis/contention.md` (mới; kết quả điền ở D07).
- **Phối hợp:** D05 cũng sửa `PreviewImageService.cs` nhưng ở `DecodeSource`, còn D10 sửa ở `DecodeAndCacheAsync`/`PersistToDiskCache`. Merge D05 trước, D10 rebase sau.
- **Làm:**
  1. `PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0` nghĩa là **không preload** (`PreloadAroundAsync` trả về ngay).
  2. Test: override 2 thì tối đa 2 decode chạy đồng thời (dùng decoder chậm giả, hoặc kiểm tra qua `CurrentCount` của semaphore).
- **Xong khi:** test đạt, VERIFY đạt khi không có biến môi trường.
- **Nhật ký:** 2026-09-17 · Sonnet 5 · `diag/D10-overrides` `91f1f27` · `_workerCount` (tham số → DIAG → mặc định), 0 = không preload; `_disableDiskCache` bỏ đọc và ghi disk cache · 5 test, 250/250 × 11 lần · review (Opus): APPROVE · **Phát hiện lỗi:** `RunPreloadSchedulerAsync` gọi `Dispatcher.Yield()`; khi không có Dispatcher (CLI benchmark, hoặc continuation sau `ConfigureAwait(false)` trong `BenchmarkEngine`), lệnh này ném exception và exception bị nuốt, nên preload dừng sau lô đầu tiên. Đã chuyển cho T32 và D12.

### D06 — Driver kịch bản `--perf-session` ∥2
- **Files:** `PhotoReview.Tests/PerfSession.cs` (mới), `PhotoReview.Tests/LocalUiNextProbe.cs` (sửa lỗi có sẵn), `PhotoReview.Tests/Program.cs` (dispatch), `tools/diag/scenarios/*.json` (mới), `tools/diag/run-matrix.ps1` (mới).
- **Bổ sung (từ D04):** `--ui-next-probe` hiện hỏng từ trước D04:
  1. Pack URI không tìm thấy `assets/photoreview.ico` khi `Application` được tạo trong exe test.
  2. `GetMethod("TryGetCachedPreview")` bị mơ hồ vì có 2 overload.
  3. Probe không đi qua `App`, nên `PHOTOREVIEW_PERF_TRACE` không bật listener.
  D06 phải: dùng chung khung STA đã sửa cả 3 lỗi (chỉ định overload bằng kiểu tham số; xử lý icon, ví dụ tạo `Application` với `ResourceAssembly` = assembly của App, hoặc bỏ qua icon trong test host; tự gọi `PerfCsvListener.TryStartFromEnvironment()`); và sửa luôn `LocalUiNextProbe.cs` (thêm file này vào Files).
- **Làm:**
  1. `--perf-session <scenario.json> <folder> <outDir> [--mode Fast|Preview|Original]`:
     - Dùng khung STA giống `LocalUiNextProbe`, nhưng cửa sổ ở trạng thái **Normal**, 1920×1080, đặt ở (0,0) màn hình chính, `ShowActivated=true`.
     - Đặt `PHOTOREVIEW_PERF_TRACE=<outDir>` **trước** khi tạo `Application`.
     - `PHOTOREVIEW_DATA_ROOT` là thư mục tạm, để không đụng session hay journal thật.
     - Mode được set qua reflection vào `_settings.LoadingMode` sau khi tạo window. **Không** ghi `config.json` thật; ghi rõ trong nhật ký nếu phải dùng cách khác.
  2. Gửi phím bằng routed event thật: `new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent }` rồi `window.RaiseEvent(...)`. Cách này đi qua `Window_KeyDown` nhưng **không** đi qua hàng đợi input của OS, nên `t_input` chỉ phản ánh độ trễ dispatcher. **Cấm** dùng `SendInput`, `SendKeys`, `keybd_event`, `AttachThreadInput` hay `SetForegroundWindow`, vì chúng đi vào desktop dùng chung (xem quy tắc 4). Độ trễ input thật chỉ được đo khi người dùng tự bấm phím.
  3. Schema kịch bản JSON: `{ "name", "steps": [ {"open": "folder|file:<index>"}, {"key": "Right", "repeat": 100, "intervalMs": 1500}, {"waitMs": 3000}, {"waitIdle": true}, {"zoom": [1,2,4]}, {"pan": {"dx": 400, "dy": 0, "steps": 30}}, {"action": "Enter"} ] }`. Tạo file cho S1–S9 (plan mục 5). S8 chạy trên **bản sao** fixture trong thư mục tạm, vì Move làm đổi file.
  4. Sau mỗi kịch bản ghi `metrics.json` (`ReviewMetrics.Snapshot`), `process.json` (working set peak, private bytes, GC count gen0/1/2, `GC.GetTotalPauseDuration`, CPU time).
  5. `run-matrix.ps1`: tham số danh sách kịch bản × mode × điều kiện (`cold-app`, `cold-diskcache`, `warm`), số lần lặp, fixture root. Xóa disk cache khi cần. **Không** tự làm trống standby list; thay vào đó dừng lại và hỏi người dùng khi điều kiện `cold-os` được yêu cầu.
- **Xong khi:** chạy S2 (10 phím) ra CSV có đủ event và `metrics.json`.
- **Nhật ký:** 2026-09-16/17 · Opus 5 · `diag/D06-perf-session` `88da3af` · phím gửi bằng `PreviewKeyDown`/`KeyDown` routed event trong process (không gửi input OS, không Activate) · `WpfTestHost`: icon set qua reflection `_resourceAssembly`, chặn đọc/ghi window-placement thật, dispatcher hooks lấy qua reflection · mode set trong bộ nhớ, không ghi config · thay `_settings.Actions` trong bộ nhớ (config thật có Enter = Move vào F4), cấm các phím nguy hiểm, kiểm đích trước mỗi action · S2/S6/S7/S8 chạy thử đạt; key→present sơ bộ 3,9–15,2 ms (warm) · folder nguồn không đổi · `--ui-next-probe` đã sửa, PASS (p95 dao động ~1 s giữa các lượt, chưa rõ nguyên nhân) · **R3: Coordinator (Opus) tự review** vì agent Opus bị giới hạn chi tiêu: APPROVE, kèm sửa: bỏ tên thư mục nguồn khỏi tài liệu và script · VERIFY đạt 199/199.

### D11 — `--perf-analyze` ∥2
- **Files:** `PhotoReview.Tests/PerfAnalyze.cs` (mới), `PhotoReview.Tests/Program.cs` (dispatch), `PhotoReview.Tests.Unit/PerfAnalyzeTests.cs` (mới, dùng CSV mẫu tự tạo), `tools/diag/samples/perf-sample.csv` (mới, nhỏ).
- **Làm:**
  1. Đọc mọi CSV trong một thư mục run. Ghép event theo `nav` thành bản ghi điều hướng gồm các cột `t_input`, `t_stat`, `t_lookup`, `t_thumb`, `t_join`, `t_disk`, `t_open`, `t_read`, `t_decode`, `t_verify`, `t_assign`, `t_render`, `t_post_*`, `firstVisual`, `finalVisual`, `kind`.
  2. Bản ghi thiếu mốc thì đánh dấu `incomplete` (ví dụ bị thay token) và **không** tính vào phân vị, nhưng vẫn đếm.
  3. Tổng hợp theo (scenario, mode, cond): count, P50/P95/max của `finalVisual` và `firstVisual`, tỷ lệ theo `kind`, tỷ trọng giai đoạn ở nhóm 10% chậm nhất, số lần mở file mỗi ảnh (nếu có event `SourceOpen`), `DispatcherLongOp` > 16 ms, preload (queue wait, paused count), GC.
  4. Áp các quy tắc R-IO, R-DEC, R-UI, R-PRE, R-CONT, R-THREAD, R-GC, R-DISK, R-FOLDER (plan mục 8), với ngưỡng đọc từ `tools/diag/rules.json` để hiệu chỉnh mà không cần build lại.
  5. Xuất `summary.md` (bảng) và `summary.json`.
- **Kiểm thử:** unit test trên CSV mẫu kiểm tra phân vị, tỷ trọng và từng quy tắc (mỗi quy tắc có ít nhất một ca đúng và một ca sai).
- **Xong khi:** test đạt, chạy được trên output của D06.
- **Nhật ký:** 2026-09-17 · Sonnet 5 (bị gián đoạn vì giới hạn sử dụng, đã tiếp tục) · `diag/D11-perf-analyze` `a333722` · 6 file `PerfAnalyze*.cs`, `rules.json`, CSV mẫu, 32 test (231/231 × 5) · test project tham chiếu `PhotoReview.Tests` bằng ProjectReference · review (Opus): CHANGES, vì định dạng output D06 và D11 không khớp (`folderAlias`, `env`, điều kiện nằm trong tên thư mục, GC dạng ms, thư mục warmup) và `t_post` bị tính vào tỷ trọng key→present; Coordinator sửa ở `8c54af4` · **chạy thử cả chuỗi** `run-matrix` (S3, F1, Preview, warm + cold-app) → `--perf-analyze`: 201 nav/lượt, hit 99,5%, final P50 5,2 ms / P95 7,1–8,4 ms / max ~340 ms; nhóm 10% chậm nhất bị `t_render` chiếm ~92% (dấu hiệu sớm cho R-UI, cần D07/D08 xác nhận; có thể do cửa sổ không ở foreground).

### D07 — Chạy ma trận
- **Files:** `docs/refactoring/diagnosis/runs/<date>/**/summary.*` (chỉ bản tóm tắt), `docs/refactoring/diagnosis/contention.md` (điền kết quả).
- **Làm:**
  1. **Baseline không trace:** S2 Preview warm, 3 lần, chỉ `--ui-next-probe` và đồng hồ ngoài (ước lượng mức ảnh hưởng của trace).
  2. **Ma trận chính:** S1–S9 × {Fast, Preview, Original} (S6 và S7 chỉ chạy Preview) × {cold-app + cold-diskcache, warm} × 3 lần, trên F1 (S6 dùng F2, S7 dùng F6, S9 dùng F4 và F5).
  3. **Cold OS cache:** S1 và S2 Preview, sau khi **người dùng** reboot hoặc làm trống standby list. Ít nhất 2 lần.
  4. **Tranh chấp tài nguyên (D10):** S3 Preview với `PRELOAD_WORKERS` = 0, 2, 4, 8, 12, và S2 với 0 và 8.
  5. **Disk cache:** S2 Preview warm-diskcache (RAM trống, disk cache đầy) so với `DISABLE_DISKCACHE=1`.
  6. **PreRead:** S2 và S3 Preview với `PREREAD=1` (tách read/decode, ước lượng lợi ích R-4).
  7. **Ổ chậm:** S2 trên S10 nếu có.
  8. Chạy `--perf-analyze` cho từng run. Ghi thời gian và điều kiện bất thường (ví dụ Windows Update đang chạy).
- **Xong khi:** mọi ô có `summary.md`. Các ô không chạy được thì có lý do.
- **Nhật ký:** —

### D08 — ETW / PresentMon ∥3
- **Files:** `docs/refactoring/diagnosis/etw-findings.md` (mới), `tools/diag/wpr/PhotoReview.wprp` (mới, profile WPR thêm provider `PhotoReview-Perf`).
- **Làm:**
  1. WPR: CPU sampling + File I/O + Disk I/O + GPU + provider `PhotoReview-Perf`. Ghi 30 s cho S3 Preview (miss nhiều) và S6 (zoom F2). Thêm một lượt S2 cold-os nếu người dùng đồng ý reboot.
  2. WPA:
     - Top stack CPU trong khoảng các `Decode` chậm: `WindowsCodecs.dll` (jpeg decode, scaler), `PresentationCore`/`wpfgfx` (render), `clr` (GC).
     - Thời gian I/O theo file.
     - Có `MsMpEng` trong đường mở file hay không (H14).
     - GPU queue và present.
  3. PresentMon trong S3 và S6: frame time P50/P95/max, số frame bị bỏ. So `Rendered` (CSV) với present thật để hiệu chỉnh `t_render`.
  4. Nếu thiếu WPR hay PresentMon và người dùng không đồng ý cài: dùng `dotnet-trace collect --providers PhotoReview-Perf,Microsoft-DotNETCore-SampleProfiler` và ghi rõ các hạn chế.
- **Xong khi:** `etw-findings.md` có ảnh chụp hoặc bảng đã ẩn path, và kết luận H7, H8, H14.
- **Nhật ký:** —

### D09 — GC và bộ nhớ ∥3
- **Files:** `docs/refactoring/diagnosis/gc-memory.md` (mới).
- **Làm:**
  1. `dotnet-counters collect --process-id <pid> --counters System.Runtime --format csv` trong S2, S3, S9 (F4 và F5).
  2. Ghi: % time in GC, số lần gen2 GC, LOH size, working set, allocation rate. Ghi thêm memory load của hệ thống (lấy từ event `PreloadPaused` hoặc `Get-Counter '\Memory\% Committed Bytes In Use'`).
  3. Tính "decoded bytes dự kiến" cho F5 (w×h×4 ở target width) và so với capacity 16 GB và ngưỡng full-folder (H6). So miss rate theo thời gian trong S9 (H5, H10).
- **Xong khi:** có kết luận H5, H6, H10, H11.
- **Nhật ký:** —

### D12 — Báo cáo và quyết định
- **Files:** `docs/refactoring/diagnosis/REPORT.md` (mới).
- **Lưu ý (từ D10):** số liệu preload từ `--benchmark*` / `BenchmarkWindow` trước khi sửa T32 có thể sai, vì preload dừng sau lô đầu khi không chạy trên UI dispatcher. Chỉ dùng số liệu preload từ `--perf-session` (có Dispatcher).
- **Làm:**
  1. **Tóm tắt 1 trang:** nút thắt chính và phụ, kèm số liệu P95 của S2 và S3 theo từng mode.
  2. **Bảng tỷ trọng giai đoạn** (10% chậm nhất) theo kịch bản và mode.
  3. **Giả thuyết:** trạng thái H1–H15 (`ĐÚNG` / `SAI` / `CHƯA RÕ`) kèm bằng chứng (link tới file con).
  4. **Quy tắc:** liệt kê quy tắc nào của plan mục 8 kích hoạt, và ngưỡng đã hiệu chỉnh (nếu có).
  5. **Đề xuất thứ tự:**
     - Task refactor nào đẩy lên hoặc hạ xuống.
     - Task mới cần thêm: ưu tiên ảnh đang xem, preload theo hướng, định dạng disk cache, chính sách target width…
     - Khuyến nghị cho Q5 (R-4 mặc định), Q7/Q8 (C3), và dữ liệu đầu vào cho T71 (C1).
  6. **Mục tiêu hiệu năng** (plan mục 8): **hỏi người dùng xác nhận** hoặc chỉnh.
- **Xong khi:** người dùng đọc và xác nhận các đề xuất.
- **Nhật ký:** —

### D13 — Cập nhật plan refactor theo kết quả
- **Files:** `docs/refactoring/REFACTOR-PLAN.md` (mục 3.2, 6, 11), `docs/refactoring/REFACTOR-TASKS.md` (bảng, phụ thuộc, task mới), `task_on_progress.md`.
- **Làm:** áp dụng các đề xuất **đã được người dùng xác nhận** ở D12. Task mới đặt ID theo nhóm (ví dụ T67, T68…) và theo đúng mẫu. Kiểm tra lại phụ thuộc bằng script awk trong lịch sử commit plan. Nếu dữ liệu cho thấy C3 không đáng làm (decode < 20% P95), đánh dấu T82–T87 là `BLOCKED: chờ quyết định` thay vì xóa.
- **Xong khi:** plan và task đã cập nhật và được commit.
- **Nhật ký:** —
