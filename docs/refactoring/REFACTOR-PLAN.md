# PhotoReview — Plan tái cấu trúc

- **Ngày lập:** 2026-09-16
- **Baseline:** `master` @ `86282cd`
- **Trạng thái plan:** `CHỜ XÁC NHẬN`. Theo `AGENTS.md`, chưa được sửa source, cấu hình hay cấu trúc dữ liệu trước khi người dùng duyệt plan này.
- **Danh sách task:** [`REFACTOR-TASKS.md`](REFACTOR-TASKS.md)

---

## 1. Mục tiêu

1. Tách `MainWindow.xaml.cs` (1086 dòng) thành các thành phần nhỏ, có ranh giới rõ và test được mà không cần mở cửa sổ WPF.
2. Chia project thành các lớp: Core / Imaging / Platform / UI / Benchmark. Dependency chỉ đi một chiều.
3. Thay 69 test kiểu source-presence (grep chuỗi trong source) bằng test hành vi. Gộp hai bộ test đang trùng nhau.
4. Giữ các bất biến an toàn và hiệu năng trong `outputs/APP-MECHANISMS-VI.md`: cache key theo fingerprint, generation guard, Recycle Bin, journal, `FileShare.Delete`, advance-before-action.
5. Tạo nền cho các tối ưu theo 4 nguyên tắc của `AGENTS.md`: đọc đĩa ít nhất, dùng nhiều RAM, review nhanh, giữ chất lượng ảnh cao. Ví dụ: tầng RAM chứa byte nguồn và chính sách RAM ước lượng theo kích thước đã decode.
6. Có CI và quality gate để các lần refactor sau không làm hỏng hành vi.

**Không phải mục tiêu của đợt này:** thêm tính năng người dùng mới, đổi UX, hỗ trợ đa nền tảng.

---

## 2. Hiện trạng

### 2.1 Số liệu

| Hạng mục | Giá trị |
|---|---|
| Tổng code và tài liệu có track | ~8.900 dòng, 93 file |
| Project | `PhotoReview.App` (WinExe, WPF và WinForms), `PhotoReview.Tests` (console runner tự viết, 147 `Check(...)`), `PhotoReview.Tests.Unit` (xUnit, 188 Fact/Theory) |
| File lớn nhất | `MainWindow.xaml.cs` 1086 dòng, `PhotoReview.Tests/Program.cs` 627 dòng, `ServiceBehaviorTests.cs` 419 dòng, `SourcePresenceTests.cs` 404 dòng |
| Namespace | Mọi thứ nằm trong `PhotoReview.App`, không có thư mục con |
| CI | Chưa có (`.github/` không tồn tại) |
| Quản lý build | Chỉ có `Directory.Build.props` (Nullable, ImplicitUsings). Chưa có `global.json`, `.editorconfig`, `Directory.Packages.props`, analyzer |

### 2.2 Bản đồ thành phần

```text
App.xaml.cs ──► MainWindow (UI + điều phối + nghiệp vụ + I/O)
                 ├─ LoadFolderAsync        : scan, sort, Explorer snapshot, generation guard
                 ├─ ShowImageAsync         : cache key, thumbnail, decode, compare, hash, status, session save
                 ├─ Window_KeyDown         : ánh xạ phím → lệnh (chuỗi if tuần tự)
                 ├─ ClassifyCurrentAsync   : Recycle (+ nhánh Move "Folder2" không còn được gọi)
                 ├─ ExecuteActionAsync     : Move/Copy + journal + undo
                 ├─ RemoveDuplicatesAsync  : hash batch + Recycle
                 ├─ Undo*                  : move stack + Recycle Bin restore
                 └─ zoom/fit/fullscreen, sibling folder, compare selection
Services (static hoặc instance, cùng namespace):
  PreviewImageService, PreloadScheduler, ThumbnailCache, DiskCacheStore(static),
  BoundedLruCache, ImageCacheKey, FileHashService, ExplorerOrderService(+COM),
  OperationJournal, RecoveryRetryService, RecycleBinRestoreService, SessionStore,
  AppSettings(+validation, migrate, I/O), AppLog(static), ReviewMetrics, PhysicalMemory,
  ImageSortService, SiblingFolderService, ComparePairService, DragDropInputService,
  InstanceLock, WindowPlacementService, AdaptivePreviewPolicy, PreloadOrderService
Benchmark* (Engine, Executor, Models, Profiles, WorkloadRunner, Window) nằm trong app production.
```

### 2.3 Vấn đề chính (xếp theo mức ảnh hưởng)

| # | Vấn đề | Bằng chứng | Hệ quả |
|---|---|---|---|
| P1 | **God class `MainWindow`** | 20+ field trạng thái (`_files`, `_index`, 3 bộ đếm generation, `_moveHistory`, `_lastUndoAction`, `_session`, `_fileActionInProgress`…). UI, nghiệp vụ và I/O trộn trong cùng method | Không test được hành vi. Race giữa các generation chỉ được bảo vệ bằng comment |
| P2 | **Test khóa vào chuỗi source** | 69 test trong `SourcePresenceTests` và nhiều `mainWindow.Contains(...)` trong `Program.cs` | Đổi tên hoặc tách method là test đỏ dù hành vi không đổi. Đây là rào cản lớn nhất khi refactor |
| P3 | **Hai bộ test trùng nhau** | 5 file giống gần hết giữa `PhotoReview.Tests` và `PhotoReview.Tests.Unit`; CLI runner vừa là test vừa là benchmark CLI vừa là probe | Duy trì hai lần. Không rõ đâu là nguồn chuẩn |
| P4 | **Stringly-typed** | `LoadingMode`, `ImageSortMode`, `InitialViewMode`, `ReviewAction.Operation`, `JournalEntry.Type/State` đều là `string`, so sánh bằng `OrdinalIgnoreCase` ở nhiều nơi. `IsValidImageSortMode` lặp `"Name"` | Dễ gõ sai, logic normalize bị rải rác |
| P5 | **Đường dẫn dữ liệu không nhất quán** | `PHOTOREVIEW_DATA_ROOT` được tự tính ở 3 nơi (Journal, Session, AppLog với root khác nhau). `AppSettings`, cache preview và thumbnail luôn dùng `LocalAppData`, bỏ qua biến này | Test có thể đụng config thật. Khó cô lập dữ liệu |
| P6 | **Static và global state** | `AppLog`, `DiskCacheStore`, `AppSettings.Load()` là static. `PreloadScheduler` gọi `Dispatcher.Yield` | Test phải tắt chạy song song (`DisableTestParallelization`). Không inject được |
| P7 | **I/O đồng bộ trên UI thread theo từng ảnh** | `_sessionStore.Save` gọi sau mỗi lần present. `SetZoom` gọi `new FileInfo().Length`. `_journal.ReadCommittedMoves()` đọc toàn bộ journal khi khởi động | Trái nguyên tắc 1 và 3 của AGENTS. Journal lớn dần theo thời gian vì không compaction |
| P8 | **Đọc nguồn nhiều lần** | Preview mode: thumbnail decode, rồi adaptive decode, rồi đọc header dimension, rồi hash (nếu compare). Mỗi bước mở file riêng | Trái nguyên tắc 1 |
| P9 | **Chính sách RAM lệch đơn vị** | `FullFolderRamThresholdBytes = ImageCacheCapacityBytes` (16 GB **decoded**) được so với `_totalSourceBytes` (byte **nén**) | 16 GB JPEG decode ra lớn hơn nhiều, nên quyết định "preload toàn folder" có thể sai |
| P10 | **Benchmark nằm trong binary production** | 6 file `Benchmark*` và `BenchmarkWindow` | Binary to hơn. Code đo lường lẫn với code sản phẩm |
| P11 | **Phụ thuộc WinForms chỉ để chọn folder** | `UseWindowsForms=true` dùng cho `FolderBrowserDialog`. Vì vậy phải viết `System.Windows.MessageBox`, `System.Windows.DragEventArgs`… | Namespace bị nhập nhằng. .NET 8+ đã có `Microsoft.Win32.OpenFolderDialog` |
| P12 | **Code chết và thiếu nhất quán** | Nhánh `else` trong `ClassifyCurrentAsync` (Folder2) không còn đường gọi. `ShortcutMappings.MoveToFolder2` là alias legacy. `UndoLastMoveAsync` so `Destination` phân biệt hoa thường. `ExecuteActionAsync` có thể `Insert(-1)` khi `sourceIndex < 0` | Rủi ro bug tiềm ẩn |
| P13 | **Quy trình trong AGENTS mâu thuẫn với thực tế** | AGENTS yêu cầu `git push origin master`, nhưng lịch sử dùng feature branch và PR (#1, #2) | Agent có thể push thẳng master |

### 2.4 Điểm mạnh cần giữ

- Các bất biến cache/lifecycle đã được suy nghĩ kỹ: epoch, in-flight dedup bằng `Lazy` và `GetOrAdd`, kiểm tra fingerprint sau decode, persist queue có giới hạn, prune được gộp.
- Journal theo mô hình Prepared/Committed/Failed, reconcile khi khởi động, Recycle Bin thay cho xóa thật.
- Explorer COM chạy trên một STA pump, có timeout và fallback.
- Nhiều logic thuần đã được tách thành service nhỏ: sort, sibling, compare pair, drag-drop, preload order.

---

## 3. Các hướng tái cấu trúc

### Hướng A — Refactor tại chỗ (1 project, chia thư mục và namespace)

- Giữ nguyên `PhotoReview.App`. Chia thư mục `Core/`, `Imaging/`, `Platform/`, `Ui/`, `Benchmark/`. Tách `MainWindow` thành controller dạng partial hoặc lớp thường.
- **Ưu:** rủi ro thấp, ít thay đổi build và publish.
- **Nhược:** không có gì chặn dependency sai chiều. Core vẫn phụ thuộc WPF, nên test vẫn cần `net10.0-windows`. Benchmark vẫn nằm trong app.
- **Công sức:** khoảng 3–5 ngày agent.

### Hướng B — Kiến trúc phân lớp nhiều project và MVVM (**Khuyến nghị**)

```text
src/
  PhotoReview.Core               net10.0            (không WPF, không Win32)
  PhotoReview.Imaging            net10.0-windows    (WPF Media/Imaging, không Window)
  PhotoReview.Platform.Windows   net10.0-windows    (COM Explorer, Recycle Bin, memory, mutex, placement)
  PhotoReview.App                net10.0-windows    (WinExe: Views, ViewModels, composition root)
  PhotoReview.Benchmarking       net10.0-windows    (engine, profiles, workloads, executor)
tools/
  PhotoReview.Benchmark.Cli      net10.0-windows    (Exe: --benchmark*, --preload-bench, probes)
tests/
  PhotoReview.Core.Tests         net10.0            (xUnit, chạy song song)
  PhotoReview.Imaging.Tests      net10.0-windows
  PhotoReview.App.Tests          net10.0-windows    (ViewModel + controller với fake)
  PhotoReview.Integration.Tests  net10.0-windows    (filesystem thật, Explorer, Recycle Bin; Trait=Integration)
```

Chiều phụ thuộc (bắt buộc, có test kiến trúc kiểm tra):

```text
App ──► Imaging ──► Core
 │  └─► Platform.Windows ──► Core
 └─► Benchmarking (tùy chọn, xem R-5) ──► Imaging, Core
Benchmark.Cli ──► Benchmarking
```

- **Ưu:** ranh giới được compiler đảm bảo. Core test được nhanh trên `net10.0`. App production có thể bỏ Benchmark. ViewModel test được. Mở đường cho Hướng C sau này.
- **Nhược:** đổi đường dẫn project nên phải cập nhật `AGENTS.md`, `README.md`, `tools/*.ps1` và đường dẫn publish mặc định. Công sức lớn hơn.
- **Công sức:** khoảng 8–12 ngày agent, chia 7 wave, nhiều task chạy song song được.

### Hướng C — Viết lại toàn bộ (đánh giá, chưa khuyến nghị làm ngay)

| Phương án | Lợi | Hại | Đánh giá |
|---|---|---|---|
| **C1. WinUI 3 + Win2D** (GPU decode/render) | Pipeline GPU, decode WIC nhanh, UI hiện đại | Mất WPF interop, packaging phức tạp hơn, viết lại toàn bộ UI, đường COM Explorer giữ nguyên | Chỉ nên cân nhắc nếu benchmark cho thấy WPF render/assign là nút thắt |
| **C2. Avalonia + SkiaSharp** | Đa nền tảng, Skia decode nhanh | Tính năng lõi là Explorer order và Recycle Bin, vốn chỉ có trên Windows, nên đa nền tảng không có giá trị | Không khuyến nghị |
| **C3. Giữ WPF, thay decode backend** (libjpeg-turbo, NetVips, hoặc WIC trực tiếp + `WriteableBitmap`) | Tăng tốc decode (nút thắt thực tế của review), không phải viết lại UI | Thêm native dependency, cần benchmark chứng minh | **Đáng làm dưới dạng spike** sau khi xong Hướng B, vì decode backend đã có interface |
| **C4. Native C++ (Win32/D2D)** | Hiệu năng tối đa | Chi phí rất lớn, mất toàn bộ code và test hiện có | Không khuyến nghị |

**Kết luận:** làm **Hướng B**, đặt interface `IImageDecoder` ở tầng Imaging để có thể chạy spike **C3** (R-7) mà không phải viết lại. Hướng C1 chỉ được mở lại qua ADR nếu số liệu benchmark chứng minh cần.

---

## 4. Kiến trúc đích (Hướng B)

### 4.1 PhotoReview.Core (không phụ thuộc UI)

```text
Core/
  Abstractions/  IFileSystem, IClock, IAppPaths, IMemoryProbe, IRecycleBin,
                 IExplorerOrderProvider, ILog, IUiScheduler (Yield), IDialogService
  Settings/      AppSettings (POCO + enum), SettingsStore (I/O, migrate, validate),
                 ShortcutMappings, ReviewAction, KeyName (string đã validate, không dùng WPF Key)
  Model/         LoadingMode, ImageSortMode, InitialViewMode, FileOperationType,
                 JournalState (enum + JsonStringEnumConverter giữ tương thích file cũ)
  Catalog/       ReviewCatalog (files, index, Next/Prev/First/Remove/Insert/Reorder),
                 GenerationClock (Navigation / Folder / Interaction), ImageSortService,
                 SiblingFolderService, ComparePairService, DragDropInputService
  FileActions/   FileActionService (Move/Copy/Recycle + journal + verify),
                 UndoService (move stack + recycle restore), DuplicateFinder (size → hash),
                 OperationJournal (+ compaction), RecoveryRetryService
  Session/       SessionStore (+ debounce writer)
  Diagnostics/   ReviewMetrics, AppLog (instance, ILog), BenchmarkStatistics
  Caching/       BoundedLruCache
```

> Về `KeyName`: Core không được tham chiếu `System.Windows.Input.Key`. Validate phím dùng một interface `IKeyNameValidator` do App cung cấp, hoặc danh sách tên phím tĩnh trong Core. App ánh xạ sang `Key` ở biên.

### 4.2 PhotoReview.Imaging

```text
Imaging/
  ImageCacheKey, AdaptivePreviewPolicy, PreloadOrderService
  Decoding/   IImageDecoder (WPF WIC mặc định), DecodeResult (bitmap, downscaled, bytesRead)
  Caching/    PreviewImageService, ThumbnailCache, DiskCacheStore (instance, 1 instance/directory),
              SourceBytesCache (mới, R-4: tầng RAM chứa byte nguồn đã đọc)
  Preload/    PreloadScheduler (dùng IUiScheduler thay cho Dispatcher.Yield), RamBudgetPolicy (R-4)
  Hashing/    FileHashService (dùng lại SourceBytesCache nếu có)
```

### 4.3 PhotoReview.Platform.Windows

`ExplorerOrderService` + `ExplorerComInterop` + `ExplorerSnapshotValidator`, `RecycleBin` (xóa và khôi phục, triển khai `IRecycleBin`), `PhysicalMemory` (`IMemoryProbe`), `InstanceLock`, `WindowPlacementService`, `AppPaths` (một nơi duy nhất đọc `PHOTOREVIEW_DATA_ROOT`).

### 4.4 PhotoReview.App (MVVM)

```text
App/
  App.xaml(.cs)           composition root (Microsoft.Extensions.DependencyInjection)
  Views/                  MainWindow, SettingsWindow, ActionProfilesWindow, RecoveryWindow,
                          DiagnosticsWindow, BatchReviewWindow, (BenchmarkWindow nếu giữ)
  ViewModels/             MainViewModel (điều phối, không có I/O trực tiếp),
                          ViewerState (zoom/fit/fullscreen), CompareViewModel, StatusFormatter
  Coordinators/           FolderLoadCoordinator (scan → provisional → Explorer reorder),
                          ImagePresenter (ShowImage pipeline + generation guard)
  Input/                  ShortcutRouter (bảng Key → Command, thay chuỗi if)
  Services/               DialogService, FolderPicker (OpenFolderDialog)
```

- Dùng `CommunityToolkit.Mvvm` (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand`). Chỉ giữ code-behind cho thao tác thuần view như ScrollViewer, DPI, placement.
- Bỏ `UseWindowsForms` sau khi thay `FolderBrowserDialog` bằng `OpenFolderDialog`.

### 4.5 Bất biến bắt buộc giữ (mỗi task phải có test bảo vệ)

| ID | Bất biến | Test hiện có / cần thêm |
|---|---|---|
| INV-1 | Cache key gồm path chuẩn hóa, length, mtime, mode, width. Không present pixel cũ khi source đổi | `ImageCacheKeyTests`, `PreviewImageServiceTests` |
| INV-2 | Clear/evict tăng epoch. In-flight hoặc persist cũ không làm entry sống lại | `PreviewImageServiceTests`, `DiskCacheStoreTests` |
| INV-3 | Advance đúng một lần **trước** thao tác filesystem, không `ShowImage` sau khi action xong | Hiện chỉ có source-presence, **cần test hành vi** trên `MainViewModel` + fake FS |
| INV-4 | Chỉ một file action chạy tại một thời điểm | Hiện chỉ có source-presence, **cần test hành vi** |
| INV-5 | Hoàn tất action sau khi đã đổi folder: không đăng ký undo, không sửa catalog | Hiện chỉ có comment, **cần test hành vi** |
| INV-6 | Delete đi vào Recycle Bin. Journal ghi Prepared → Committed/Failed | `OperationJournalTests`, smoke script |
| INV-7 | Explorer snapshot bị bỏ qua nếu catalog đã có tương tác hoặc tập file đổi | **Cần test hành vi** trên `FolderLoadCoordinator` với fake provider |
| INV-8 | File handle đọc dùng `FileShare.ReadWrite \| FileShare.Delete` và `SequentialScan` | `FileActionConcurrencyTests` + test cho `IImageDecoder` |
| INV-9 | Mở file trực tiếp thì chờ Explorer snapshot trước frame đầu. Mở folder thì present fallback trước | **Cần test hành vi** |
| INV-10 | Logging mặc định tắt. Khi tắt không tạo file hay thư mục | `AppLogTests` |
| INV-11 | Config hỏng thì backup rồi reset. Config bị khóa thì dùng default trong RAM, không ghi đè | `AppSettingsTests` |

---

## 5. Lộ trình (wave)

| Wave | Nội dung | Task | Chạy song song |
|---|---|---|---|
| W0 | Baseline và lưới an toàn: tag, benchmark baseline, CI, `global.json`, `.editorconfig`, CPM, sửa AGENTS | T00–T05 | T01–T04 song song |
| W1 | Gộp test, test hành vi cho INV-3/4/5/7/9 trên code **hiện tại** | T10–T14 | T11, T12, T13 song song |
| W2 | Tách Core: enum, AppPaths, abstraction, di chuyển logic thuần | T20–T26 | T21, T22, T23 song song sau T20 |
| W3 | Tách Imaging và Platform, bỏ static | T30–T35 | T31, T32, T33 song song |
| W4 | Chia nhỏ `MainWindow` thành MVVM | T40–T47 | T41, T42, T43 song song sau T40 |
| W5 | Tách Benchmark, gỡ WinForms, dọn code chết | T50–T53 | T50, T51, T52 song song |
| W6 | Tối ưu hiệu năng theo AGENTS (R-1…R-6) | T60–T66 | T61–T64 song song |
| W7 | Spike viết lại hoặc đổi decoder (ADR), tài liệu, release | T70–T74 | T70, T71 song song |

Chi tiết từng task (file, bước, tiêu chí hoàn thành, kiểm thử, agent, đầu ra) nằm ở [`REFACTOR-TASKS.md`](REFACTOR-TASKS.md).

---

## 6. Đề xuất cải tiến hiệu năng (R-x, thuộc W6)

| ID | Đề xuất | Lý do (AGENTS) | Cách đo |
|---|---|---|---|
| R-1 | Lưu session có debounce (ví dụ 500 ms, ghi trên worker) thay vì ghi đồng bộ mỗi lần present | Nguyên tắc 1, 3 | Số lần ghi `Sessions/*.json` trên 100 lần Next; P95 key→present |
| R-2 | Journal compaction và index các move đã commit (hoặc file snapshot riêng) để khởi động không phải đọc toàn bộ lịch sử | Nguyên tắc 1, 3 | Thời gian khởi động với journal 100k dòng |
| R-3 | Bỏ `FileInfo` trên UI thread (`SetZoom`, status). Dùng metadata đã có trong catalog entry | Nguyên tắc 3 | Số lần stat mỗi lần navigation |
| R-4 | **Tầng RAM hai cấp:** `SourceBytesCache` (byte nén, dùng lại cho thumbnail, adaptive, dimension, hash) + `RamBudgetPolicy` ước lượng **decoded** = w×h×4 từ header. Nếu tổng nguồn < 16 GB thì nạp toàn folder vào tầng byte | Nguyên tắc 1, 2 (đúng yêu cầu "folder < 16 GB thì load toàn bộ lên RAM") | Số lần đọc nguồn và byte đọc mỗi ảnh (mục tiêu ≤ 1), RAM peak, cold/warm P95 |
| R-5 | Tách benchmark khỏi binary production (hoặc bật qua build flag) | Giảm kích thước và JIT khi khởi động | Kích thước publish, thời gian khởi động |
| R-6 | Cache catalog entry (path, length, mtime, dimension) thay cho `List<string>` | Nguyên tắc 1, 3 | Số stat, thời gian scan |
| R-7 | Spike `IImageDecoder` thay thế (libjpeg-turbo / NetVips / WIC trực tiếp) | Nguyên tắc 3, 4 | Decode ms theo profile, so pixel với WIC |

Mọi R-x chỉ được coi là "nhanh hơn" khi có benchmark theo quy trình ở mục "Giới hạn diễn giải và benchmark" trong `APP-MECHANISMS-VI.md`, so với baseline T01.

---

## 7. Mô hình làm việc multi-agent

### 7.1 Vai trò

| Agent | Trách nhiệm |
|---|---|
| **Coordinator** | Giữ `REFACTOR-TASKS.md` và `task_on_progress.md`, giao task theo wave, kiểm tra dependency, không tự viết code |
| **Worker** | Làm một task trong **git worktree riêng**, branch `refactor/<task-id>-<slug>`. Chỉ sửa các file task đó liệt kê |
| **Reviewer** | Review diff của worker: bất biến INV, tiêu chí hoàn thành, không lan sang phạm vi task khác |
| **Integrator** | Merge branch theo thứ tự dependency vào branch tích hợp `refactor/integration`, chạy `tools/verify-all.ps1`, xử lý conflict |

### 7.2 Quy tắc

1. Mỗi worker trả về: (a) branch đã commit, (b) **handoff note** ghi vào mục "Nhật ký" của task trong `REFACTOR-TASKS.md` gồm file đã sửa, lệnh test đã chạy và kết quả, việc còn lại, (c) cập nhật trạng thái `TODO → IN PROGRESS → DONE/BLOCKED`.
2. Các task song song trong cùng wave **không được sửa chung file**. Nếu buộc phải sửa chung, task đánh dấu `Song song: Không` hoặc ghi rõ file nào do ai sở hữu.
3. Integrator merge theo thứ tự ID tăng dần trong wave. Khi conflict, ưu tiên phiên bản của task có ID nhỏ hơn rồi rebase task sau.
4. Kết thúc mỗi wave: chạy `tools/verify-all.ps1`, lệnh CLI/xUnit, publish vào thư mục mặc định, và mở PR `refactor/integration → master`.
5. Nếu phạm vi thay đổi so với plan: đặt task sang `BLOCKED`, ghi lý do, **chờ người dùng xác nhận lại** (theo AGENTS).

### 7.3 Quy ước commit và branch

- Branch: `refactor/T21-core-enums`. Commit: `refactor(core): introduce LoadingMode enum (T21)`.
- Không push thẳng `master`. Chỉ vào `master` qua PR (xem T05 về việc sửa AGENTS).

---

## 8. Kiểm thử

| Cấp | Nội dung | Lệnh |
|---|---|---|
| Unit | Core, Imaging, ViewModel (fake FS/clock/explorer) | `dotnet test PhotoReview.slnx -c Release --filter "Category!=Integration&Category!=Manual"` |
| Integration | Filesystem thật, journal, Recycle Bin, Explorer probe | `dotnet test ... --filter Category=Integration` |
| Kiến trúc | Chiều dependency giữa assembly (NetArchTest hoặc test reflection tự viết) | nằm trong unit |
| Script | `tools/smoke-test.ps1`, `tools/fault-injection-test.ps1`, `tools/verify-release.ps1` | `tools/verify-all.ps1` |
| Benchmark | Profile `recommended-auto`, `--benchmark-all` trên fixture cố định | `dotnet run --project tools/PhotoReview.Benchmark.Cli -c Release -- --benchmark-all <folder> <out>` |
| GUI acceptance (thủ công) | Checklist ở T73: mở file/folder, Next/Prev, Move/Copy/Delete/Undo, Compare, Recovery, Settings | thủ công, ghi kết quả vào task |

**Parity gate (W1 → W5):** trước khi xóa `PhotoReview.Tests`, mỗi dòng trong 147 `Check(...)` phải được ánh xạ sang một xUnit test (hoặc ghi lý do bỏ) trong `docs/refactoring/test-parity.md`.

---

## 9. Rủi ro và rollback

| Rủi ro | Mức | Giảm thiểu | Rollback |
|---|---|---|---|
| Hồi quy race/generation khi tách `MainWindow` | Cao | Viết test hành vi INV-3/4/5/7/9 **trước** (W1) trên code hiện tại, rồi mới tách | Revert merge commit của wave. Tag `pre-refactor-baseline` |
| Đổi đường dẫn project làm hỏng script, publish, file association | Trung bình | T24 cập nhật đồng loạt `tools/*.ps1`, README, AGENTS. `verify-release.ps1` là gate | Giữ đường dẫn publish cũ qua property `PublishDir` cho tới khi người dùng xác nhận |
| Đổi kiểu `string → enum` làm hỏng `config.json` / `operations.jsonl` cũ | Trung bình | `JsonStringEnumConverter` + converter chấp nhận mọi giá trị legacy. Test đọc fixture config và journal cũ | Converter fallback về default; backup file cũ |
| Tầng RAM mới (R-4) gây áp lực bộ nhớ | Trung bình | Kiểm tra headroom, giới hạn cứng, feature flag trong settings | Tắt flag |
| Hiệu năng giảm do thêm indirection hoặc DI | Thấp | Benchmark so với baseline T01 cuối mỗi wave (≤ 5% P95) | Revert task gây giảm |
| Worker song song conflict | Trung bình | Phân chia sở hữu file, merge theo thứ tự | Rebase task sau |
| Mất tính năng khi xóa `PhotoReview.Tests` | Trung bình | Parity gate (mục 8) | Khôi phục project từ tag |

**Rollback tổng:** mọi thay đổi đi qua `refactor/integration` và PR. `master` luôn giữ bản chạy được. Tag `pre-refactor-baseline` trỏ vào `86282cd`.

---

## 10. Tiêu chí hoàn thành toàn đợt

- [ ] `MainWindow.xaml.cs` ≤ 250 dòng, chỉ còn logic view. `MainViewModel` + coordinator có test hành vi cho INV-3/4/5/7/9.
- [ ] Không còn test source-presence, hoặc ≤ 5 test cho wiring XAML và ghi rõ lý do.
- [ ] Chỉ còn một hệ test xUnit. `PhotoReview.Tests` đã được thay bằng `PhotoReview.Benchmark.Cli`. Parity doc hoàn chỉnh.
- [ ] Test kiến trúc xác nhận chiều dependency. `PhotoReview.Core` build được trên `net10.0` thuần.
- [ ] Không còn `UseWindowsForms`. Không còn đọc `PHOTOREVIEW_DATA_ROOT` ngoài `AppPaths`.
- [ ] CI GitHub Actions xanh: build, unit test, publish, verify-release.
- [ ] Benchmark `--benchmark-all` không chậm hơn baseline quá 5% P95. R-4 có số liệu số lần đọc nguồn mỗi ảnh.
- [ ] `README.md`, `AGENTS.md`, `APP-MECHANISMS-VI.md`, `task_on_progress.md` đã cập nhật.
- [ ] Publish thành công vào đường dẫn mặc định đã thống nhất. PR được merge vào `master`.

---

## 11. Câu hỏi cần người dùng quyết định trước khi bắt đầu

1. **Chọn hướng:** B (khuyến nghị) / A / C?
2. **Benchmark trong app:** giữ `BenchmarkWindow` trong app (tham chiếu `PhotoReview.Benchmarking`) hay chỉ giữ CLI?
3. **Đường dẫn publish:** chấp nhận `src/PhotoReview.App/bin/Release/net10.0-windows/publish` (sau khi chuyển vào `src/`) hay giữ nguyên thư mục gốc `PhotoReview.App/`?
4. **Quy trình git:** đổi AGENTS từ "push master" sang "PR vào master" (T05)?
5. **R-4 (tầng byte RAM):** bật mặc định hay qua setting?
6. **Thư viện ngoài:** đồng ý thêm `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `NetArchTest.Rules` (chỉ trong test)?
