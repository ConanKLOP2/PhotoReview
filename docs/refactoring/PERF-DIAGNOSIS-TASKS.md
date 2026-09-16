# PhotoReview — Task chẩn đoán hiệu năng (D00–D13)

- **Plan:** [`PERF-DIAGNOSIS-PLAN.md`](PERF-DIAGNOSIS-PLAN.md) · **Cập nhật:** 2026-09-16
- **Quy trình chung:** giống [`REFACTOR-TASKS.md`](REFACTOR-TASKS.md) mục 0 (một task mỗi phiên, chỉ sửa file trong **Files**, có VERIFY, ghi nhật ký theo mẫu).
- **Branch:** `diag/<ID>-<slug>`, tách từ `refactor/integration` (sau T00).
- **Đường dẫn:** các task này chạy **trước** T24, nên dùng layout cũ (`PhotoReview.App/`, `PhotoReview.Tests/`, `PhotoReview.Tests.Unit/`).
- **Quy tắc riêng:**
  1. Không tải hoặc cài công cụ khi chưa hỏi người dùng. Không đổi cài đặt hệ thống hay bảo mật. Các bước cần admin (RAMMap, reboot) do **người dùng** làm.
  2. Khi không có biến môi trường chẩn đoán, **hành vi app không được đổi**. Mọi test hiện có phải đạt.
  3. Không commit ảnh thật, đường dẫn cá nhân, hay file dữ liệu thô > 5 MB (chỉ commit bản tóm tắt).

## Bảng tổng quan

| ID | Tên | Phụ thuộc | ∥ | Sửa code | TT |
|---|---|---|---|---|---|
| D00 | Chuẩn bị môi trường và fixture | T00 | | Không | TODO |
| D01 | Phân tích nhanh từ AppLog | D00 | ∥1 | Script | TODO |
| D02 | Đếm truy cập file (Process Monitor) | D00 | ∥1 | Không | TODO |
| D03 | EventSource `PhotoReview-Perf` + listener CSV | D00 | ∥1 | Có | TODO |
| D04 | Gắn event vào các điểm đo | D03 | | Có | TODO |
| D05 | Tách đọc/decode + `--io-decode-split` | D04 | ∥2 | Có | TODO |
| D10 | Ghi đè số preload worker / tắt disk cache | D04 | ∥2 | Có | TODO |
| D06 | Driver kịch bản `--perf-session` | D04 | ∥2 | Có | TODO |
| D11 | Phân tích `--perf-analyze` | D04 | ∥2 | Có (CLI) | TODO |
| D07 | Chạy ma trận kịch bản | D05, D06, D10, D11 | | Không | TODO |
| D08 | ETW / PresentMon deep-dive | D07 | ∥3 | Không | TODO |
| D09 | GC và bộ nhớ | D07 | ∥3 | Không | TODO |
| D12 | Báo cáo, quyết định, đề xuất thứ tự task | D07, D08, D09, D01, D02 | | Không | TODO |
| D13 | Cập nhật plan/task refactor theo kết quả | D12 | | Docs | TODO |

**Chạy song song:** ∥1 gồm D01, D02, D03 · ∥2 gồm D05, D10, D06, D11 (sửa các file khác nhau, xem mục Files) · ∥3 gồm D08, D09.

---

### D00 — Chuẩn bị môi trường và fixture
- **TT:** TODO · **Files:** `docs/refactoring/diagnosis/env.md` (mới).
- **Làm:**
  1. Ghi cấu hình máy:
     - CPU, RAM, loại ổ và model (`Get-PhysicalDisk`), phiên bản Windows, GPU và driver, màn hình, DPI.
     - Power plan (`powercfg /getactivescheme`), trạng thái Defender (`Get-MpComputerStatus`, chỉ đọc).
     - Phiên bản .NET SDK và runtime.
  2. Xác định fixture F1–F7 (plan mục 5): số file, tổng dung lượng, kích thước pixel (lấy mẫu), số ảnh có EXIF orientation. Đường dẫn ghi dạng `<F1_ROOT>` và giữ mapping ở máy local.
  3. Kiểm tra công cụ đã có: `dotnet-counters`, `dotnet-trace`, PerfView, WPR/WPA, PresentMon, Process Monitor, RAMMap. **Liệt kê công cụ còn thiếu và hỏi người dùng** trước khi tải hoặc cài.
  4. Build Release tại commit hiện tại của `refactor/integration`. Ghi SHA.
- **Xong khi:** `env.md` đầy đủ, danh sách công cụ đã được người dùng duyệt.
- **Nhật ký:** —

### D01 — Phân tích nhanh từ AppLog ∥1
- **Files:** `tools/diag/parse-applog.ps1` (mới), `docs/refactoring/diagnosis/log-baseline.md` (mới).
- **Làm:**
  1. Bật `LoggingEnabled` qua Settings (đây là thay đổi config của người dùng; hỏi trước, và ghi lại để tắt sau). Xóa `%LOCALAPPDATA%\PhotoReview\logs\app.log` cũ (hoặc đổi tên) trước khi chạy.
  2. Chạy thủ công S1, S2 (khoảng 50 ảnh), S3 (giữ phím khoảng 10 s) trên F1 ở mode Preview, rồi Fast.
  3. Script đọc `app.log`, nhóm theo `token=`, tính: `start→cache-state`, `start→thumbnail-presented`, `start→preview-presented`. Đếm `ramReady=True/False`, số dòng `Preload paused for memory`, và các mốc Explorer (`query-start`, `native-read-complete`, `native order applied`). Xuất CSV và bảng P50/P95/max theo `ramReady`.
  4. Viết `log-baseline.md`, ghi rõ hạn chế: chưa có mốc render, log làm tăng độ trễ, timestamp chỉ chính xác tới ms.
- **Xong khi:** có bảng số liệu và danh sách giả thuyết H* được tăng hoặc giảm độ tin cậy.
- **Kiểm thử:** script chạy được trên file log mẫu (commit một đoạn log ≤ 200 dòng đã ẩn đường dẫn vào `tools/diag/samples/`).
- **Nhật ký:** —

### D02 — Đếm truy cập file ∥1
- **Files:** `docs/refactoring/diagnosis/file-access.md` (mới), `tools/diag/procmon-filter.pmf` (tùy chọn).
- **Làm:**
  1. Process Monitor filter: `Process Name is PhotoReview.App.exe`, `Operation is CreateFile / ReadFile / QueryBasicInformationFile / QueryNetworkOpenInformationFile`.
  2. Với mỗi mode (Fast / Preview / Original) × (disk cache trống / có sẵn): mở F1 và Next đúng 10 ảnh **chậm** (cách nhau 3 s), sau đó 10 ảnh **nhanh**.
  3. Export CSV. Đếm cho mỗi ảnh nguồn: số `CreateFile`, tổng byte `ReadFile`, số lần stat. Đếm truy cập file trong `cache\*.png`, `thumbnails\*.png`, `Sessions\*.json`, `operations.jsonl`.
  4. Kết luận cho H1, H2, H3 (tần suất ghi session), H9 (có đọc PNG cache không).
- **Xong khi:** có bảng "số lần mở và byte đọc mỗi ảnh" theo mode và trạng thái cache.
- **Nhật ký:** —

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
- **Nhật ký:** —

### D04 — Gắn event vào các điểm đo
- **Files:** `PhotoReview.App/MainWindow.xaml.cs`, `PreviewImageService.cs`, `ThumbnailCache.cs`, `PreloadScheduler.cs`, `FileHashService.cs`, `SessionStore.cs`, `ExplorerOrderService.cs` (chỉ mốc tổng), `App.xaml.cs` (`Dispatcher.Hooks`), `PhotoReview.Tests.Unit/PerfTraceTests.cs` (bổ sung).
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
- **Nhật ký:** —

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
- **Nhật ký:** —

### D10 — Preload worker / tắt disk cache ∥2
- **Files:** `PhotoReview.App/PreloadScheduler.cs` (số worker lấy từ `DiagOptions.PreloadWorkers ?? AppConstants.PreloadWorkerCount` cho **cả** `SemaphoreSlim` **lẫn** hằng `workers` trong vòng lặp), `PhotoReview.App/PreviewImageService.cs` (bỏ qua đọc và persist disk cache khi `DisableDiskCache`), `PhotoReview.Tests.Unit/PerfTraceTests.cs` (test override), `docs/refactoring/diagnosis/contention.md` (mới; kết quả điền ở D07).
- **Phối hợp:** D05 cũng sửa `PreviewImageService.cs` nhưng ở `DecodeSource`, còn D10 sửa ở `DecodeAndCacheAsync`/`PersistToDiskCache`. Merge D05 trước, D10 rebase sau.
- **Làm:**
  1. `PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0` nghĩa là **không preload** (`PreloadAroundAsync` trả về ngay).
  2. Test: override 2 thì tối đa 2 decode chạy đồng thời (dùng decoder chậm giả, hoặc kiểm tra qua `CurrentCount` của semaphore).
- **Xong khi:** test đạt, VERIFY đạt khi không có biến môi trường.
- **Nhật ký:** —

### D06 — Driver kịch bản `--perf-session` ∥2
- **Files:** `PhotoReview.Tests/PerfSession.cs` (mới), `PhotoReview.Tests/Program.cs` (dispatch), `tools/diag/scenarios/*.json` (mới), `tools/diag/run-matrix.ps1` (mới).
- **Làm:**
  1. `--perf-session <scenario.json> <folder> <outDir> [--mode Fast|Preview|Original]`:
     - Dùng khung STA giống `LocalUiNextProbe`, nhưng cửa sổ ở trạng thái **Normal**, 1920×1080, đặt ở (0,0) màn hình chính, `ShowActivated=true`.
     - Đặt `PHOTOREVIEW_PERF_TRACE=<outDir>` **trước** khi tạo `Application`.
     - `PHOTOREVIEW_DATA_ROOT` là thư mục tạm, để không đụng session hay journal thật.
     - Mode được set qua reflection vào `_settings.LoadingMode` sau khi tạo window. **Không** ghi `config.json` thật; ghi rõ trong nhật ký nếu phải dùng cách khác.
  2. Gửi phím bằng routed event thật: `new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent }` rồi `window.RaiseEvent(...)`. Cách này đi qua `Window_KeyDown` nhưng **không** đi qua hàng đợi input của OS, nên `t_input` chỉ phản ánh độ trễ dispatcher. Nếu cần đo input thật, có thêm tùy chọn `--sendinput` dùng `SendInput` Win32 (cửa sổ phải đang foreground).
  3. Schema kịch bản JSON: `{ "name", "steps": [ {"open": "folder|file:<index>"}, {"key": "Right", "repeat": 100, "intervalMs": 1500}, {"waitMs": 3000}, {"waitIdle": true}, {"zoom": [1,2,4]}, {"pan": {"dx": 400, "dy": 0, "steps": 30}}, {"action": "Enter"} ] }`. Tạo file cho S1–S9 (plan mục 5). S8 chạy trên **bản sao** fixture trong thư mục tạm, vì Move làm đổi file.
  4. Sau mỗi kịch bản ghi `metrics.json` (`ReviewMetrics.Snapshot`), `process.json` (working set peak, private bytes, GC count gen0/1/2, `GC.GetTotalPauseDuration`, CPU time).
  5. `run-matrix.ps1`: tham số danh sách kịch bản × mode × điều kiện (`cold-app`, `cold-diskcache`, `warm`), số lần lặp, fixture root. Xóa disk cache khi cần. **Không** tự làm trống standby list; thay vào đó dừng lại và hỏi người dùng khi điều kiện `cold-os` được yêu cầu.
- **Xong khi:** chạy S2 (10 phím) ra CSV có đủ event và `metrics.json`.
- **Nhật ký:** —

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
- **Nhật ký:** —

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
