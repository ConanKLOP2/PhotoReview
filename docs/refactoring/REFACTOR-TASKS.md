# PhotoReview — Danh sách task tái cấu trúc

- **Plan gốc:** [`REFACTOR-PLAN.md`](REFACTOR-PLAN.md) (Hướng B: kiến trúc phân lớp + MVVM)
- **Cập nhật:** 2026-09-16
- **Trạng thái chung:** `CHỜ XÁC NHẬN PLAN`. Chưa task nào được phép bắt đầu.
- **Trạng thái hợp lệ:** `TODO` · `IN PROGRESS` · `BLOCKED` · `DONE`

## Cách dùng file này (cho mọi agent)

1. Đọc `AGENTS.md`, `task_on_progress.md`, `REFACTOR-PLAN.md` (mục 4.5 Bất biến, mục 7 Multi-agent).
2. Chỉ nhận task có `Trạng thái: TODO` và mọi task trong `Phụ thuộc` đã `DONE`.
3. Đổi trạng thái sang `IN PROGRESS` và ghi tên agent vào dòng `Agent`.
4. Làm việc trong worktree và branch `refactor/<ID>-<slug>`. Chỉ sửa các file nằm trong **Phạm vi file**.
5. Khi xong: chạy **Kiểm thử**, ghi **Nhật ký** (file đã sửa, lệnh, kết quả, việc còn lại), rồi đổi trạng thái sang `DONE`. Nếu phạm vi lệch plan thì đổi sang `BLOCKED`, ghi lý do và chờ người dùng.
6. Lệnh verify chuẩn (gọi tắt **VERIFY** bên dưới):
   ```powershell
   dotnet build PhotoReview.slnx -c Release
   dotnet test PhotoReview.slnx -c Release
   dotnet run --project PhotoReview.Tests -c Release   # cho tới khi T53 xóa project này
   .\tools\verify-all.ps1
   ```
   Sau T24, đường dẫn đổi theo layout `src/`, `tests/`, `tools/`. Dùng lệnh trong README mới.

### Bảng tổng quan

| ID | Tên | Wave | Phụ thuộc | Song song | Trạng thái |
|---|---|---|---|---|---|
| T00 | Tag baseline + branch tích hợp | W0 | — | Không (Coordinator) | TODO |
| T01 | Benchmark baseline | W0 | T00 | Có | TODO |
| T02 | CI GitHub Actions | W0 | T00 | Có | TODO |
| T03 | `global.json`, `.editorconfig`, analyzer | W0 | T00 | Có | TODO |
| T04 | Central Package Management | W0 | T00 | Có | TODO |
| T05 | Cập nhật quy trình git trong AGENTS | W0 | T00 | Có (chỉ docs) | TODO |
| T10 | Ma trận parity test | W1 | T00 | Không | TODO |
| T11 | Chuyển check CLI còn thiếu sang xUnit | W1 | T10 | Có | TODO |
| T12 | Xóa file test trùng trong `PhotoReview.Tests` | W1 | T10 | Có | TODO |
| T13 | Gắn Trait Integration/Manual, bật chạy song song có kiểm soát | W1 | T10 | Có | TODO |
| T14 | Test hành vi INV-3/4/5/7/9 (qua seam tối thiểu) | W1 | T11 | Không | TODO |
| T20 | Tạo project `PhotoReview.Core` + test project | W2 | W1 xong | Không | TODO |
| T21 | Enum hóa mode, sort, view, operation, journal state | W2 | T20 | Có | TODO |
| T22 | `AppPaths` + `IFileSystem`/`IClock`/`ILog` abstraction | W2 | T20 | Có | TODO |
| T23 | Di chuyển service thuần sang Core | W2 | T20 | Có | TODO |
| T24 | Chuyển layout sang `src/`, `tests/`, `tools/` | W2 | T21, T22, T23 | Không | TODO |
| T25 | `SettingsStore` tách I/O khỏi `AppSettings` | W2 | T21, T22 | Không | TODO |
| T26 | `OperationJournal`/`SessionStore`/`RecoveryRetry` sang Core qua abstraction | W2 | T22, T25 | Không | TODO |
| T30 | Tạo `PhotoReview.Imaging` + `PhotoReview.Platform.Windows` | W3 | W2 xong | Không | TODO |
| T31 | `DiskCacheStore` thành instance, `IImageDecoder` | W3 | T30 | Có | TODO |
| T32 | `PreloadScheduler` dùng `IUiScheduler` + `IMemoryProbe` | W3 | T30 | Có | TODO |
| T33 | Explorer/RecycleBin/Memory/InstanceLock sang Platform | W3 | T30 | Có | TODO |
| T34 | `AppLog` thành instance `ILog` (giữ facade static tạm) | W3 | T31, T32, T33 | Không | TODO |
| T35 | Test kiến trúc (chiều dependency) | W3 | T34 | Không | TODO |
| T40 | Composition root DI + CommunityToolkit.Mvvm | W4 | W3 xong | Không | TODO |
| T41 | `ReviewCatalog` + `GenerationClock` | W4 | T40 | Có | TODO |
| T42 | `FileActionService` + `UndoService` + `DuplicateFinder` | W4 | T40 | Có | TODO |
| T43 | `ShortcutRouter` + `ViewerState` (zoom/fit/fullscreen) | W4 | T40 | Có | TODO |
| T44 | `FolderLoadCoordinator` | W4 | T41 | Không | TODO |
| T45 | `ImagePresenter` + `CompareViewModel` + `StatusFormatter` | W4 | T41, T44 | Không | TODO |
| T46 | `MainViewModel` + rút gọn `MainWindow` | W4 | T42, T43, T45 | Không | TODO |
| T47 | Xóa test source-presence, thay bằng test ViewModel | W4 | T46 | Không | TODO |
| T50 | `PhotoReview.Benchmarking` + `Benchmark.Cli` | W5 | W4 xong | Có | TODO |
| T51 | Bỏ WinForms (`OpenFolderDialog`) | W5 | W4 xong | Có | TODO |
| T52 | Dọn code chết và bug nhỏ (P12) | W5 | W4 xong | Có | TODO |
| T53 | Xóa `PhotoReview.Tests` sau parity gate | W5 | T50, T11 | Không | TODO |
| T60 | Mở rộng metric: đọc nguồn theo key, stat count | W6 | W5 xong | Không | TODO |
| T61 | R-1 Session save debounce | W6 | T60 | Có | TODO |
| T62 | R-2 Journal compaction/index | W6 | T60 | Có | TODO |
| T63 | R-3 + R-6 Catalog entry metadata, bỏ stat trên UI | W6 | T60 | Có | TODO |
| T64 | R-4a `RamBudgetPolicy` theo kích thước decoded | W6 | T60 | Có | TODO |
| T65 | R-4b `SourceBytesCache` (đọc nguồn một lần) | W6 | T64 | Không | TODO |
| T66 | Benchmark so sánh W6 với baseline | W6 | T61–T65 | Không | TODO |
| T70 | Spike R-7 decoder thay thế + ADR | W7 | T31 | Có | TODO |
| T71 | ADR đánh giá WinUI 3 (C1) | W7 | T66 | Có | TODO |
| T72 | Cập nhật tài liệu | W7 | T66 | Không | TODO |
| T73 | GUI acceptance thủ công | W7 | T72 | Không | TODO |
| T74 | Release: merge PR, publish, tag | W7 | T73 | Không | TODO |

### Nhóm agent theo wave (có thể chạy cùng lúc)

- **W0:** Coordinator làm T00 → 4 worker cho T01, T02, T03, T04, và T05 làm cùng lúc (T05 chỉ sửa docs).
- **W1:** 1 worker làm T10 → 3 worker cho T11, T12, T13 → 1 worker làm T14.
- **W2:** T20 → 3 worker cho T21, T22, T23 → T24 → T25 → T26 (tuần tự).
- **W3:** T30 → 3 worker cho T31, T32, T33 → T34 → T35.
- **W4:** T40 → 3 worker cho T41, T42, T43 → T44 → T45 → T46 → T47.
- **W5:** 3 worker cho T50, T51, T52 → T53.
- **W6:** T60 → 4 worker cho T61, T62, T63, T64 → T65 → T66.
- **W7:** 2 worker cho T70, T71 → T72 → T73 → T74.

---

## W0 — Baseline và lưới an toàn

### T00 — Tag baseline và branch tích hợp
- **Trạng thái:** TODO · **Agent:** Coordinator · **Song song:** Không
- **Mục tiêu:** có điểm rollback và branch để gom các wave.
- **Bước:**
  1. `git tag pre-refactor-baseline 86282cd` rồi push tag (sau khi người dùng xác nhận).
  2. `git switch -c refactor/integration master` rồi push.
  3. Cập nhật `task_on_progress.md`: mục tiêu đợt refactor, link plan.
- **Hoàn thành khi:** tag và branch tồn tại trên `origin`.
- **Đầu ra:** tên tag và branch trong nhật ký.
- **Nhật ký:** —

### T01 — Benchmark baseline
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T00
- **Phạm vi file:** chỉ thêm `docs/refactoring/baseline/` (JSON/MD). Không sửa code.
- **Bước:**
  1. Chọn fixture cố định (ghi đường dẫn, số file, tổng dung lượng, định dạng, kích thước pixel).
  2. Build Release tại `pre-refactor-baseline`.
  3. Chạy `dotnet run --project PhotoReview.Tests -c Release -- --benchmark-all <fixture> docs/refactoring/baseline/run1`, lặp 3 lần, cả cold cache (xóa `%LOCALAPPDATA%\PhotoReview\cache` và `thumbnails`) lẫn warm cache.
  4. Ghi `baseline.md`: máy, CPU, RAM, ổ đĩa, power plan, median/P95/max theo profile, source reads/bytes, RAM peak.
- **Hoàn thành khi:** `baseline.md` có đủ số liệu 3 lần chạy cho mọi profile không phải correctness-only.
- **Kiểm thử:** không áp dụng (chỉ đo).
- **Đầu ra:** thư mục baseline + tóm tắt trong nhật ký.
- **Nhật ký:** —

### T02 — CI GitHub Actions
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T00
- **Phạm vi file:** `.github/workflows/ci.yml` (mới).
- **Bước:**
  1. Job `windows-latest`: `actions/setup-dotnet` (theo `global.json` nếu T03 đã có, nếu chưa thì `10.0.x`).
  2. Các bước: restore, build Release, `dotnet run --project PhotoReview.Tests -c Release`, `dotnet test -c Release --filter "Category!=Manual"`, publish framework-dependent, `tools/verify-release.ps1`.
  3. Upload artifact thư mục publish và kết quả test (trx).
  4. Trigger: `pull_request` vào `master` và `refactor/integration`, `push` vào `master`.
- **Hoàn thành khi:** workflow chạy xanh trên PR của T02.
- **Lưu ý:** test cần Explorer/Recycle Bin thật có thể lỗi trên runner. Nếu có, ghi lại để T13 gắn Trait.
- **Nhật ký:** —

### T03 — `global.json`, `.editorconfig`, analyzer
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T00
- **Phạm vi file:** `global.json`, `.editorconfig`, `Directory.Build.props`.
- **Bước:**
  1. `global.json`: SDK `10.0.401`, `rollForward: latestFeature`.
  2. `.editorconfig`: C# style đang dùng thực tế (file-scoped namespace, `var`, collection expression, 4 spaces, CRLF theo `.gitattributes`).
  3. `Directory.Build.props`: thêm `<AnalysisLevel>latest-recommended</AnalysisLevel>`, `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` (giai đoạn đầu), `<Deterministic>true</Deterministic>`.
  4. Ghi số warning hiện có vào nhật ký làm baseline. **Không sửa warning trong task này.**
- **Hoàn thành khi:** build Release thành công, số warning đã được ghi lại.
- **Kiểm thử:** VERIFY.
- **Nhật ký:** —

### T04 — Central Package Management
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T00
- **Phạm vi file:** `Directory.Packages.props` (mới), `PhotoReview.Tests.Unit/PhotoReview.Tests.Unit.csproj`.
- **Bước:** bật `ManagePackageVersionsCentrally`, chuyển version của `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio` vào file trung tâm, xóa `Version=` khỏi csproj.
- **Hoàn thành khi:** `dotnet restore` và VERIFY đều đạt.
- **Nhật ký:** —

### T05 — Cập nhật quy trình git trong AGENTS
- **Trạng thái:** TODO · **Song song:** Có (chỉ docs) · **Phụ thuộc:** T00 + **người dùng chốt câu hỏi 4 trong plan**
- **Phạm vi file:** `AGENTS.md`.
- **Bước:** thay "push origin master" bằng quy trình feature branch, PR vào `master` và CI xanh. Thêm mục "Refactor đang chạy: xem `docs/refactoring/`".
- **Hoàn thành khi:** người dùng duyệt nội dung mới.
- **Nhật ký:** —

---

## W1 — Hợp nhất test và khóa hành vi

### T10 — Ma trận parity test
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T00
- **Phạm vi file:** `docs/refactoring/test-parity.md` (mới).
- **Bước:**
  1. Liệt kê cả 147 `Check(...)` trong `PhotoReview.Tests/Program.cs` (số dòng + mô tả).
  2. Với mỗi dòng, ghi test xUnit tương ứng (file::method) hoặc `MISSING` hoặc `DROP` (kèm lý do, ví dụ trùng hoặc chỉ là source-presence).
  3. Liệt kê 5 file trùng giữa hai project và ghi rõ khác biệt, nếu có (chạy `diff`).
  4. Phân loại 69 test trong `SourcePresenceTests`: (a) thay được bằng test hành vi ở W1/W4, (b) wiring XAML cần giữ, (c) bỏ.
- **Hoàn thành khi:** không còn dòng nào chưa được phân loại.
- **Đầu ra:** `test-parity.md`, và danh sách `MISSING` giao cho T11.
- **Nhật ký:** —

### T11 — Chuyển check CLI còn thiếu sang xUnit
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T10
- **Phạm vi file:** `PhotoReview.Tests.Unit/**` (chỉ thêm file mới `*Migrated*Tests.cs` hoặc thêm vào file hiện có không bị T13 sửa), `docs/refactoring/test-parity.md` (cột trạng thái).
- **Bước:** viết xUnit cho mọi dòng `MISSING`, giữ nguyên ngữ nghĩa assertion, dùng `TempRoot` để cô lập dữ liệu.
- **Hoàn thành khi:** `test-parity.md` không còn `MISSING`, và `dotnet test` đạt.
- **Kiểm thử:** VERIFY.
- **Nhật ký:** —

### T12 — Xóa file test trùng trong `PhotoReview.Tests`
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T10
- **Phạm vi file:** `PhotoReview.Tests/{BenchmarkScenarioTests,CacheExplorerRegressionTests,FileActionConcurrencyTests,ImageCacheKeyTests,PerformanceTestHarness}.cs`, `PhotoReview.Tests/Program.cs` (chỉ phần gọi các file trên).
- **Bước:** nếu T10 xác nhận bản xUnit đã bao phủ đủ thì xóa bản trong CLI và bỏ lời gọi tương ứng trong `Program.cs`. `LocalImageBenchmark.cs` và `LocalUiNextProbe.cs` được **giữ lại** (chuyển đi ở T50).
- **Hoàn thành khi:** CLI vẫn build và chạy đạt. Các check còn lại in PASS.
- **Kiểm thử:** VERIFY.
- **Nhật ký:** —

### T13 — Trait và chạy song song có kiểm soát
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T10
- **Phạm vi file:** `PhotoReview.Tests.Unit/TestInfrastructure.cs`, các file test cần gắn `[Trait]` (không đụng file T11 đang thêm).
- **Bước:**
  1. Gắn `[Trait("Category","Integration")]` cho test dùng filesystem thật lâu, Recycle Bin, Explorer. Gắn `Manual` cho test cần GUI hoặc Explorer mở.
  2. Tạo `[Collection("GlobalState")]` cho test đụng `AppLog` static và biến môi trường. Bỏ `DisableTestParallelization` toàn assembly nếu an toàn.
  3. Điều tra lỗi flaky "quota prune nền" (189/190) đã ghi trong `task_on_progress.md`: thêm `DiskCacheStore.WaitForPruneAsync` hoặc cô lập thư mục riêng.
- **Hoàn thành khi:** `dotnet test` chạy 5 lần liên tiếp đều đạt. Thời gian chạy được ghi trước và sau.
- **Nhật ký:** —

### T14 — Test hành vi INV-3/4/5/7/9 trên code hiện tại
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T11
- **Mục tiêu:** khóa hành vi **trước** khi tách `MainWindow`.
- **Phạm vi file:** `PhotoReview.App/MainWindow.xaml.cs` (**chỉ** được thêm seam tối thiểu: `internal` constructor hoặc hook, không đổi logic), `PhotoReview.App/AssemblyInfo.cs` (`InternalsVisibleTo`), `PhotoReview.Tests.Unit/MainWindowBehaviorTests.cs` (mới).
- **Bước:**
  1. Chạy test trên STA thread với `Application` (helper `StaTestRunner`), tạo `MainWindow` với data root tạm.
  2. INV-3: một Move trên folder 3 ảnh thì ảnh hiện tại đổi đúng một lần, trước khi file biến mất. Kiểm tra bằng thứ tự sự kiện qua hook.
  3. INV-4: gọi hai action liền nhau thì chỉ một action thực thi.
  4. INV-5: đổi folder giữa lúc Move thì không có undo entry, catalog của folder mới không bị sửa.
  5. INV-7: fake `IExplorerOrderProvider` trả snapshot chậm, người dùng Next trước đó, nên order không được áp dụng.
  6. INV-9: mở file thì frame đầu chỉ xuất hiện sau khi snapshot về. Mở folder thì present fallback trước.
- **Lưu ý:** nếu không tạo được seam mà không đổi logic thì chuyển `BLOCKED` và đề xuất dời INV sang T41–T46 (test trên thành phần mới).
- **Hoàn thành khi:** 5 nhóm test đạt và ổn định (chạy 10 lần).
- **Nhật ký:** —

---

## W2 — Tách Core

### T20 — Tạo `PhotoReview.Core` và test project
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** W1 DONE
- **Phạm vi file:** `PhotoReview.Core/PhotoReview.Core.csproj` (net10.0), `PhotoReview.Core.Tests/*.csproj`, `PhotoReview.slnx`, `PhotoReview.App.csproj` (ProjectReference).
- **Bước:** tạo project rỗng, thư mục như mục 4.1 của plan, thêm vào solution, App tham chiếu Core. Chuyển `BoundedLruCache` sang làm mẫu (namespace `PhotoReview.Core.Caching`) và cập nhật `using`.
- **Hoàn thành khi:** Core build trên `net10.0` (không `-windows`), VERIFY đạt.
- **Nhật ký:** —

### T21 — Enum hóa
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T20
- **Sở hữu file:** `PhotoReview.Core/Model/*` (mới), `AppSettings.cs`, `BenchmarkModels.cs` (chỉ `LoadingMode`), các chỗ so sánh chuỗi mode trong `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `PreviewImageService` caller. **T22 và T23 không được sửa các file này.**
- **Bước:**
  1. Tạo `LoadingMode {Fast, Preview, Original}`, `ImageSortMode {Name, SizeDescending, SizeAscending}`, `InitialViewMode {Fit, Percent100, Percent200, Percent400}`, `FileOperationType {Move, Copy, Recycle}`, `JournalState {Prepared, Committed, Failed}`.
  2. Viết `LenientEnumConverter<T>` đọc được mọi giá trị legacy (`"Size"`, `"PortraitFirst"`, `"Delete"` → Recycle, `"100%"`) và ghi ra tên chuẩn.
  3. `AppSettings`: property kiểu enum. Xóa `Normalize*` và `IsValid*` hoặc chuyển thành wrapper `[Obsolete]` nếu test cũ còn dùng.
  4. `JournalEntry.Type/State`: dùng enum + converter. **Format JSONL trên đĩa giữ nguyên chuỗi cũ.**
  5. Thêm test fixture: file `config.json` v1/v2 cũ và `operations.jsonl` cũ, đọc rồi ghi lại, so sánh tương đương.
- **Hoàn thành khi:** không còn `string.Equals(..., "Original"/"Preview"/"Fit"/"Move"…)` trong App (kiểm bằng grep), test fixture legacy đạt.
- **Kiểm thử:** VERIFY + `AppSettingsTests` + `OperationJournalTests`.
- **Rủi ro:** config người dùng. Giữ backup `.corrupt-*` như cũ.
- **Nhật ký:** —

### T22 — `AppPaths` và abstraction nền
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T20
- **Sở hữu file:** `PhotoReview.Core/Abstractions/*` (mới), `PhotoReview.Core/AppPaths.cs` (mới). **Chỉ tạo mới, không sửa caller** (việc đó thuộc T25, T26, T31–T34).
- **Bước:**
  1. `IAppPaths`: `DataRoot`, `ConfigFile`, `JournalFile`, `SessionsDir`, `LogFile`, `PreviewCacheDir`, `ThumbnailCacheDir`. Triển khai `AppPaths(string? overrideRoot)` là nơi **duy nhất** đọc `PHOTOREVIEW_DATA_ROOT`. Giữ layout đĩa hiện tại khi không có override (log ở `PhotoReview\logs`, dữ liệu ở `PhotoReview\Data`, config ở `PhotoReview\config.json`).
  2. `IFileSystem` (các thao tác đang dùng: Exists, Length/mtime, Move, Copy, OpenRead với share flags, WriteAtomic, EnumerateFiles, CreateDirectory), `PhysicalFileSystem`, `InMemoryFileSystem` (trong test project).
  3. `IClock`, `ILog`, `IUiScheduler`, `IMemoryProbe`, `IRecycleBin`, `IDialogService`, `IKeyNameValidator`.
- **Hoàn thành khi:** có unit test cho `AppPaths` (có và không có override) và `InMemoryFileSystem`.
- **Nhật ký:** —

### T23 — Di chuyển service thuần sang Core
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T20
- **Sở hữu file:** `ImageSortService.cs`, `SiblingFolderService.cs`, `ComparePairService.cs`, `DragDropInputService.cs`, `ImageFileTypes.cs`, `ReviewMetrics.cs`, `BenchmarkStatistics` (tách khỏi `BenchmarkModels.cs` **chỉ** class này), cùng test tương ứng.
- **Bước:** `git mv` sang `PhotoReview.Core/<nhóm>/`, đổi namespace, sửa `using` ở caller (chỉ dòng `using`). `ImageSortService` đang P/Invoke `StrCmpLogicalW` nên phải đặt sau interface `INaturalComparer`: bản Windows nằm ở Platform (T33), tạm thời để ở App. Core dùng bản managed làm fallback cho test.
- **Hoàn thành khi:** các file trên không còn trong `PhotoReview.App`, VERIFY đạt.
- **Nhật ký:** —

### T24 — Chuyển layout thư mục
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T21, T22, T23 + **người dùng chốt câu hỏi 3**
- **Phạm vi file:** toàn bộ đường dẫn project, `PhotoReview.slnx`, `tools/*.ps1`, `README.md`, `AGENTS.md` (lệnh), `.github/workflows/ci.yml`, `outputs/*.ps1` nếu có đường dẫn.
- **Bước:**
  1. `git mv PhotoReview.App src/PhotoReview.App`, `PhotoReview.Core` sang `src/`, test sang `tests/`. `PhotoReview.Tests` tạm để ở `tests/PhotoReview.Tests`.
  2. Cập nhật `ProjectReference`, `slnx`, script (`$root`, `$appProject`, đường dẫn publish), CI.
  3. Grep toàn repo `PhotoReview.App/` để không sót. Cập nhật `ProjectSources` trong test (đường dẫn đọc source).
- **Hoàn thành khi:** VERIFY đạt, publish ra đúng thư mục đã chốt, `verify-release.ps1` đạt.
- **Rollback:** revert một commit duy nhất (task này phải nằm gọn trong **1 commit**).
- **Nhật ký:** —

### T25 — `SettingsStore`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T21, T22
- **Phạm vi file:** `AppSettings.cs` → `Core/Settings/{AppSettings,ShortcutMappings,ReviewAction,SettingsValidator,SettingsStore}.cs`, `SettingsWindow.xaml.cs`, `ActionProfilesWindow.xaml.cs`, `App.xaml.cs`, `MainWindow.xaml.cs` (chỗ gọi `AppSettings.Load()`), test Settings.
- **Bước:**
  1. `AppSettings` chỉ còn là POCO. `SettingsStore(IAppPaths, IFileSystem, ILog)` chứa `Load/Save/Migrate` và giữ nguyên nhánh corrupt/locked.
  2. `SettingsValidator` dùng `IKeyNameValidator` (App cung cấp bản dựa trên `System.Windows.Input.Key`).
  3. Bỏ side-effect `AppLog.Enabled = ...` trong `Load/Save`. Caller tự set (hoặc `SettingsStore` phát event `Changed`).
  4. `MainWindow` nhận settings qua `SettingsStore.Current` thay vì gọi `AppSettings.Load()` lại sau mỗi dialog.
- **Hoàn thành khi:** INV-11 đạt, test không còn đụng `%LOCALAPPDATA%` thật (kiểm bằng test override `AppPaths`).
- **Nhật ký:** —

### T26 — Journal, Session, Recovery sang Core
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T22, T25
- **Phạm vi file:** `OperationJournal.cs`, `SessionStore.cs`, `RecoveryRetryService.cs`, caller trong `MainWindow`, `RecoveryWindow`, test.
- **Bước:** inject `IAppPaths`, `IFileSystem`, `IClock`. `RecoveryRetryService` thành instance. Giữ nguyên format file. Sửa `SessionStore.Load` để luôn gán `Folder` khi file JSON thiếu trường này.
- **Hoàn thành khi:** INV-6 đạt, test chạy trên `InMemoryFileSystem` (trừ test durability, gắn Integration).
- **Nhật ký:** —

---

## W3 — Imaging và Platform

### T30 — Tạo `PhotoReview.Imaging` và `PhotoReview.Platform.Windows`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** W2 DONE
- **Phạm vi file:** 2 csproj mới + 2 test project (`Imaging.Tests`, và `Integration.Tests` nếu chưa có), `slnx`, reference.
- **Bước:** `Imaging` dùng `UseWPF=true` nhưng không có Window, XAML hay `Application`. Di chuyển `ImageCacheKey`, `AdaptivePreviewPolicy`, `PreloadOrderService` (thuần) làm mẫu.
- **Hoàn thành khi:** VERIFY đạt.
- **Nhật ký:** —

### T31 — `DiskCacheStore` thành instance, thêm `IImageDecoder`
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T30
- **Sở hữu file:** `DiskCacheStore.cs`, `PreviewImageService.cs`, `ThumbnailCache.cs`, `Imaging/Decoding/*` (mới), test tương ứng.
- **Bước:**
  1. `DiskCacheStore(string directory, string pattern, long maxBytes, ILog)`: mỗi instance tự quản coalesce prune. Bỏ dictionary static. Giữ `WaitForPruneAsync`.
  2. `IImageDecoder`: `Decode(Stream/Path, targetWidth) → DecodeResult{Bitmap, Downscaled}` và `ReadDimensions`. `WicImageDecoder` giữ **nguyên** share flags, `SequentialScan`, buffer 1 MB, `OnLoad`, `Freeze` (INV-8).
  3. `PreviewImageService` và `ThumbnailCache` nhận `IImageDecoder`, `DiskCacheStore`, `IAppPaths`, `ReviewMetrics` qua constructor. Giữ nguyên epoch, `Lazy`, `GetOrAdd` và persist queue (INV-1, INV-2).
- **Hoàn thành khi:** `PreviewImageServiceTests` và `DiskCacheStoreTests` đạt không cần sửa assertion (chỉ sửa phần tạo đối tượng). Test chạy song song được.
- **Nhật ký:** —

### T32 — `PreloadScheduler` bỏ phụ thuộc Dispatcher
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T30
- **Sở hữu file:** `PreloadScheduler.cs`, `PhysicalMemory.cs` (chỉ thêm `IMemoryProbe` adapter), test preload.
- **Bước:** thay `Dispatcher.Yield` bằng `IUiScheduler.YieldAsync()` (App dùng `Dispatcher`, test dùng `Task.Yield`). Thay `Func<double,bool> hasHeadroom` và `PhysicalMemory.GetSnapshot` bằng `IMemoryProbe`. Nhận `PreloadOptions` (worker count, memory limit, reserve) thay cho `AppConstants` trực tiếp.
- **Hoàn thành khi:** test preload không cần Dispatcher, VERIFY đạt.
- **Nhật ký:** —

### T33 — Tách Platform.Windows
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T30
- **Sở hữu file:** `ExplorerOrderService.cs`, `ExplorerComInterop.cs`, `RecycleBinRestoreService.cs`, `InstanceLock.cs`, `WindowPlacementService.cs`, `PhysicalMemory.cs` (di chuyển file; T32 chỉ thêm interface ở Core), phần P/Invoke của `ImageSortService`.
- **Bước:**
  1. `git mv` sang `src/PhotoReview.Platform.Windows/`.
  2. `WindowsRecycleBin : IRecycleBin`: `SendToRecycleBin` (thay `Microsoft.VisualBasic.FileSystem.DeleteFile` bằng `IFileOperation` hoặc `SHFileOperation` với `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI`, **hoặc** giữ VisualBasic nếu muốn ít rủi ro, ghi rõ lựa chọn trong nhật ký) và `TryRestore`.
  3. `ExplorerSnapshotValidator` là logic thuần nên chuyển vào Core.
  4. `WindowPlacementService` có tham chiếu `Window`, nên giữ ở App (ghi lý do) hoặc tách phần persist sang Platform.
- **Hoàn thành khi:** smoke/fault-injection script đạt, test Explorer/RecycleBin đã gắn Integration.
- **Nhật ký:** —

### T34 — `AppLog` thành instance
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T31, T32, T33
- **Phạm vi file:** `AppLog.cs` → `Core/Diagnostics/FileLog.cs : ILog`, mọi caller.
- **Bước:** tạo `FileLog(IAppPaths)` giữ nguyên writer thread, rotation và cách flush. Giữ `AppLog` static làm facade **tạm**, trỏ tới instance mặc định để các caller UI chưa chuyển vẫn chạy. Mọi service ở Core, Imaging và Platform nhận `ILog` qua constructor. Kiểm tra `if (log.Enabled)` được giữ ở các vòng nóng.
- **Hoàn thành khi:** Core, Imaging và Platform không còn tham chiếu `AppLog` (kiểm bằng grep), INV-10 đạt.
- **Nhật ký:** —

### T35 — Test kiến trúc
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T34
- **Phạm vi file:** `tests/PhotoReview.Architecture.Tests/` (hoặc thêm vào `Core.Tests`), `Directory.Packages.props`.
- **Bước:** assert: Core không tham chiếu `PresentationCore`, `WindowsBase`, `System.Windows.Forms`, Imaging, Platform, App. Imaging không tham chiếu App, Platform, `PresentationFramework`. Platform không tham chiếu Imaging, App. Không type nào ngoài `AppPaths` đọc `PHOTOREVIEW_DATA_ROOT` (kiểm bằng scan IL hoặc source).
- **Hoàn thành khi:** test đạt trong CI.
- **Nhật ký:** —

---

## W4 — Chia nhỏ `MainWindow` (MVVM)

> Sau W3, `MainWindow` vẫn chứa toàn bộ điều phối. W4 tách nó thành các thành phần test được. Mỗi task phải **chuyển test INV tương ứng từ T14 sang thành phần mới**.

### T40 — Composition root
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** W3 DONE + người dùng chốt câu hỏi 6
- **Phạm vi file:** `App.xaml`, `App.xaml.cs`, `Directory.Packages.props`, `PhotoReview.App.csproj`, `MainWindow.xaml.cs` (constructor).
- **Bước:** thêm `Microsoft.Extensions.DependencyInjection` và `CommunityToolkit.Mvvm`. Đăng ký singleton cho các service W2/W3. Bỏ `StartupUri` nếu có. `MainWindow` nhận dependency qua constructor. Giữ logic khởi động: instance lock, unhandled exception handler, parse arg.
- **Hoàn thành khi:** app khởi động và mở folder bình thường (kiểm thủ công nhanh + VERIFY).
- **Nhật ký:** —

### T41 — `ReviewCatalog` và `GenerationClock`
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T40
- **Sở hữu file:** `Core/Catalog/ReviewCatalog.cs`, `Core/Catalog/GenerationClock.cs` (mới), test. **Không sửa `MainWindow`** (T46 sẽ nối vào).
- **Bước:**
  1. `ReviewCatalog`: danh sách entry (T63 sẽ mở rộng metadata), `CurrentIndex`, `Current`, `IndexOf` (OrdinalIgnoreCase), `Remove(path) → nextIndex`, `Restore(path, index)` (clamp `[0, Count]`, sửa lỗi `Insert(-1)` của P12), `ReplaceOrder(IReadOnlyList<string>)` giữ current, `MoveToFront(path)`.
  2. `GenerationClock`: `Navigation`, `Folder`, `Interaction` với các thao tác `Next*()` và `IsCurrent(token)`, giữ ngữ nghĩa `Interlocked`/`Volatile` như code cũ.
- **Hoàn thành khi:** có test cho mọi method, kể cả biên (list rỗng, xóa phần tử cuối, index âm).
- **Nhật ký:** —

### T42 — `FileActionService`, `UndoService`, `DuplicateFinder`
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T40
- **Sở hữu file:** `Core/FileActions/*` (mới), test. **Không sửa `MainWindow`.**
- **Bước:**
  1. `FileActionService.ExecuteAsync(FileActionRequest{Source, Operation, DestinationSpec}) → FileActionResult`: validate đích (không trùng folder nguồn, đích chưa tồn tại), journal Prepared → thực thi trên worker → verify size → Committed hoặc Failed. Dùng `IFileSystem`, `IRecycleBin`, `OperationJournal`. **Không** đụng catalog hay UI.
  2. Gate "một action tại một thời điểm" (`SemaphoreSlim(1)` hoặc `Interlocked`) nằm trong service (INV-4).
  3. `UndoService`: stack move (nạp từ journal khi khởi động, xem T62), `LastAction`, `UndoAsync()` kiểm fingerprint theo journal (so `Destination` **không** phân biệt hoa thường), restore Recycle Bin.
  4. `DuplicateFinder.FindAsync(files, removeNumbered, CancellationToken) → IReadOnlyList<string>`: nhóm theo size rồi theo hash, regex ` \(\d+\)$`.
- **Hoàn thành khi:** có test cho Move/Copy/Recycle thành công, đích tồn tại, size lệch, lỗi giữa chừng, undo khi file đã đổi, undo với đường dẫn khác hoa thường, và duplicate.
- **Nhật ký:** —

### T43 — `ShortcutRouter` và `ViewerState`
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T40
- **Sở hữu file:** `App/Input/ShortcutRouter.cs`, `App/ViewModels/ViewerState.cs` (mới), test.
- **Bước:**
  1. `ShortcutRouter`: dựng `Dictionary<(Key, ModifierKeys), ReviewCommand>` từ settings mỗi khi settings đổi (thay cho `Enum.TryParse` mỗi lần nhấn phím). Giữ thứ tự ưu tiên hiện tại: Fullscreen, Esc, folder nav, First, Ctrl+Undo, Compare, Actions, Recycle, Skip, Fit, Zoom, Next/Prev. Lưu ý code cũ dùng `e.Key` ở phần lớn nhánh nhưng dùng `pressedKey` (xử lý `Key.System`) cho Fullscreen; giữ đúng hành vi này và ghi lại.
  2. `ViewerState`: `Zoom` (clamp 0.25–4, bước 0.25), `IsFit`, `IsFullscreen`, `ApplyInitialViewMode(InitialViewMode)`. View bind vào `ScaleTransform`, `Stretch`, `MaxWidth/MaxHeight`.
- **Hoàn thành khi:** có test ánh xạ cho toàn bộ default shortcut và các trường hợp xung đột.
- **Nhật ký:** —

### T44 — `FolderLoadCoordinator`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T41
- **Phạm vi file:** `App/Coordinators/FolderLoadCoordinator.cs` (mới), test.
- **Bước:** chuyển logic `LoadFolderAsync` thành các bước: cancel load cũ, scan (worker), sort, đưa initial lên đầu, reset cache, load session, (mở file thì chờ snapshot), tính tổng dung lượng, present, áp dụng Explorer order nếu hợp lệ và chưa có tương tác. Phát event hoặc callback (`CatalogReady`, `PresentRequested(index)`, `OrderApplied`, `Failed`) thay vì chạm UI trực tiếp. Dùng `IExplorerOrderProvider` (progressive).
- **Hoàn thành khi:** INV-7 và INV-9 được test trên coordinator với fake provider và `InMemoryFileSystem`. Có test folder rỗng và test đổi folder nhanh hai lần.
- **Nhật ký:** —

### T45 — `ImagePresenter`, `CompareViewModel`, `StatusFormatter`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T41, T44
- **Phạm vi file:** `App/Coordinators/ImagePresenter.cs`, `App/ViewModels/CompareViewModel.cs`, `App/ViewModels/StatusFormatter.cs` (mới), test.
- **Bước:**
  1. `ImagePresenter.ShowAsync(index)`: tăng navigation generation, kiểm tra file biến mất thì xóa khỏi catalog và chuyển tiếp, RAM hit, (Preview thì thumbnail trước), decode, present, preload, compare, dimension, session. Mỗi `await` đều kiểm tra generation. Kết quả đi ra qua property observable (`CurrentImage`, `Status`) để view bind.
  2. `CompareViewModel`: pair, selection, trạng thái size/hash (hash chạy song song).
  3. `StatusFormatter`: gom mọi chuỗi status tiếng Việt hiện có (giữ **nguyên văn**) và `FormatFileSize`.
- **Hoàn thành khi:** có test cho ảnh biến mất, thay token giữa chừng, compare kèm hash, và Preview mode có thumbnail.
- **Nhật ký:** —

### T46 — `MainViewModel` và rút gọn `MainWindow`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T42, T43, T45
- **Phạm vi file:** `App/ViewModels/MainViewModel.cs` (mới), `MainWindow.xaml`, `MainWindow.xaml.cs`, `BatchReviewWindow`, `RecoveryWindow`, `DiagnosticsWindow` (chỉ phần mở dialog qua `IDialogService`).
- **Bước:**
  1. `MainViewModel` ghép Catalog, Coordinator, Presenter, FileActionService, Undo, Duplicate, ShortcutRouter và ViewerState. Giữ nguyên thứ tự **advance-before-action**: `catalog.Remove` → `presenter.ShowAsync(next)` (không await) → `fileActions.ExecuteAsync` → nếu folder generation không đổi thì đăng ký undo, nếu lỗi thì `catalog.Restore` (INV-3, INV-5).
  2. `MainWindow.xaml` bind `Image.Source`, `StatusText`, `FolderText`, `ComparePanel.Visibility`, `ScaleTransform`. Menu/button dùng `Command`.
  3. Code-behind chỉ còn: DPI change → `vm.DpiScale`, `ImageScroll` size → `vm.ViewportSize`, KeyDown → `ShortcutRouter`, drag-drop → `vm.OpenAsync`, placement.
- **Hoàn thành khi:** `MainWindow.xaml.cs` ≤ 250 dòng. Test INV từ T14 được viết lại trên `MainViewModel` và đạt. Kiểm thủ công nhanh theo checklist T73 (mục 1–6).
- **Nhật ký:** —

### T47 — Xóa test source-presence
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T46
- **Phạm vi file:** `SourcePresenceTests.cs`, `TestInfrastructure.cs` (`ProjectSources`), test trong `ServiceBehaviorTests` có grep source, `MainWindowBehaviorTests.cs` (T14), `test-parity.md`.
- **Bước:** với mỗi test theo phân loại ở T10: (a) xóa, vì đã có test hành vi mới (ghi tên test thay thế), (b) giữ tối đa 5 test wiring XAML (ví dụ `AllowDrop`, `AutomationProperties.Name`) và đổi sang parse XAML bằng `XDocument` thay vì so chuỗi, (c) xóa. Xóa `MainWindowBehaviorTests` nếu đã có bản trên ViewModel.
- **Hoàn thành khi:** không còn `.Contains(` trên source `.cs`, `test-parity.md` đã cập nhật.
- **Nhật ký:** —

---

## W5 — Benchmark, WinForms, dọn dẹp

### T50 — Tách Benchmark
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** W4 DONE + người dùng chốt câu hỏi 2
- **Sở hữu file:** `Benchmark*.cs`, `BenchmarkWindow.*`, `tests/PhotoReview.Tests/{Program.cs,LocalImageBenchmark.cs,LocalUiNextProbe.cs,PerformanceTestHarness.cs}`, `tools/benchmark-folder.ps1`, test Benchmark.
- **Bước:**
  1. `src/PhotoReview.Benchmarking`: Engine, Models, Profiles, WorkloadRunner, ImageExecutor (dùng service thật qua DI).
  2. `tools/PhotoReview.Benchmark.Cli`: phần CLI trong `Program.cs` (`--benchmark*`, `--preload-bench`, `--ui-next-probe`, `--explorer-probe`, `--benchmark-list-profiles`) dùng `System.CommandLine` hoặc parse tay. **Giữ nguyên cú pháp tham số.**
  3. `BenchmarkWindow`: theo quyết định câu hỏi 2, hoặc giữ trong App (tham chiếu Benchmarking), hoặc xóa khỏi App.
  4. Sửa `tools/benchmark-folder.ps1`.
- **Hoàn thành khi:** `dotnet run --project tools/PhotoReview.Benchmark.Cli -c Release -- --benchmark-list-profiles` in đủ profile. `--benchmark` chạy được trên fixture T01.
- **Nhật ký:** —

### T51 — Bỏ WinForms
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** W4 DONE
- **Sở hữu file:** `PhotoReview.App.csproj`, `App/Services/FolderPicker.cs`, các file dùng `Forms.` hoặc alias `System.Windows.*` đầy đủ (trừ file T50 sở hữu. `BenchmarkWindow` dùng `FolderBrowserDialog` nên T51 **phối hợp**: đổi sau khi T50 merge, hoặc T50 tự dùng `IFolderPicker`).
- **Bước:** thay `FolderBrowserDialog` bằng `Microsoft.Win32.OpenFolderDialog`. Xóa `UseWindowsForms`. Rút gọn `System.Windows.MessageBox` thành `MessageBox`. Chuyển hết qua `IDialogService`.
- **Hoàn thành khi:** không còn `System.Windows.Forms` trong App, kích thước publish giảm (ghi số trước và sau).
- **Nhật ký:** —

### T52 — Dọn code chết và bug nhỏ
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** W4 DONE
- **Sở hữu file:** `Core/Settings/*` (legacy `Folder2Name`, `MoveToFolder2`), `SettingsWindow.*`, `AppConstants.cs`.
- **Bước:**
  1. Xóa `Folder2Name` và nhánh Move "Folder2" (đã mất cùng `ClassifyCurrentAsync` ở T46; nếu còn thì xóa). Giữ đọc được config cũ (bỏ qua trường).
  2. `MoveToFolder2`: đánh dấu legacy trong converter, bỏ khỏi UI (ghi lý do nếu cần giữ).
  3. `AppConstants`: chuyển sang `PerformanceOptions` (bind từ settings, có default), để chuẩn bị cho R-4.
  4. Rà lại các catch rỗng (`catch { }`): hoặc log qua `ILog`, hoặc ghi comment lý do.
- **Hoàn thành khi:** VERIFY đạt, config cũ vẫn load được (fixture T21).
- **Nhật ký:** —

### T53 — Xóa `PhotoReview.Tests`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T50, T11
- **Phạm vi file:** `tests/PhotoReview.Tests/` (xóa), `slnx`, `tools/verify-all.ps1`, CI, README, AGENTS (bỏ lệnh `dotnet run --project PhotoReview.Tests`).
- **Bước:** xác nhận `test-parity.md` 100% ánh xạ, xóa project, thay các gate tương ứng bằng `dotnet test`.
- **Hoàn thành khi:** VERIFY (bản mới) đạt, CI xanh.
- **Nhật ký:** —

---

## W6 — Hiệu năng theo nguyên tắc AGENTS

### T60 — Mở rộng metric
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** W5 DONE
- **Phạm vi file:** `Core/Diagnostics/ReviewMetrics.cs`, decoder, `IFileSystem` (bộ đếm), `DiagnosticsWindow`, benchmark report model.
- **Bước:** thêm `SourceOpenCount` theo path (top N), `StatCount`, histogram key→present (bucket), số lần ghi session. Đưa các số này vào `BenchmarkReport`.
- **Hoàn thành khi:** chạy benchmark ra report có trường mới. Chạy lại baseline (tag T00) bằng CLI mới nếu cần để có số so sánh.
- **Nhật ký:** —

### T61 — R-1 Session save debounce
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T60
- **Sở hữu file:** `Core/Session/*`, chỗ gọi trong `MainViewModel`/`ImagePresenter` (chỉ đổi lời gọi `Save` thành `Update`).
- **Bước:** `SessionWriter` giữ trạng thái mới nhất, ghi sau 500 ms không đổi hoặc khi `FlushAsync` (lúc đóng app hoặc đổi folder). Ghi trên worker, vẫn atomic.
- **Hoàn thành khi:** test cho thấy 100 lần update liên tiếp chỉ ghi 1–2 lần, đóng app thì flush, đổi folder thì flush folder cũ.
- **Nhật ký:** —

### T62 — R-2 Journal compaction
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T60
- **Sở hữu file:** `Core/FileActions/OperationJournal.cs`, `UndoService` (phần khởi động).
- **Bước:** (a) khi khởi động chỉ đọc N move committed gần nhất cần cho undo (đọc ngược từ cuối file), **hoặc** (b) compaction: khi file > X MB, ghi lại file chỉ gồm trạng thái mới nhất của mỗi Id chưa kết thúc + K move committed gần nhất (atomic, có backup). Chọn và ghi ADR ngắn trong nhật ký.
- **Hoàn thành khi:** test với journal 100k dòng khởi động < ngưỡng thống nhất. Reconcile và recovery vẫn đúng (INV-6).
- **Nhật ký:** —

### T63 — R-3 + R-6 Catalog metadata
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T60
- **Sở hữu file:** `ReviewCatalog` (entry type), `FolderLoadCoordinator` (scan dùng `DirectoryInfo.EnumerateFiles` để lấy length/mtime trong một lần), `StatusFormatter`, `ViewerState` (bỏ `FileInfo`).
- **Bước:** `CatalogEntry(Path, Length, LastWriteUtc, Dimensions?)`. Tính `totalSourceBytes` từ kết quả scan (bỏ `Task.Run` stat riêng). `ImageCacheKey.Create(entry)` chỉ revalidate khi cần (giữ kiểm tra fingerprint sau decode, INV-1).
- **Hoàn thành khi:** `StatCount` mỗi lần Next giảm (số liệu từ T60), INV-1 vẫn đạt.
- **Nhật ký:** —

### T64 — R-4a `RamBudgetPolicy`
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T60
- **Sở hữu file:** `Imaging/Preload/RamBudgetPolicy.cs` (mới), `PreloadScheduler` (điều kiện full-folder), `PerformanceOptions`.
- **Bước:** ước lượng decoded bytes = Σ(w×h×4) theo dimension (đọc header rẻ, hoặc ước lượng theo tỷ lệ nén trung bình rồi hiệu chỉnh dần). Quyết định full-folder khi decoded estimate ≤ capacity **và** headroom đủ. Mục tiêu dùng ≥ 16 GB khi máy 32 GB (giữ reserve 2 GB).
- **Hoàn thành khi:** test cho các tổ hợp (folder nhỏ, folder lớn, RAM thấp). Log hoặc metric ghi lại quyết định.
- **Nhật ký:** —

### T65 — R-4b `SourceBytesCache`
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T64 + người dùng chốt câu hỏi 5
- **Phạm vi file:** `Imaging/Caching/SourceBytesCache.cs` (mới), `WicImageDecoder`, `ThumbnailCache`, `PreviewImageService`, `FileHashService`, `PreloadScheduler`, settings (feature flag).
- **Bước:**
  1. LRU theo byte, key là fingerprint (path, length, mtime). Đọc tuần tự một lần (`SequentialScan`) vào `byte[]` (hoặc `ArrayPool`/pinned khi > 85 KB, cân nhắc POH).
  2. Decoder nhận `MemoryStream` từ cache: thumbnail, adaptive, dimension và hash **dùng chung** một lần đọc.
  3. Khi tổng nguồn ≤ budget (mặc định 16 GB) thì preload nạp byte toàn folder theo thứ tự lân cận trước, sau đó decoded tier lấy từ byte tier.
  4. Evict khi Move/Delete và clear cache (tăng epoch, giữ INV-2). File handle vẫn đóng ngay sau khi đọc (INV-8 được đảm bảo tốt hơn).
  5. Feature flag `UseSourceBytesCache`.
- **Hoàn thành khi:** metric `SourceOpenCount` ≤ 1 mỗi ảnh ở mọi mode (trừ khi bị evict). Memory headroom được tôn trọng. Test lifecycle (evict, clear, file đổi) đạt.
- **Nhật ký:** —

### T66 — Benchmark so sánh
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T61–T65
- **Phạm vi file:** `docs/refactoring/results/w6.md`.
- **Bước:** chạy lại đúng quy trình T01 (cùng máy và fixture). Chạy thêm biến thể flag bật/tắt cho R-4. So sánh median/P95/max, source reads, RAM peak.
- **Hoàn thành khi:** báo cáo có bảng so sánh và kết luận. Nếu P95 tệ hơn baseline quá 5% ở profile nào thì mở task sửa hoặc revert trước khi sang W7.
- **Nhật ký:** —

---

## W7 — Spike, tài liệu, release

### T70 — Spike decoder thay thế (R-7) + ADR
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T31
- **Phạm vi file:** branch spike riêng (`spike/decoder-*`), không merge. `docs/adr/0001-image-decoder.md`.
- **Bước:** viết `IImageDecoder` cho 1–2 lựa chọn (ví dụ NetVips, libjpeg-turbo qua P/Invoke, WIC trực tiếp qua `Windows.Graphics.Imaging` hoặc COM). Đo decode ms, RAM, chất lượng (so pixel và màu với WIC, EXIF orientation, ICC), license, kích thước phân phối.
- **Hoàn thành khi:** ADR có khuyến nghị giữ hay thay, kèm số liệu.
- **Nhật ký:** —

### T71 — ADR đánh giá WinUI 3 (C1)
- **Trạng thái:** TODO · **Song song:** Có · **Phụ thuộc:** T66
- **Phạm vi file:** `docs/adr/0002-ui-framework.md`.
- **Bước:** dựa trên số liệu T66 (thời gian UI assign/render so với decode), đánh giá lợi ích của Win2D/WinUI, chi phí migrate (Views + ViewModel đã tách thì ViewModel dùng lại được), packaging (unpackaged), file association.
- **Hoàn thành khi:** ADR có quyết định Go/No-go và điều kiện mở lại.
- **Nhật ký:** —

### T72 — Cập nhật tài liệu
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T66
- **Phạm vi file:** `README.md`, `AGENTS.md`, `outputs/APP-MECHANISMS-VI.md` (cân nhắc chuyển sang `docs/`), `docs/architecture.md` (mới: sơ đồ layer, luồng load/present/action), `task_on_progress.md`.
- **Hoàn thành khi:** mọi lệnh trong tài liệu chạy được trên layout mới. Bảng bất biến có ánh xạ tới tên class mới.
- **Nhật ký:** —

### T73 — GUI acceptance thủ công
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T72
- **Checklist** (ghi PASS/FAIL và ghi chú):
  1. Mở folder bằng nút, bằng kéo thả folder, bằng kéo thả ảnh, bằng tham số dòng lệnh, và Open With (association).
  2. Explorer đang mở cùng folder với sort tùy chỉnh thì thứ tự khớp Explorer. Explorer đóng thì dùng fallback.
  3. Next/Prev/Home/Space, PageUp/PageDown sang folder cùng cấp.
  4. Fast / Preview / Original: chất lượng hiển thị, zoom +/−, Ctrl+wheel, Fit, F11, Esc.
  5. Enter (Move Loại 2), F3/F4, F5 (Copy), Delete (Recycle). Ctrl+Z undo move. Undo recycle qua nút.
  6. Compare (`a.jpg` / `a (1).jpg`): chọn trái/phải, hash, size, action trên ảnh đã chọn.
  7. Remove duplicates (hai chế độ), hộp xác nhận batch, báo cáo lỗi.
  8. Recovery: tạo lỗi Move (đích đã tồn tại) rồi Retry.
  9. Settings: đổi mode khi đang xem, đổi shortcut bị trùng thì báo lỗi, import/export action.
  10. Diagnostics hiển thị metric. Clear cache. Logging bật/tắt.
  11. Hai instance cùng folder thì hiện thông báo.
  12. Folder 5.000+ ảnh: RAM, độ mượt, không treo UI.
- **Hoàn thành khi:** mọi mục PASS, hoặc lỗi đã có task sửa.
- **Nhật ký:** —

### T74 — Release
- **Trạng thái:** TODO · **Song song:** Không · **Phụ thuộc:** T73
- **Bước:** PR `refactor/integration → master` (mô tả link plan, tasks, kết quả T66/T73), CI xanh, merge **sau khi người dùng duyệt**. Tăng version (`1.1.0` hoặc `2.0.0`), publish vào thư mục mặc định, `verify-release.ps1`, tag `v<version>`. Cập nhật `task_on_progress.md`.
- **Hoàn thành khi:** tag đã push, artifact publish đã được verify.
- **Nhật ký:** —
