# Task tối ưu cấu trúc (ST00–ST12)

- **Plan:** [`STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md`](STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md) · **Cập nhật:** 2026-09-20
- **Trạng thái hợp lệ:** `TODO` · `IN PROGRESS` · `BLOCKED` · `DONE`. Toàn bộ task dưới đây hiện là `TODO` (hoặc `BLOCKED` nếu ghi rõ); **chưa task nào được triển khai**.
- **Ký hiệu:** **⛔Qn** cần quyết định Qn trong plan mục 2 trước khi bắt đầu; **∥** chạy song song được.
- **Mức model gợi ý:** L1 (nhỏ, cơ học) / L2 (vừa) / L3 (đụng invariant hoặc `MainViewModel`). Coordinator luôn đọc diff trước khi merge, đặc biệt task L1 (lịch sử: model nhỏ từng làm quá phạm vi).

## 0. Hướng dẫn cho model thực thi

1. Đọc `AGENTS.md`, `task_on_progress.md`, plan ST, rồi task được chọn.
2. Chọn **một** task `TODO` mà mọi task trong **Phụ thuộc** đã `DONE` và không còn ⛔ chưa trả lời. Kiểm tra `git status`, branch, SHA trước khi làm.
3. Chỉ sửa file trong **Files**. Cần sửa file khác → đổi `BLOCKED`, ghi lý do, không tự mở rộng.
4. `MainViewModel.cs`, `App.xaml.cs`, `MainWindow.xaml.cs` chỉ một owner tại một thời điểm; kiểm tra không có task OC/WD/IO/ST khác đang `IN PROGRESS` trên cùng file.
5. Refactor không đổi hành vi: không sửa, xóa hay nới assertion để test qua. Test bị thay thế phải có test hành vi tương đương.
6. Chạy **Xong khi**; ghi nhật ký (lệnh, kết quả thật), đổi trạng thái, commit `refactor(<vùng>): <mô tả> (<ID>)` trên nhánh `codex/structure-optimize-<ID>`.

## 1. Bảng tổng hợp

| ID | Tên | Mức | Phụ thuộc | Trạng thái |
|---|---|---|---|---|
| ST00 | Baseline và rule kiến trúc mới | L1 | — | TODO |
| ST01 | Dọn code chết và trừu tượng thừa | L1 | ST00 | TODO |
| ST02 | `SourceSizeTracker` (bỏ stat lại toàn folder) | L2 | ST01 | TODO |
| ST03 | Trích `AppComposition` khỏi `App.xaml.cs` | L2 | ST02 | TODO |
| ST04 | Đưa hạ tầng xuống dưới App | L2 | ST03, ⛔Q-ST2 | BLOCKED (Q-ST2) |
| ST05 | Tách `PerfAnalyze*` khỏi project WPF ∥ | L2 | ST00, ⛔Q-ST1 | BLOCKED (Q-ST1) |
| ST06 | Bỏ reflection của CLI vào `MainWindow` | L3 | ST03, ST04 | TODO |
| ST07 | Dependency bắt buộc + test builder cho `MainViewModel` | L3 | ST03 | TODO |
| ST08 | Trích `FileActionController` | L3 | ST07, OC14, ⛔Q-ST4 | BLOCKED (OC14/Q-ST4) |
| ST09 | Trích `DuplicateCleanupController` và `SiblingFolderNavigator` | L3 | ST08 | TODO |
| ST10 | Dọn test và rule kiến trúc còn lại | L2 | ST04–ST09 (từng phần) | TODO |
| ST11 | Đồng bộ tài liệu | L1 | ST01–ST10 | TODO |
| ST12 | Điều tra project `Presentation` (không triển khai) | L2 | ST06, ST09 | TODO |

## 2. Task chi tiết

### ST00 — Baseline và rule kiến trúc mới — TODO

- **Mục tiêu:** khóa mốc so sánh và thêm rule đang đúng sẵn để các task sau không làm xấu đi ranh giới.
- **Files:** `tests/PhotoReview.Architecture.Tests/*` (chỉ thêm), `task_on_progress.md`.
- **Làm:** chạy build + `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"`, ghi số PASS từng project và SHA (baseline `5dc5cda` đã có commit preload sau lần đo 762/762). Thêm rule đang PASS trên baseline: `Imaging.TurboJpeg` không phụ thuộc `PhotoReview.App`/`Platform.Windows`; `Core` không tham chiếu type của tầng trên (đã có). Đánh số lại rule liên tục.
- **Không làm:** không thêm rule sẽ FAIL trên baseline (Cli→App, reflection) — các rule đó thuộc ST06/ST10.
- **Xong khi:** số PASS baseline ghi trong nhật ký; rule mới PASS; không đổi source ngoài test.
- **Rủi ro/rollback:** không; revert commit test.

### ST01 — Dọn code chết và trừu tượng thừa — TODO

- **Phụ thuộc:** ST00. **Mức:** L1→L2 (đụng `MainWindow*`).
- **Files:** `src/PhotoReview.App/ThumbnailModels.cs` (xóa), `App/Diagnostics/PhotoReviewPerf.cs`, `App/IProgressiveExplorerOrderProvider.cs` (xóa), `App/App.xaml.cs` (đăng ký), `App/MainWindow.xaml.cs`, `App/MainWindowHelpers.cs`, `App/MainWindowTestHooks.cs`, `App/AssemblyInfo.cs` (comment), `tests/PhotoReview.App.Tests/CompositionRootTests.cs`, `tests/PhotoReview.Integration.Tests/Fakes/FakeExplorerOrderProvider.cs` và test dùng hook.
- **Làm:**
  1. Xác nhận lại bằng grep rằng `ThumbnailCacheOptions`/`ThumbnailResult` không được dùng (kể cả tools/tests/XAML), rồi xóa `ThumbnailModels.cs`.
  2. Bỏ marker `IProgressiveExplorerOrderProvider` và `ExplorerOrderProviderAdapter`; dùng `IExplorerOrderProvider` (đã có `TryGetSnapshotProgressiveAsync`). Đăng ký `ExplorerOrderService` **một lần** (singleton, dispose bởi container); `MainWindowHelpers` lấy từ hook hoặc từ DI, không tự `new`.
  3. Đổi tên/loại bỏ file alias `App/Diagnostics/PhotoReviewPerf.cs` sang tên đúng nội dung (ví dụ `GlobalUsings.cs`) hoặc chuyển `global using` vào chỗ hợp lý.
- **Không làm:** không đổi hành vi Explorer snapshot, timeout, thứ tự; không đụng `ExplorerOrderService` (Platform).
- **Xong khi:** build sạch; `CompositionRootTests`, `MainWindowBehaviorTests.Explorer`, `FolderLoadCoordinatorTests`, `ExplorerOrderServiceTests` PASS; full gate không giảm; grep xác nhận không còn tham chiếu type đã xóa; chỉ còn một instance `ExplorerOrderService` trong DI.
- **Rủi ro/rollback:** hook test seam đổi kiểu; commit riêng, revert được.

### ST02 — `SourceSizeTracker` (bỏ stat lại toàn folder) — TODO

- **Phụ thuộc:** ST01. **Mức:** L2. Đây là task **hành vi** duy nhất của gói; commit riêng.
- **Bối cảnh:** S3 — `GetTotalSourceBytes` re-stat toàn folder mỗi khi membership đổi; OC08 ghi DONE cho nửa đầu (sort/remap) nhưng nửa còn lại chưa xong. Sau task này, cập nhật lại ghi chú OC08 trong `OPTIMIZE-CLEAN-PLAN` cho khớp thực tế.
- **Files:** `src/PhotoReview.Core/Catalog/` (type mới, ví dụ `SourceSizeTracker`), `Core/Catalog/ReviewCatalog.cs` và/hoặc `CatalogEntry.cs` nếu cần lộ `Length` theo path, `App/App.xaml.cs` (chỉ thay lambda), tests Core.
- **Làm:** tính tổng byte từ `Length` đã có trong `CatalogEntry` lúc scan, cập nhật theo thay đổi membership (remove/restore/insert) thay vì stat lại; fallback stat chỉ cho path chưa có metadata. Thread-safe, không giữ lock khi gọi filesystem. Giữ nguyên chữ ký `Func<long>` mà `PreloadScheduler` nhận.
- **Không làm:** không đổi RAM budget/policy của preload; không cache lâu hơn vòng đời catalog; không bỏ kiểm identity tại decode/file action.
- **Tests:** `CountingFileSystem` chứng minh 0 lần `GetFileStat` sau 100 lần remove; tổng đúng sau remove/restore/reload; file mất/đổi kích thước giữa scan và tính; 1k/10k entries; đồng thời với `Snapshot()`.
- **Xong khi:** targeted pass; full gate không giảm; ghi số `GetFileStat` trước/sau; không có tuyên bố % tốc độ khi chưa đo.
- **Rủi ro/rollback:** metadata cũ sau khi file bị đổi ngoài app → chỉ ảnh hưởng ước lượng budget; revert commit độc lập.

### ST03 — Trích `AppComposition` khỏi `App.xaml.cs` — TODO

- **Phụ thuộc:** ST02. **Mức:** L2, refactor cơ học.
- **Files:** `App/App.xaml.cs`, file mới `App/Composition/*` (ví dụ `AppComposition.cs`, `ReviewSessionFactory.cs`), `App/Diagnostics/` (chuyển `PerfDispatcherHooks` ra file riêng), `tests/PhotoReview.App.Tests/CompositionRootTests.cs`.
- **Làm:** chuyển factory `MainViewModel` (App.xaml.cs 138-206) vào type có tên rõ ràng, giữ cách phá vòng VM↔sink nhưng gói trong một chỗ có comment; `ConfigureServices` gọi vào đó. Chuyển `PerfDispatcherHooks` ra `Diagnostics/`. `App.xaml.cs` chỉ còn startup/lifecycle/DI wiring.
- **Không làm:** không đổi thứ tự khởi tạo, lifetime (Singleton/Transient), tham số, hay hành vi. Không gộp với ST04/ST07.
- **Xong khi:** `CompositionRootTests` PASS; `App.xaml.cs` giảm rõ rệt (mục tiêu < 200 dòng, không phải điều kiện cứng); full gate không giảm; smoke `verify-all` mở app thành công.
- **Rủi ro/rollback:** đổi thứ tự resolve gây lỗi khởi động; commit riêng.

### ST04 — Đưa hạ tầng xuống dưới App — BLOCKED (Q-ST2)

- **Phụ thuộc:** ST03, ⛔Q-ST2. **Mức:** L2.
- **Files:** `App/FileHashService.cs` → `Imaging/Caching/` (hoặc `Imaging/`); `App/PhysicalMemory.cs` → gộp vào `Platform.Windows/WindowsMemoryProbe.cs` (policy `MemoryReserveBytes`/0.85 truyền qua tham số hoặc `PerformanceOptions`); `App/Diagnostics/PerfCsvListener.cs`, `DiagOptions.cs` → `Core/Diagnostics/`; các `using`/DI/test/CLI bị ảnh hưởng; `Core/Diagnostics/PhotoReviewPerf.cs` (sửa `cref`).
- **Làm:** di chuyển từng nhóm một, mỗi nhóm một commit; giữ tên EventSource `PhotoReview-Perf` và event id nguyên vẹn. `IMemoryProbe` vẫn do Platform cung cấp; App chỉ đăng ký.
- **Không làm:** không đổi ngưỡng memory pressure hay nội dung CSV; không đổi hành vi hash. `PerfDispatcherHooks` phải ở lại App (cần WPF Dispatcher).
- **Tests:** `FileHashServiceTests`, `DiagOptionsTests`, `PerfTraceTests`, `DiagOverrideTests`, `PreloadSafetyTests` di chuyển/cập nhật namespace và vẫn PASS; rule kiến trúc: Core không phụ thuộc WPF sau khi nhận `PerfCsvListener`.
- **Xong khi:** build toàn solution; test không giảm; `Benchmark.Cli` biên dịch với using mới; wire contract EventSource không đổi.
- **Rủi ro/rollback:** namespace đổi làm gãy consumer ngoài repo (ghi chú); revert từng commit nhóm.

### ST05 — Tách `PerfAnalyze*` khỏi project WPF ∥ — BLOCKED (Q-ST1)

- **Phụ thuộc:** ST00, ⛔Q-ST1. **Mức:** L2. Không đụng `MainViewModel`/`App.xaml.cs`, chạy song song được.
- **Files:** `src/PhotoReview.Benchmarking/PerfAnalyze*.cs` (6 file), project mới (mặc định `src/PhotoReview.PerfAnalysis/`, `net10.0`), `PhotoReview.slnx`, `tools/PhotoReview.Benchmark.Cli/*.csproj` và `Program.cs`, `tests/PhotoReview.Integration.Tests/PerfAnalyzeTests.cs` (chuyển sang `Core.Tests` hoặc test project không cần WPF).
- **Làm:** xác nhận `PerfAnalyze*` không phụ thuộc type WPF/Benchmarking khác, rồi chuyển nguyên trạng, giữ namespace công khai nếu có thể để giảm churn. Cập nhật tham chiếu và solution. Thêm rule kiến trúc: project mới không phụ thuộc WPF/App/Imaging.
- **Không làm:** không đổi logic grouping/rules (OC10 đã DONE); không đổi định dạng CSV/report.
- **Tests:** `PerfAnalyzeTests` 33/33 (số baseline của OC10) vẫn PASS, chạy được không cần STA; `--perf-analyze` trên hai fixture worker 0/8 vẫn ra 2 file, 1 nhóm hoặc kết quả tương đương như cũ.
- **Xong khi:** build sạch; test không giảm; project mới `net10.0` (không `-windows`).
- **Rủi ro/rollback:** `Benchmarking` còn dùng nội bộ `PerfAnalyze`? nếu có, thêm tham chiếu ngược; revert bằng cách chuyển file lại.

### ST06 — Bỏ reflection của CLI vào `MainWindow` — TODO

- **Phụ thuộc:** ST03, ST04 (và OC15–OC17 nếu đang chạm `MainWindow.xaml.cs`). **Mức:** L3.
- **Bối cảnh:** S8 — 26 site reflection trong `tools/PhotoReview.Benchmark.Cli` (`WpfTestHost.cs`, `PerfSession.cs`, `LocalUiNextProbe.cs`, `IoDecodeSplit.cs`, `LocalImageBenchmark.cs`); các member private/compat của `MainWindow` (`_files`, `_index`, `_fileActionInProgress`, `_metrics`, `_settings`, `LoadFolderAsync`, `ShowImageAsync`, `ResetFitView`, `SetZoom`, `TryGetCachedPreview`) tồn tại phục vụ CLI.
- **Files:** `tools/PhotoReview.Benchmark.Cli/*.cs`, `App/MainWindow.xaml.cs`, `App/MainWindowHelpers.cs`, `App/MainWindowTestHooks.cs`, một seam công khai/`internal`+`InternalsVisibleTo` mới (ví dụ `IReviewSurface` hoặc thao tác qua `MainViewModel`), `tests/PhotoReview.Integration.Tests`.
- **Làm:** liệt kê từng site reflection và thao tác tương ứng; thay bằng API có tên (qua `MainViewModel`/seam) hoặc bằng routed event trong process. Sau khi không còn site nào dùng, xóa các field/method compat (`_files`, `_index`... nếu không còn nơi nào khác dùng — kiểm bằng grep và test). Thêm rule kiến trúc/test: CLI không dùng `BindingFlags.NonPublic` trên type của App.
- **Không làm:** không dùng OS SendInput/SendKeys/SetForegroundWindow (quy tắc harness của repo); không đổi số liệu/định dạng đầu ra benchmark; không đổi hành vi cửa sổ thật.
- **Tests:** `LocalUiNextProbe`, `PerfSession`, `IoDecodeSplit` chạy được trên fixture tạm (không ảnh người dùng); `MainWindowBehaviorTests.*` PASS; `--benchmark-list-profiles` và một phiên `--perf-session` ngắn thành công.
- **Xong khi:** `grep GetField|GetMethod` trong CLI bằng 0 với type `MainWindow`; build + test không giảm; mọi CLI command trước đây chạy được vẫn chạy.
- **Rủi ro/rollback:** chỉ phát hiện lỗi ở runtime; do đó bắt buộc chạy thử từng command CLI. Tách hai commit: (a) thay reflection, (b) xóa compat member.

### ST07 — Dependency bắt buộc + test builder cho `MainViewModel` — TODO

- **Phụ thuộc:** ST03. **Mức:** L3. Owner duy nhất của `MainViewModel.cs` và test VM khi chạy.
- **Bối cảnh:** S1. Constructor 21 tham số, 13 optional.
- **Files:** `App/ViewModels/MainViewModel.cs`, `App/Composition/*`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModel*Tests.cs`, helper test mới (`MainViewModelBuilder`).
- **Làm:** phân loại từng dependency: bắt buộc trong production (`FileActionService`, `UndoService`, `IDialogService`, `IPreloadController`, `SessionWriter`, `IUiScheduler`, `FileHashService`, `PreviewImageService`, `ThumbnailCache`, `ReviewMetrics`…) hay thực sự optional; đổi bắt buộc thành tham số không-null và xóa nhánh `?.`/null-guard tương ứng; test dùng `MainViewModelBuilder` với fake/no-op tường minh. Nếu nhiều tham số cùng nhóm, gom thành options/record dependency có tên (không cố ý giấu số lượng).
- **Không làm:** chưa tách controller (ST08–ST09); không đổi hành vi; không thay `IServiceProvider` vào VM; không cơ học đổi mọi optional (dependency có ý nghĩa optional phải giữ và ghi lý do).
- **Xong khi:** build; `MainViewModel*Tests` PASS với builder; số null-guard giảm (ghi trước/sau); `CompositionRootTests` PASS; full gate không giảm.
- **Rủi ro/rollback:** test setup thay đổi lớn; commit riêng. Không chạy chồng WD03 (cùng vùng test helper) — thống nhất thứ tự với WD03 trước.

### ST08 — Trích `FileActionController` — BLOCKED (OC14/Q-ST4)

- **Phụ thuộc:** ST07, OC14 (semantics Undo đã chốt), ⛔Q-ST4. **Mức:** L3 (đụng INV-3/4/5/6).
- **Files:** `App/ViewModels/MainViewModel.cs` (đoạn `RunActionAsync`, `RecycleAsync`, `ExecuteFileActionCoreAsync`, `UndoAsync`, `UndoLastAsync`, khoảng dòng 292–515 ở baseline), file mới `App/ViewModels/FileActionController.cs`, `MainWindow.xaml.cs` (entry point Ctrl+Z / menu), tests App/Integration Undo và file action.
- **Làm:** chuyển thao tác file + Undo + guard mutual-exclusion vào controller có một command boundary duy nhất; `MainViewModel` chỉ ủy quyền và cập nhật trạng thái hiển thị. Giữ đúng thứ tự: advance một lần trước I/O; hủy cập nhật catalog/undo khi folder đã đổi (generation).
- **Không làm:** không đổi contract journal/`FileActionService`; không đổi semantics Undo ngoài quyết định OC14; không giữ lock khi chờ dialog.
- **Tests:** toàn bộ test OC14 (Move→Undo, Recycle→Undo, hai entry point đồng thời, hai Undo đồng thời, folder switch, không history, guard release khi exception) + `MainViewModelFileActionTests`, `InterleavedFileActionSequenceTests` không đổi kết quả.
- **Xong khi:** INV-3/4/5 có test PASS; `MainViewModel` giảm rõ; full gate không giảm.
- **Rủi ro/rollback:** race interleaving; commit riêng (ST08 không lẫn OC14 patch); revert không đụng journal/filesystem.

### ST09 — Trích `DuplicateCleanupController` và `SiblingFolderNavigator` — TODO

- **Phụ thuộc:** ST08. **Mức:** L3.
- **Files:** `MainViewModel.cs` (`RemoveDuplicatesAsync`, `ClearCacheAsync`, `NavigateSiblingFolderAsync`, `FindNextImageFolder`), file mới tương ứng trong `App/ViewModels/` (hoặc `App/Coordinators/`), tests `MainViewModelAdvancedTests`, `SiblingFolderServiceTests`, duplicate tests.
- **Làm:** tách hai nhóm thành collaborator nhỏ; giữ nguyên xác nhận dialog, kiểm generation/identity trước mutation (OC02) và `IUiScheduler` cho dialog sau `await`.
- **Không làm:** không đổi policy survivor (`DuplicateFinder`), không bỏ màn xác nhận; không đổi `ConfigureAwait` một cách cơ học.
- **Xong khi:** tests liên quan PASS, đặc biệt các test STA/UI thread của OC02; `MainViewModel.cs` giảm còn trách nhiệm điều phối chính (mục tiêu định tính, ghi số dòng trước/sau).
- **Rủi ro/rollback:** regress F04 (dialog sai thread); commit riêng cho từng controller.

### ST10 — Dọn test và rule kiến trúc còn lại — TODO

> **Đã được mở rộng và ưu tiên lại bởi [`TEST-CLEANUP-PLAN-2026-09-20.md`](TEST-CLEANUP-PLAN-2026-09-20.md) (TC00–TC11).** Phần gộp `OperationJournalTests`, `SourcePresenceTests` và trait/README nằm ở TC10; test đường nóng (đọc đĩa, Next, Move/Delete liên tục) ở TC01–TC08. Các rule kiến trúc bên dưới vẫn thuộc ST10.

- **Phụ thuộc:** làm rải theo từng task; hoàn tất sau ST04–ST09. **Mức:** L2.
- **Files:** `tests/PhotoReview.Architecture.Tests/*`, `tests/PhotoReview.Core.Tests/OperationJournalTests.cs` và `FileActions/OperationJournalTests.cs`, `tests/PhotoReview.App.Tests/SourcePresenceTests.cs`, `ProjectSources.cs`.
- **Làm:** (a) thêm rule sau khi các task tương ứng xong: Cli không reflection private vào App (ST06); project `PerfAnalysis` không phụ thuộc WPF (ST05); `FileHashService`/`PerfCsvListener` ở đúng tầng (ST04). (b) Gộp hai `OperationJournalTests` vào một chỗ (đổi tên lớp nếu trùng), không mất test nào — đối chiếu danh sách `[Fact]` trước/sau. (c) `SourcePresenceTests`: thay từng nhóm bằng test hành vi khi có hạ tầng (theo `docs/refactoring/test-parity.md` và OC12); nhóm chưa có thay thế giữ nguyên, ghi rõ vì sao.
- **Không làm:** không gom `GlobalStateCollection` về `TestSupport` (xUnit yêu cầu cùng assembly); không mass-rename test; không xóa test để giảm cảnh báo.
- **Xong khi:** số `[Fact]/[Theory]` sau ≥ trước cho phần gộp; rule mới PASS; đối chiếu test-parity cập nhật.

### ST11 — Đồng bộ tài liệu — TODO

- **Phụ thuộc:** ST01–ST10 (theo phần đã DONE). **Mức:** L1.
- **Files:** `docs/architecture.md`, `README.md` (nếu nhắc cấu trúc), `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md` (ghi chú OC08/OC12 khớp thực tế), `task_on_progress.md`, hai file ST.
- **Làm:** cập nhật sơ đồ phụ thuộc (thêm `Benchmarking → Imaging/Platform`, `Benchmark.Cli`, `PerfAnalysis` nếu có), luồng composition, vị trí `FileHashService`/`PerfCsvListener`; chỉ mô tả điều đã làm, không mô tả kế hoạch như đã xong.
- **Xong khi:** sơ đồ đối chiếu với `ProjectReference` thực tế (kiểm bằng lệnh); không còn liên kết gãy.

### ST12 — Điều tra project `Presentation` (không triển khai) — TODO

- **Phụ thuộc:** ST06, ST09. **Mức:** L2, chỉ tài liệu.
- **Câu hỏi:** sau ST06/ST09, nếu ViewModels/Coordinators/Sinks được đưa vào project riêng (`net10.0-windows`, không có `Window`/XAML), `Benchmark.Cli` và test có bỏ được tham chiếu tới `PhotoReview.App` không? Chi phí: di chuyển file, `InternalsVisibleTo`, thay đổi test, khóa XAML resource (`pack://application`).
- **Đầu ra:** ghi chú quyết định (ADR ngắn trong `docs/adr/`) nêu số type phải di chuyển, tham chiếu vòng phát hiện, lợi ích đo được (thời gian build/test, số project tham chiếu App), và khuyến nghị làm/không làm. Không sửa source.
- **Xong khi:** có quyết định được người dùng duyệt; nếu "làm", lập task mới, không mở rộng ST này.

## 3. Nhật ký

Chưa có mục nào. Mẫu: `ID · ngày · agent · SHA · lệnh đã chạy + kết quả PASS/FAIL thật · file sửa · vướng mắc`.
