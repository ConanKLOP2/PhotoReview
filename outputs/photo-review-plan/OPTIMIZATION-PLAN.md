# PhotoReview — Optimization & Multi-Agent Execution Plan

Ngày lập: 2026-09-15
Nguồn: phân tích toàn bộ codebase (`PhotoReview.App`, `PhotoReview.Tests`, `tools/`, `outputs/photo-review-plan/*`) đối chiếu với `PROGRESS.md`/`TASKS.md`/`ERROR-HISTORY.md` hiện có.

Mục tiêu tài liệu: liệt kê **lỗi tiềm ẩn**, **điểm tối ưu hiệu năng**, **vấn đề vận hành/kiến trúc**, và chia thành các **work package** có thể giao cho nhiều agent (hoặc nhiều dev) chạy song song, kèm tiêu chí hoàn thành (DoD) và thứ tự phụ thuộc.

---

## 0. Tóm tắt phát hiện chính (ranked theo impact)

| # | Phát hiện | Loại | File:line | Impact |
|---|---|---|---|---|
| 1 | `BenchmarkImageExecutor.cs` là dead code — không được gọi ở đâu cả, trong khi benchmark thật (CLI + UI) chỉ đọc file thô (`File.ReadAllBytesAsync`/`FileStream`), **không decode `BitmapImage`** → benchmark không đo đúng cái nó tuyên bố đo | Correctness của tooling | `BenchmarkImageExecutor.cs` (toàn bộ), `PhotoReview.Tests/Program.cs:18-37`, `BenchmarkWindow.cs:49-58` | Cao — mọi số liệu benchmark hiện tại (P50/P95/P99) không phản ánh chi phí decode/cache thật |
| 2 | Cache ảnh preview đã decode ghi ra đĩa **không có eviction/quota** (`GetDiskCachePath`, `MainWindow.xaml.cs:412-433,531-535`) | Bug/leak | `MainWindow.xaml.cs` | Cao — đĩa phình vô hạn theo thời gian sử dụng |
| 3 | `BenchmarkEngine.RunAsync`: `metrics ??= result.Metrics` chỉ giữ lại metrics của iteration đầu tiên | Bug | `BenchmarkEngine.cs:47` | Trung bình — báo cáo benchmark sai lệch |
| 4 | `BenchmarkPhaseResult.P50/P95/P99/Max` sort lại toàn bộ mẫu mỗi lần truy cập property (không memo hoá) | Perf | `BenchmarkModels.cs:52-55` | Trung bình (tăng theo Iterations) |
| 5 | `ImageCacheKey.Create` gọi `FileInfo` (stat syscall) lặp lại 2-3 lần cho cùng 1 path trong cùng 1 lần điều hướng (`ShowImageAsync`, `TryGetCachedPreview`, `GetPreviewAsync`, `PreloadOneAsync`) | Perf | `MainWindow.xaml.cs:277-279,397,453,465,626` | Trung bình — I/O thừa trên hot path điều hướng ảnh |
| 6 | `MainWindow.xaml.cs` là God Object 1158 dòng, không DI, không tách service/ViewModel | Kiến trúc | `MainWindow.xaml.cs` | Cao (dài hạn) — cản trở test thật, tăng rủi ro regression |
| 7 | Test suite (`PhotoReview.Tests/Program.cs:175-320`) phần lớn là **grep chuỗi trong source** (`.Contains("...")`) thay vì chạy code thật | Testing | `PhotoReview.Tests/Program.cs` | Cao — false confidence, dễ fail sai khi refactor vô hại, dễ pass sai khi logic hỏng |
| 8 | Fire-and-forget task (`_ = ShowImageAsync(...)`, `_ = PreloadAroundAsync(...)`) không quan sát exception → lỗi âm thầm bị nuốt | Bug | `MainWindow.xaml.cs:232,305,1102` | Trung bình |
| 9 | 2 nơi định nghĩa độc lập `workers = 8` và `SemaphoreSlim(8,8)` phải tự đồng bộ tay | Kiến trúc/Perf | `MainWindow.xaml.cs:36,558` | Thấp-Trung bình |
| 10 | `ExplorerOrderService` tạo thread STA mới mỗi lần mở folder thay vì tái sử dụng | Perf | `ExplorerOrderService.cs:41-48` | Thấp-Trung bình — ảnh hưởng đúng vào giá trị cốt lõi "mở folder tức thời" |
| 11 | Magic numbers trùng lặp (`16GB` RAM cache, `2GB` reserve, `0.80` preload limit) rải rác nhiều file | Kiến trúc | `MainWindow.xaml.cs:41,43`, `BenchmarkImageExecutor.cs:11`, `PhysicalMemory.cs:33`, `BenchmarkProfiles.cs:5` | Thấp |
| 12 | `AppLog` build chuỗi interpolated string trên hot path kể cả khi logging bị tắt (không lazy) | Perf | `MainWindow.xaml.cs:264,285,294,304` + `AppLog.cs:24` | Thấp |
| 13 | Không có test framework thật (xUnit/NUnit), test project là console app tay | Vận hành/CI | `PhotoReview.Tests.csproj` | Trung bình — không CI-friendly (không TRX/JUnit, không parallel, không isolation) |
| 14 | `PhotoReview.App.csproj` kéo cả `UseWinForms` chỉ để dùng `FolderBrowserDialog` | Kiến trúc nhỏ | `PhotoReview.App.csproj` | Thấp |

---

## 1. Nguyên tắc chia agent

- Mỗi **Work Package (WP)** độc lập về file để tránh conflict khi chạy song song.
- Agent nào đụng `MainWindow.xaml.cs` phải chạy **tuần tự** với nhau (nhiều WP cùng file) — không song song.
- Mọi WP đều phải: build sạch (`dotnet build`), chạy lại `PhotoReview.Tests` (console harness hiện tại) không fail, cập nhật `PROGRESS.md`/`TASKS.md`/`ERROR-HISTORY.md` nếu sửa bug đã biết.
- Ưu tiên chạy theo **3 wave** để kiểm soát rủi ro (wave sau phụ thuộc wave trước).

---

## Wave 1 — Sửa lỗi & khoá rủi ro dữ liệu (song song, không đụng chung file)

### WP1.1 — Benchmark correctness: quyết định số phận `BenchmarkImageExecutor`
**Agent:** `benchmark-fixer`
**File:** `PhotoReview.App/BenchmarkImageExecutor.cs`, `PhotoReview.App/BenchmarkWindow.cs`, `PhotoReview.Tests/Program.cs`
**Việc làm:**
1. Wire `BenchmarkImageExecutor.DecodeAsync` vào cả CLI runner (`Program.cs:18-37`) và UI (`BenchmarkWindow.cs:49-58`) làm workload mặc định cho các profile "production-like", thay cho `File.ReadAllBytesAsync`/`FileStream` thô.
2. Sửa race cache-then-decode trong `DecodeAsync` (2 request đồng thời cùng key đều miss cache) bằng in-flight de-dup pattern giống `ThumbnailCache`/`FileHashService` (`ConcurrentDictionary<Key, Lazy<Task<...>>>`).
3. Cập nhật `TASKS.md` (BM-04, BM-12, BM-13) sang DONE kèm bằng chứng (log/run).
**DoD:** benchmark chạy thực sự qua `BitmapImage`/`OnLoad`/`Freeze`; có test (xem WP3.x) khẳng định workload gọi đúng production decode path.

### WP1.2 — Sửa bug tổng hợp metrics & percentile trong Benchmark engine
**Agent:** `benchmark-metrics-fixer`
**File:** `PhotoReview.App/BenchmarkEngine.cs`, `PhotoReview.App/BenchmarkModels.cs`
**Việc làm:**
1. `BenchmarkEngine.cs:47` — đổi `metrics ??= result.Metrics` thành hợp nhất đúng ngữ nghĩa cumulative (ví dụ giữ `metrics = result.Metrics` mỗi iteration nếu đó là snapshot tích luỹ, hoặc cộng dồn nếu là delta — xác nhận ngữ nghĩa `ReviewMetricsSnapshot` trước khi sửa).
2. `BenchmarkModels.cs:52-55` — memo hoá percentile: sort mẫu **một lần** khi `BenchmarkPhaseResult` được tạo (hoặc lazy-cache sorted array), thay vì sort lại mỗi lần gọi `.P50`/`.P95`/`.P99`/`.Max`.
3. Bỏ dead-code defensive clamp `Math.Max(1, profile.Iterations)` (`BenchmarkEngine.cs:25`) hoặc thay bằng `Debug.Assert`/throw nếu invariant bị vi phạm, vì `BenchmarkProfileValidation` đã đảm bảo `Iterations >= 1`.
4. Thêm timeout tuỳ chọn per-iteration (param mới, optional, default = không đổi hành vi) để tránh treo vô hạn khi delegate deadlock.
**DoD:** unit test mới cho `BenchmarkStatistics.Percentile` (edge case: 1 mẫu, số mẫu chẵn/lẻ) + test xác nhận `BenchmarkReport.Metrics` phản ánh đúng nhiều iteration.

### WP1.3 — Chặn disk cache preview phình vô hạn
**Agent:** `disk-cache-guard`
**File:** `PhotoReview.App/MainWindow.xaml.cs` (chỉ phần `GetDiskCachePath`/vùng ghi cache decoded preview, dòng ~407-441, 531-535)
**Việc làm:**
1. Thêm quota + LRU prune cho thư mục `%LocalAppData%\PhotoReview\cache` theo đúng pattern đã có ở `ThumbnailCache.PruneDiskCache` (tái sử dụng code nếu hợp lý, hoặc factor ra 1 class dùng chung `DiskCacheQuotaManager`).
2. Chuyển việc ghi PNG cache sang **background** (không block worker task đang trả ảnh về UI) — hiện tại ghi đĩa xảy ra trước khi `_cache.Set` trả kết quả, kéo dài latency cảm nhận được.
3. Tôn trọng `PHOTOREVIEW_DATA_ROOT` (đã dùng ở `AppLog.cs:18`) cho path này để nhất quán override toàn app.
**DoD:** chạy thử để duyệt >5000 ảnh liên tục, dung lượng thư mục cache không vượt quota cấu hình; có test hồi quy giống style `ERROR-HISTORY.md` (E-0XX mới).

### WP1.4 — Fix fire-and-forget task nuốt exception
**Agent:** `task-safety-fixer`
**File:** `PhotoReview.App/MainWindow.xaml.cs` (chỉ các dòng `_ = XxxAsync(...)`)
**Việc làm:**
1. Thêm helper `FireAndForget(Task task, string context)` bọc try/catch + `AppLog.Error` cho mọi task discarded (`_ = ShowImageAsync`, `_ = PreloadAroundAsync`, `_ = LoadFolderAsync`).
2. Áp dụng đồng loạt tại các call site liệt kê trong báo cáo (dòng 232, 305, 1102, và các chỗ tương tự phát hiện thêm khi grep `_ = `).
**DoD:** grep `_ = ` trong `MainWindow.xaml.cs` không còn task nào thiếu observe; test giả lập throw bên trong 1 trong các Async method và xác nhận log ghi nhận lỗi thay vì im lặng.

> **Lưu ý:** WP1.3 và WP1.4 đều sửa `MainWindow.xaml.cs` nhưng ở vùng code tách biệt rõ (cache path vs fire-and-forget call site). Nếu chạy 2 agent song song, phải merge cẩn thận / hoặc chạy tuần tự để tránh conflict diff.

---

## Wave 2 — Tối ưu hiệu năng hot path (sau khi Wave 1 xong, tuần tự trên `MainWindow.xaml.cs`)

### WP2.1 — Gộp `ImageCacheKey.Create` calls, giảm stat syscall thừa
**Agent:** `hotpath-optimizer`
**File:** `PhotoReview.App/MainWindow.xaml.cs`
**Việc làm:**
1. Trong `ShowImageAsync`, tính `ImageCacheKey` **một lần** rồi truyền xuống `TryGetCachedPreview`, `GetPreviewAsync`, preload-hit check thay vì mỗi hàm tự gọi `ImageCacheKey.Create` lại.
2. Refactor logic lặp `isOriginal = string.Equals(_settings.LoadingMode, "Original", ...)` (6 chỗ) thành 1 helper `GetCurrentCacheKey(path)`.
3. Trong mode "Original", bỏ gọi `GetOriginalDimensionsAsync` thừa khi bitmap RAM-cached đã là full-res (dùng trực tiếp `image.PixelWidth/PixelHeight`); chỉ gọi lại khi ở Preview mode (ảnh đã downscale).
**DoD:** benchmark trước/sau (dùng chính benchmark đã fix ở WP1.1) cho thấy giảm số lần gọi `FileInfo`/decode header trên mỗi lần Next/Previous; không có regression trong `PhotoReview.Tests`.

### WP2.2 — Đồng bộ hằng số & loại bỏ duplicate magic numbers
**Agent:** `constants-consolidator`
**File:** tạo mới `PhotoReview.App/AppConstants.cs` (hoặc mở rộng file config có sẵn), sửa `MainWindow.xaml.cs`, `PhysicalMemory.cs`, `BenchmarkProfiles.cs`, `BenchmarkImageExecutor.cs`
**Việc làm:**
1. Gom `16GB` RAM cache size, `2GB` reserve, `0.80` preload memory ratio, `8` preload workers/slots thành hằng số đặt tên rõ ràng dùng chung.
2. Đảm bảo `workers` (local const trong `RunPreloadSchedulerAsync`) và `_preloadSlots` (`SemaphoreSlim`) đọc từ cùng 1 hằng số.
**DoD:** build sạch, không còn literal trùng lặp (grep xác nhận), hành vi runtime không đổi (so benchmark trước/sau).

### WP2.3 — Giảm chi phí AppLog trên hot path
**Agent:** `logging-optimizer`
**File:** `PhotoReview.App/AppLog.cs`, các call site log trong `MainWindow.xaml.cs`
**Việc làm:**
1. Thêm guard `if (AppLog.Enabled)` tại call site trước khi build interpolated string, hoặc đổi API `AppLog.Info` sang nhận `Func<string>`/dùng `LoggerMessage`-style deferred formatting.
2. Thay `Thread.Yield()` busy-spin trong `AppLog.Flush()` (`AppLog.cs:21`) bằng chờ có tín hiệu (`ManualResetEventSlim`/`SemaphoreSlim`) thay vì spin tối đa 2 giây.
**DoD:** CPU profiling cho thấy giảm allocation/CPU khi logging tắt; `Flush()` không còn busy-spin (verify bằng test đo CPU time trong lúc flush).

### WP2.4 — Tái sử dụng STA thread pump cho Explorer order thay vì tạo mới mỗi lần
**Agent:** `explorer-order-optimizer`
**File:** `PhotoReview.App/ExplorerOrderService.cs`
**Việc làm:**
1. Thay tạo `Thread` mới mỗi lần `QueryShell` bằng 1 dedicated STA thread pump tái sử dụng (channel/queue dispatch), giữ nguyên COM ref-count discipline (`finally { Marshal.Release(...) }`).
2. Sửa lỗ hổng catch-all thiếu ở `QueryShell` (dòng ~75-88): bọc thêm catch cho exception ngoài `COMException`/`RuntimeBinderException` để log đủ context `windowsInspected`.
**DoD:** đo latency mở folder trước/sau (dùng `LocalUiNextProbe`/`--explorer-probe`), xác nhận giảm hoặc không tệ hơn; không leak COM object (theo dõi Handle count qua Task Manager trong 1 phiên chạy dài).

> Tham khảo file đã có sẵn `EXPLORER-ORDER-OPTIMIZATION-PLAN.md` — đối chiếu để tránh trùng lặp công việc đã lên kế hoạch trước đó.

---

## Wave 3 — Kiến trúc & chất lượng test (dài hạn, có thể chạy song song với Wave 2 vì khác phạm vi)

### WP3.1 — Thay thế "test" grep-source-text bằng test hành vi thật
**Agent:** `test-quality-fixer`
**File:** `PhotoReview.Tests/Program.cs` (dòng 175-320), có thể cần expose thêm internal API/testable service từ `MainWindow.xaml.cs`
**Việc làm:**
1. Với từng check hiện đang làm `mainWindow.Contains("...")`, xác định hành vi thực sự cần kiểm tra, và:
   - Nếu logic đã nằm trong 1 service tách biệt (`OperationJournal`, `FileHashService`, v.v.) → viết test gọi thẳng service đó (đã có phần này, giữ nguyên).
   - Nếu logic còn kẹt trong `MainWindow.xaml.cs` → extract thành method/service public đủ để test gọi trực tiếp (phối hợp với WP3.2).
2. Xoá các check chỉ còn ý nghĩa "tài liệu hoá" sau khi đã có test thật thay thế, hoặc giữ lại nhưng đổi tên rõ ràng (`AssertSourceContains` helper) để không nhầm là test hành vi.
**DoD:** không còn `.Contains(...)` trên nội dung file source được dùng làm tiêu chí pass/fail hành vi; toàn bộ check cũ có test hành vi thay thế tương đương hoặc lý do ghi rõ vì sao giữ nguyên.

### WP3.2 — Tách logic khỏi `MainWindow.xaml.cs` thành service có thể inject/test
**Agent:** `architecture-refactor` (agent lớn, cần review kỹ, khuyến nghị chạy cuối cùng sau khi Wave 1+2 ổn định)
**File:** `MainWindow.xaml.cs` → nhiều file service mới
**Việc làm (theo thứ tự ưu tiên, mỗi bước là 1 PR nhỏ để dễ review):**
1. Extract preload scheduling (`RunPreloadSchedulerAsync` và phần liên quan) → `PreloadScheduler` class nhận dependency qua constructor.
2. Extract file-action orchestration (Move/Copy/Recycle/Undo, restore-on-failure) → `FileActionCoordinator`.
3. Extract decode/cache logic (`GetPreviewAsync`, `DecodeSource`, `TryGetCachedPreview`) → `PreviewImageService` (đây cũng là chỗ hợp nhất với `BenchmarkImageExecutor` từ WP1.1 để **benchmark dùng đúng 1 class production**, xoá triệt để nguy cơ "3 bản decode logic khác nhau" nêu ở mục kiến trúc #3 trong báo cáo).
4. `MainWindow` chỉ còn giữ vai trò UI glue, nhận các service qua constructor (DI thủ công, chưa cần container).
**DoD:** `PhotoReview.Tests` có thể test `PreviewImageService`/`PreloadScheduler`/`FileActionCoordinator` độc lập không cần khởi tạo `MainWindow`/WPF; benchmark (WP1.1) gọi thẳng `PreviewImageService` thay vì có class `BenchmarkImageExecutor` riêng → **xoá được file `BenchmarkImageExecutor.cs`** sau khi hợp nhất.

### WP3.3 — Nâng cấp test project lên framework thật (xUnit) — tuỳ chọn, đánh giá rủi ro trước
**Agent:** `test-infra-migrator`
**File:** `PhotoReview.Tests.csproj`, `PhotoReview.Tests/*.cs`
**Việc làm:**
1. Thêm xUnit (`Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`) song song với console harness hiện có (không xoá ngay CLI benchmark runner — tách phần `--benchmark*`/`--*-probe` CLI dispatcher ra 1 project console riêng `PhotoReview.BenchTools` nếu cần giữ khả năng chạy benchmark từ dòng lệnh).
2. Chuyển các `Check(...)` thành `[Fact]`/`[Theory]` để có isolation + báo cáo TRX (CI-friendly).
**DoD:** `dotnet test` chạy được, sinh TRX; các probe/benchmark CLI vẫn chạy được qua entry point riêng, không mất chức năng hiện có.

> **Cân nhắc:** đây là thay đổi lớn về tooling, nên hỏi ý kiến người dùng/maintainer trước khi thực thi toàn bộ WP3.3 vì ảnh hưởng workflow CI hiện tại (`tools/verify-all.ps1`, `tools/verify-release.ps1` có thể đang gọi trực tiếp `PhotoReview.Tests.exe`).

### WP3.4 — Dọn `csproj`/build hygiene
**Agent:** `build-hygiene`
**File:** `PhotoReview.App.csproj`, thêm `Directory.Build.props`, `.editorconfig`
**Việc làm:**
1. Đánh giá thay `UseWindowsForms`+`FolderBrowserDialog` bằng `Microsoft.Win32`/WPF-native folder picker hoặc `CommonOpenFileDialog` nếu có sẵn thư viện nhẹ hơn — cân nhắc lợi ích thực tế trước khi đổi (rủi ro thấp nhưng không phải ưu tiên cao).
2. Thêm `Directory.Build.props` gom `Nullable`, `ImplicitUsings`, `LangVersion` dùng chung 2 project.
3. Cân nhắc bật analyzer cơ bản (`EnableNETAnalyzers`, `AnalysisLevel=latest`) — chạy thử, xử lý warning phát sinh theo đợt riêng, không chặn các WP khác.
**DoD:** build không phát sinh warning mới nghiêm trọng; `UseWindowsForms` được loại bỏ nếu thay thế thành công, hoặc giữ nguyên kèm ghi chú lý do.

---

## 2. Sơ đồ phụ thuộc & khuyến nghị điều phối agent

```
Wave 1 (song song, review kỹ merge nếu đụng MainWindow.xaml.cs):
  WP1.1 (benchmark-fixer)        ─┐
  WP1.2 (benchmark-metrics-fixer)─┼─ độc lập file, chạy song song an toàn
  WP1.3 (disk-cache-guard)       ─┤  (khác vùng MainWindow.xaml.cs)
  WP1.4 (task-safety-fixer)      ─┘  (khác vùng MainWindow.xaml.cs, chạy SAU WP1.3 nếu muốn tránh merge conflict)

Wave 2 (sau Wave 1, tuần tự trên MainWindow.xaml.cs; WP2.2-2.4 có thể song song vì khác file):
  WP2.1 (hotpath-optimizer)        [MainWindow.xaml.cs — chạy 1 mình]
  WP2.2 (constants-consolidator)   [file khác, song song với WP2.1 OK nếu không đụng đúng dòng]
  WP2.3 (logging-optimizer)        [AppLog.cs + MainWindow.xaml.cs log lines — phối hợp với WP2.1]
  WP2.4 (explorer-order-optimizer) [ExplorerOrderService.cs — độc lập hoàn toàn, song song mọi lúc]

Wave 3 (có thể bắt đầu song song với Wave 2, nhưng WP3.2 nên chạy SAU cùng):
  WP3.1 (test-quality-fixer)     — phối hợp chặt với WP3.2
  WP3.2 (architecture-refactor)  — lớn nhất, làm cuối, cần con người review
  WP3.3 (test-infra-migrator)    — tuỳ chọn, cần approval trước
  WP3.4 (build-hygiene)          — độc lập, chạy bất kỳ lúc nào
```

**Khuyến nghị vận hành với nhiều agent:**
- Dùng `Agent` tool với `isolation: "worktree"` cho mỗi WP để agent làm việc trên bản sao git độc lập, tránh đụng file khi chạy song song thật sự.
- Mỗi WP kết thúc bằng 1 commit riêng + build/test pass, sau đó review/merge tuần tự vào `master` theo đúng thứ tự Wave.
- Với các WP cùng đụng `MainWindow.xaml.cs` (WP1.3, WP1.4, WP2.1, WP2.3), nên merge tuần tự từng cái một, chạy lại `PhotoReview.Tests` sau mỗi merge để bắt conflict logic sớm.
- Cập nhật `PROGRESS.md`/`TASKS.md`/`ERROR-HISTORY.md` sau mỗi WP hoàn thành (đúng convention đã có trong repo) để giữ tính nhất quán với tài liệu audit hiện hữu.

---

## 3. Tiêu chí hoàn thành toàn bộ plan

- [ ] Benchmark đo đúng production decode path (WP1.1), số liệu P50/P95/P99 đáng tin cậy (WP1.2).
- [ ] Không còn tăng trưởng đĩa không kiểm soát từ preview cache (WP1.3).
- [ ] Không còn fire-and-forget task nuốt exception trong `MainWindow.xaml.cs` (WP1.4).
- [ ] Giảm số lần stat/decode thừa trên mỗi lần Next/Previous, đo được bằng benchmark đã sửa (WP2.1).
- [ ] Không còn magic number trùng lặp giữa các file (WP2.2).
- [ ] Logging không tốn chi phí khi tắt; `AppLog.Flush()` không busy-spin (WP2.3).
- [ ] Mở folder qua Explorer-order nhanh hơn hoặc bằng hiện tại, không leak COM (WP2.4).
- [ ] Test suite không còn phụ thuộc vào grep source text để xác nhận hành vi (WP3.1).
- [ ] `MainWindow.xaml.cs` giảm kích thước đáng kể, logic cốt lõi nằm trong service test được độc lập; `BenchmarkImageExecutor.cs` không còn là bản sao riêng mà dùng chung `PreviewImageService` (WP3.2).
- [ ] (Tuỳ chọn, cần approval) Test project chạy trên xUnit, có TRX output cho CI (WP3.3).
- [ ] Build sạch, hygiene cấu hình project nhất quán (WP3.4).

---

*File này bổ sung cho, không thay thế, các tài liệu audit hiện có (`04-PERFORMANCE.md`, `06-TESTING.md`, `ERROR-HISTORY.md`, `TASKS.md`, `EXPLORER-ORDER-OPTIMIZATION-PLAN.md`). Trước khi thực thi từng WP, đối chiếu lại các file đó để tránh trùng lặp công việc đã ghi nhận.*
