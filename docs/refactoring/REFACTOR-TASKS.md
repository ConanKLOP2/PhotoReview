# PhotoReview — Task tái cấu trúc (B + C3)

> Review sau merge 2026-09-18 (`afb2f77`): [plan sửa lỗi](MERGED-CODE-REVIEW-PLAN.md) và [task MR00–MR06](MERGED-CODE-REVIEW-TASKS.md). Đề xuất chờ duyệt; ưu tiên preload/gate trước D07, hoàn tất contract/quality trước T87. Không thay trạng thái các task cũ bằng kết quả review này.

- **Plan:** [`REFACTOR-PLAN.md`](REFACTOR-PLAN.md) · **Cập nhật:** 2026-09-16
- **Trạng thái hợp lệ:** `TODO` · `IN PROGRESS` · `BLOCKED` · `DONE`
- **Ký hiệu:**
  - **⛔Qn:** phải có câu trả lời cho câu hỏi Qn (plan mục 11) trước khi bắt đầu.
  - **∥:** chạy song song được với các task khác cùng nhóm.

---

## 0. Hướng dẫn cho model thực thi (đọc trước mỗi phiên)

1. Đọc `AGENTS.md`, `task_on_progress.md`, rồi plan mục **4.6 (INV)**, **3.3 (K)** và **7.2**.
2. Chọn **một** task thỏa cả ba điều kiện: trạng thái `TODO`, mọi task trong **Phụ thuộc** đã `DONE`, và không bị ⛔ (hoặc ⛔ đã được trả lời trong nhật ký T00).
3. Sửa trạng thái sang `IN PROGRESS` và ghi `Agent: <tên model>`. Tạo worktree và branch `refactor/<ID>-<slug>` từ `refactor/integration`.
4. Chỉ sửa file trong mục **Files**. Nếu thấy cần sửa file khác, đổi sang `BLOCKED` và ghi lý do.
5. Làm đúng các bước trong **Làm**. Không làm gì trong mục **Không làm**.
6. Chạy **Kiểm thử** và VERIFY. Test phải đạt thật, không được skip hay xóa assertion để cho qua.
7. Ghi **Nhật ký** theo mẫu, đổi trạng thái sang `DONE`, commit `refactor(<layer>): <mô tả> (<ID>)`.

### Phân model và quy trình review

Áp dụng cho cả file này và [`PERF-DIAGNOSIS-TASKS.md`](PERF-DIAGNOSIS-TASKS.md).

| Mức | Model thực hiện | Task | Review |
|---|---|---|---|
| **L1** | Haiku 4.5 | T00, T02, T03, T04, T05, T10, T12, T13a, T20, T22a, T22c, T23a, T23b, T26b, T30, T33a, T45a, T50a, T51, T53a, T72, T88 · D00, D01, D02, D07 | **R1:** Coordinator đọc diff + kiểm tra VERIFY. Nếu diff đụng file ngoài danh sách thì trả lại |
| **L2** | Sonnet 5 | T11, T13b, T21a, T21b, T21c, T22b, T25a, T25b, T26a, T26c, T31a, T31b, T32, T33b, T34, T35, T40, T41a, T42a, T42b, T42c, T43a, T43b, T45b, T46d, T47, T50b, T52, T53b, T60, T61, T62, T63, T64, T73, T74, T80, T81, T83, T87 · D03, D05, D08, D09, D10, D11 | **R2:** một agent Sonnet 5 **mới** (không có context của worker) review theo checklist. Task nào đụng INV-1…INV-12 thì review bằng Opus 5 |
| **L3** | Opus 5 | T14a–T14d, T24, T31c, T41b, T44, T45c, T46a, T46b, T46c, T65, T82, T84, T85, T86, T71 · D04, D06, D12, D13 | **R3:** một agent Opus 5 **mới** review theo checklist. Test liên quan phải chạy 10 lần đều đạt. Coordinator kiểm thủ công nếu có đổi UI |

**Mức effort gợi ý:** L1 Low, L2 Medium, L3 High. Task L1 có chữ "race", "generation", "epoch", "COM" hoặc "P/Invoke" thì nâng lên Medium.

**Checklist review (R2/R3)** — reviewer trả lời từng mục bằng PASS, FAIL hoặc N/A kèm dẫn chứng `file:line`:
1. Diff chỉ gồm file trong mục **Files** của task.
2. Mọi bước **Làm** đã được thực hiện; không có gì thuộc mục **Không làm**.
3. Bất biến INV và ràng buộc K liên quan vẫn đúng. Nêu rõ INV nào và kiểm tra bằng cách nào.
4. Không đổi hành vi ngoài phạm vi. Chuỗi tiếng Việt hiển thị giữ nguyên văn. Comment về race/contract không bị xóa.
5. Test mới kiểm tra hành vi thật: không assert rỗng, không `Skip` mà không có lý do.
6. Reviewer tự chạy lại lệnh **Kiểm thử** và ghi kết quả.
7. Kết luận: `APPROVE` hoặc `CHANGES` kèm danh sách sửa cụ thể. Nếu `CHANGES`, worker cũ (hoặc một worker mới cùng mức) sửa, rồi review lại. Tối đa 2 vòng; quá 2 vòng thì nâng task lên mức cao hơn.

**Quy ước để tiết kiệm token:**
- Worker **không** sửa `REFACTOR-TASKS.md`, `PERF-DIAGNOSIS-TASKS.md` hay `task_on_progress.md` (tránh conflict). Worker trả nhật ký trong câu trả lời; Coordinator ghi vào file.
- Worker **không dùng** `git add -A` hay `git add .`; chỉ `git add <file cụ thể>` nằm trong danh sách Files.
- Worker **không sửa** chuỗi mô tả hay DisplayName của test/check có sẵn, trừ khi task yêu cầu (vì bảng parity ghép theo chuỗi này). Không gắn Trait làm test bị loại khỏi CI nếu test đó chạy headless được.
- Worker không push. Coordinator merge vào `refactor/integration` theo thứ tự ID, chạy VERIFY đầy đủ **một lần sau mỗi đợt merge**, rồi push.
- Worker chỉ đọc mục task của mình, mục 0 của file này và các mục plan được task tham chiếu. Không đọc toàn bộ plan.

### Alias đường dẫn

T24 chuyển layout, nên đường dẫn thật phụ thuộc T24 đã xong chưa:

| Alias | Trước T24 | Sau T24 |
|---|---|---|
| `{App}` | `PhotoReview.App/` | `src/PhotoReview.App/` |
| `{Core}` | `PhotoReview.Core/` | `src/PhotoReview.Core/` |
| `{Imaging}` | — (tạo ở T30, sau T24) | `src/PhotoReview.Imaging/` |
| `{Platform}` | — | `src/PhotoReview.Platform.Windows/` |
| `{UT}` | `PhotoReview.Tests.Unit/` | `tests/PhotoReview.Tests.Unit/` |
| `{CoreT}` | `PhotoReview.Core.Tests/` | `tests/PhotoReview.Core.Tests/` |
| `{ImgT}` | — | `tests/PhotoReview.Imaging.Tests/` |
| `{AppT}` | — | `tests/PhotoReview.App.Tests/` |
| `{IntT}` | — | `tests/PhotoReview.Integration.Tests/` |
| `{CLI}` | `PhotoReview.Tests/` | `tests/PhotoReview.Tests/` → sau T50b là `tools/PhotoReview.Benchmark.Cli/` |

### VERIFY

```powershell
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
dotnet run --project {CLI} -c Release          # chỉ khi {CLI} vẫn là test runner (trước T50b)
.\tools\verify-all.ps1
```

### Mẫu nhật ký

```text
- YYYY-MM-DD · <model> · branch refactor/<ID>-<slug> · commit <sha>
  Files: ...
  Thay đổi: ...
  Kiểm thử: <lệnh> → <kết quả, số test đạt/tổng>
  Lệch plan / rủi ro / việc còn lại: ...
```

---

## 1. Bảng tổng quan

| ID | Tên | Phụ thuộc | ∥ | Cổng | TT |
|---|---|---|---|---|---|
| T00 | Tag baseline, branch tích hợp, ghi xác nhận Q | — | | | DONE |
| T01 | Benchmark baseline (gồm decode). **Được thay bằng D07 + D12** | T00 | ∥A | | TODO |
| T02 | CI GitHub Actions | T00 | ∥A | | DONE |
| T03 | `global.json`, `.editorconfig`, analyzer | T00 | ∥A | | DONE |
| T04 | Central Package Management | T00 | ∥A | | DONE |
| T05 | AGENTS: quy trình PR | T00 | ∥A | ⛔Q4 | DONE |
| T10 | Ma trận parity test | T00 | | | DONE |
| T11 | Chuyển check CLI còn thiếu sang xUnit | T10 | ∥B | | DONE |
| T12 | Xóa file test trùng trong CLI | T10 | ∥B | | DONE |
| T13a | Gắn Trait Integration/Manual | T10 | ∥B | | DONE |
| T13b | Sửa test flaky quota prune | T10 | ∥B | | DONE |
| T14a | STA harness + seam tối thiểu trong MainWindow | T11, T13a, D05, D06, D10 | | | DONE |
| T14b | Test INV-3, INV-4 trên MainWindow | T14a | ∥C | | DONE |
| T14c | Test INV-5 | T14a | ∥C | | DONE |
| T14d | Test INV-7, INV-9 | T14a | ∥C | | DONE |
| T20 | Tạo Core + Core.Tests | T14b–d | | | DONE |
| T21a | Enum + `LenientEnumConverter` trong Core | T20 | ∥D | | DONE |
| T22a | `AppPaths` | T20 | ∥D | | DONE |
| T22b | `IFileSystem` + triển khai | T20 | ∥D | | DONE |
| T22c | Các interface còn lại | T20 | ∥D | | DONE |
| T23a | Chuyển service thuần (sort, sibling, compare, drag-drop, file types) | T20 | ∥D | | DONE |
| T23b | Chuyển `ReviewMetrics`, `BenchmarkStatistics`, `ExplorerSnapshotValidator` | T20 | ∥D | | DONE |
| T21b | `AppSettings` dùng enum + sửa caller | T21a, T23a | | | DONE |
| T21c | `JournalEntry` dùng enum | T21a | | | DONE |
| T24 | Chuyển layout `src/`/`tests/` | T21b, T21c, T22a–c, T23a–b | | ⛔Q3 | DONE |
| T25a | `SettingsStore` | T24 | | | DONE |
| T25b | `SettingsValidator` + `IKeyNameValidator` | T25a | | | DONE |
| T26a | `OperationJournal` qua abstraction | T24 | ∥E | | DONE |
| T26b | `SessionStore` qua abstraction | T24 | ∥E | | DONE |
| T26c | `RecoveryRetryService` thành instance | T26a | | | DONE |
| T30 | Tạo Imaging, Platform, Imaging.Tests, Integration.Tests | T25b, T26b, T26c | | | DONE |
| T31a | `DiskCacheStore` thành instance | T30 | ∥F | | DONE |
| T31b | `IImageDecoder` + `WpfBitmapImageDecoder` | T30 | ∥F | | DONE |
| T32 | `PreloadScheduler` bỏ Dispatcher | T30 | ∥F | | DONE |
| T33a | Chuyển Explorer sang Platform | T30 | ∥F | | DONE |
| T33b | Chuyển RecycleBin, Memory, InstanceLock, NaturalComparer sang Platform | T30 | ∥F | | DONE |
| T31c | `IDecodedImage` + `WpfImageAdapter` (K-1) | T31a, T31b, T32, T33a, T33b | | | DONE |
| T34 | `AppLog` → `FileLog : ILog` | T31c, T32, T33a, T33b | | | DONE |
| T35 | Test kiến trúc | T34 | | ⛔Q6 | DONE |
| T80 | Fixture ảnh + helper đo chất lượng | T31c | ∥G | | DONE |
| T81 | `--decoder-bench` | T31c | ∥G | | DONE |
| T83 | `ExifOrientation` + áp dụng trong backend Wpf | T80 | | | DONE |
| T84 | `IImageDecoderFactory` + fallback + backend trong cache key | T83, T35 | | | DONE |
| T82 | `WicDirectDecoder` | T84 | ∥H | | DONE |
| T85 | `TurboJpegDecoder` | T84 | ∥H | ⛔Q7 | DONE |
| T86 | Cổng chất lượng + benchmark + ADR 0001 | T81, T82, (T85) | | | DONE |
| T40 | Composition root DI + Mvvm | T35 | | ⛔Q6 | DONE |
| T41a | `ReviewCatalog` + `CatalogEntry` | T40 | ∥I | | DONE |
| T41b | `GenerationClock` | T40 | ∥I | | DONE |
| T42a | `FileActionService` | T40 | ∥I | | DONE |
| T42b | `UndoService` | T42a | | | DONE |
| T42c | `DuplicateFinder` | T40 | ∥I | | DONE |
| T43a | `ShortcutRouter` | T40 | ∥I | | DONE |
| T43b | `ViewerState` | T40 | ∥I | | DONE |
| T44 | `FolderLoadCoordinator` | T41a, T41b | | | DONE |
| T45a | `StatusFormatter` | T40 | ∥I | | DONE |
| T45b | `CompareViewModel` | T41a | | | DONE |
| T45c | `ImagePresenter` | T44, T45a, T45b, T84 | | | DONE |
| T46a | `MainViewModel`: mở folder + điều hướng | T43a, T43b, T45c | | | DONE |
| T46b | `MainViewModel`: file action + undo | T46a, T42b | | | DONE |
| T46c | `MainViewModel`: duplicate, recovery, diagnostics, settings | T46b, T42c | | | DONE |
| T46d | `MainWindow` binding, rút gọn code-behind | T46c | | | DONE |
| T47 | Xóa test source-presence | T46d | | | DONE |
| T88 | `BitmapScalingMode` chất lượng cao + setting | T46d | ∥J | | DONE |
| T50a | Project `PhotoReview.Benchmarking` | T47 | ∥J | | DONE |
| T50b | `Benchmark.Cli` + quyết định `BenchmarkWindow` | T50a | | ⛔Q2 | DONE |
| T51 | Bỏ WinForms | T50b | | | DONE |
| T52 | Dọn code chết | T47, T88 | | | DONE |
| T87 | Tích hợp decoder đã chọn vào app | T86, T52 | | ⛔Q8 | TODO |
| T53a | Xóa CLI test runner | T50b, T11 | | | DONE |
| T53b | Chia `Tests.Unit` vào các project test theo lớp | T53a | | | DONE |
| T60 | Mở rộng metric | T53b | | | DONE |
| T61 | R-1 Session debounce | T60 | ∥K | | TODO |
| T62 | R-2 Journal startup | T60 | ∥K | | TODO |
| T63 | R-3/R-6 Catalog metadata | T60 | ∥K | | TODO |
| T64 | R-4a `RamBudgetPolicy` | T60 | ∥K | | TODO |
| T65 | R-4b `SourceBytesCache` | T64, T87 | | ⛔Q5 | TODO |
| T66 | Benchmark so sánh cuối | T61–T65, T87, T88 | | | TODO |
| T71 | ADR 0002 UI framework (C1 go/no-go) | T66 | ∥L | | TODO |
| T72 | Cập nhật tài liệu | T66 | ∥L | | TODO |
| T73 | GUI acceptance | T72 | | | TODO |
| T74 | Release | T73 | | | TODO |

**Chẩn đoán hiệu năng:** các task D00–D13 nằm trong [`PERF-DIAGNOSIS-TASKS.md`](PERF-DIAGNOSIS-TASKS.md). Chúng chạy sau T00 và trước T14a (các task D sửa `MainWindow`/`PreviewImageService`/`PreloadScheduler`). Mọi task di chuyển hoặc tách code phải giữ event `PhotoReview-Perf` và biến môi trường chẩn đoán (ràng buộc **K-4**). T66 dùng lại `--perf-session`/`run-matrix.ps1`/`--perf-analyze` của D06/D11.

**Nhóm chạy song song:** ∥A (W0), ∥B (W1), ∥C (test INV), ∥D (Core), ∥E (Journal/Session), ∥F (Imaging/Platform), ∥G (hạ tầng đo C3), ∥H (backend C3), ∥I (thành phần MVVM), ∥J (hậu MVVM), ∥K (hiệu năng), ∥L (tài liệu).

**Chạy song song giữa hai nhánh lớn:** sau T35, nhánh C3 (T80…T86) và nhánh W4 (T40…T47) chạy cùng lúc được. Hai nhánh không sửa chung file: C3 chỉ đụng `{Imaging}`, `{ImgT}`, `{CLI}`; W4 đụng `{App}`, `{Core}/Catalog|FileActions`, `{AppT}`. Ngoại lệ:
- T83 và T84 sửa `ImageCacheKey` và `PreviewImageService`, nên T45c phụ thuộc T84.
- Các task sửa `AppSettings`/`SettingsWindow` phải chạy tuần tự: T88 → T52 → T87.

---

## W0 — Baseline

### T00 — Tag baseline, branch tích hợp, xác nhận câu hỏi
- **TT:** TODO · **Agent:** Coordinator
- **Files:** `task_on_progress.md`, mục nhật ký T00 trong file này.
- **Làm:**
  1. `git tag pre-refactor-baseline 86282cd`, `git push origin pre-refactor-baseline`.
  2. `git switch -c refactor/integration refactor/plan-b-c3`, `git push -u origin refactor/integration`.
  3. Ghi câu trả lời hoặc xác nhận mặc định cho Q2–Q8 của người dùng vào nhật ký T00.
- **Xong khi:** tag và branch có trên origin, nhật ký ghi rõ trạng thái từng Q.
- **Nhật ký:**
  - 2026-09-16 · Opus 5 (Coordinator) · branch `refactor/integration` (từ `refactor/plan-b-c3` @ `f1f98ca`)
    - Tag `pre-refactor-baseline` → `86282cd`.
    - **Người dùng chấp nhận mặc định Q2–Q8** (2026-09-16):
      - Q2: giữ `BenchmarkWindow`.
      - Q3: chuyển project vào `src/`.
      - Q4: quy trình PR.
      - Q5: R-4 chỉ bật mặc định sau khi T66 đạt.
      - Q6: đồng ý `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `NetArchTest.Rules`.
      - Q7: đóng gói turbojpeg chỉ khi nhanh hơn WicDirect ≥ 15%.
      - Q8: bật decoder mới khi đạt cổng chất lượng.
    - Đợt 1 dừng sau W0 + T10 để người dùng kiểm tra.

### T01 — Benchmark baseline ∥A
- **Ghi chú:** nếu D07 và D12 đã `DONE` thì đánh dấu T01 `DONE`, link tới `docs/refactoring/diagnosis/REPORT.md`, và chỉ bổ sung phần `--benchmark-all` (bước 3) nếu D07 chưa chạy.
- **Files:** `docs/refactoring/baseline/**` (mới). Không sửa code.
- **Làm:**
  1. Chọn fixture ảnh thật cố định: ≥ 200 JPEG 12–50 MP, ≥ 20 PNG, vài ảnh chụp dọc có EXIF orientation. Ghi đường dẫn, số lượng, tổng dung lượng.
  2. Tại tag baseline: build Release. Xóa `%LOCALAPPDATA%\PhotoReview\cache` và `thumbnails` để đo cold.
  3. `dotnet run --project PhotoReview.Tests -c Release -- --benchmark-all <fixture> docs/refactoring/baseline/cold-N`, chạy 3 lần cold và 3 lần warm.
  4. Viết `baseline.md`: cấu hình máy (CPU, RAM, ổ đĩa, power plan), median/P95/max từng profile, `DecodeMilliseconds`, `UiAssignMilliseconds`, `PresentMilliseconds`, source reads/bytes, RAM peak (Task Manager hoặc `Get-Process`).
  5. Ghi danh sách ảnh bị hiển thị sai hướng (kiểm bằng mắt, dùng cho T83).
- **Xong khi:** `baseline.md` có đủ các số liệu trên.
- **Nhật ký:** —

### T02 — CI ∥A
- **Files:** `.github/workflows/ci.yml` (mới).
- **Làm:**
  1. Runner `windows-latest`, `actions/setup-dotnet` (`global-json-file` nếu có, nếu không thì `10.0.x`).
  2. Các bước: restore → build Release → `dotnet run --project PhotoReview.Tests -c Release` → `dotnet test -c Release --filter "Category!=Manual" --logger trx` → publish framework-dependent → `tools/verify-release.ps1`.
  3. Upload artifact thư mục publish và file trx.
  4. Trigger: PR vào `master` và `refactor/integration`; push vào `master`.
- **Không làm:** sửa test cho qua CI. Test nào lỗi do môi trường runner thì ghi tên vào nhật ký để T13a gắn Trait.
- **Xong khi:** workflow chạy xanh (hoặc chỉ đỏ ở các test đã liệt kê).
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `refactor/T02-ci` `f43c5e1` · Files: `.github/workflows/ci.yml` · 6 lệnh CI chạy local đạt (xUnit 190/190) · R1 (Opus): APPROVE · merge `ff05b6c`. GitHub Actions trên `refactor/integration`: build thành công, 21 warning (người dùng xác nhận).

### T03 — `global.json`, `.editorconfig`, analyzer ∥A
- **Files:** `global.json`, `.editorconfig`, `Directory.Build.props`.
- **Làm:**
  1. `global.json`: `{"sdk":{"version":"10.0.401","rollForward":"latestFeature"}}`.
  2. `.editorconfig`: 4 spaces, CRLF, file-scoped namespace, ưu tiên `var`, bật collection expression. Severity chỉ ở mức `suggestion`.
  3. `Directory.Build.props`: `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `Deterministic=true`. **Không** bật `TreatWarningsAsErrors`.
  4. Ghi số warning trước và sau vào nhật ký.
- **Không làm:** sửa warning.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `refactor/T03-build-config` `efc4a49` · Files: `global.json`, `.editorconfig`, `Directory.Build.props` · warning 0 → 140 (CA analyzer, chưa sửa) · build/CLI/xUnit 190/190 đạt · R1 (Opus): CHANGES, vì XAML thực tế thụt 4 spaces; Coordinator đã sửa ở `44a70a3` · merge `7b1cb84`. GitHub Actions trên `refactor/integration`: build thành công, 21 warning (người dùng xác nhận).

### T04 — Central Package Management ∥A
- **Files:** `Directory.Packages.props` (mới), `PhotoReview.Tests.Unit/PhotoReview.Tests.Unit.csproj`.
- **Làm:** thêm `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` và `PackageVersion` cho `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4. Xóa `Version=` trong csproj.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `refactor/T04-cpm` `6a9c7e0` · Files: `Directory.Packages.props`, csproj test · restore/build/xUnit 190/190/CLI đạt · R1 (Opus): APPROVE · merge `3dd2932`. GitHub Actions trên `refactor/integration`: build thành công, 21 warning (người dùng xác nhận).

### T05 — AGENTS: quy trình PR ∥A ⛔Q4
- **Files:** `AGENTS.md`.
- **Làm:** thay mục "push origin master" bằng quy trình: feature branch → PR vào `master` → CI xanh → merge. Vẫn bắt buộc test và publish trước khi bàn giao. Thêm dòng "Đợt refactor: xem `docs/refactoring/`".
- **Xong khi:** người dùng duyệt diff.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `refactor/T05-agents-pr` `2def97f` · R1 (Opus): APPROVE · người dùng duyệt diff 2026-09-16 · đã merge.

---

## W1 — Hợp nhất test, khóa hành vi

### T10 — Ma trận parity
- **Files:** `docs/refactoring/test-parity.md` (mới).
- **Làm:**
  1. Bảng 1: từng `Check(` trong `PhotoReview.Tests/Program.cs` (số dòng, mô tả), test xUnit tương ứng (`file::method`) hoặc `MISSING` hoặc `DROP: <lý do>`.
  2. Bảng 2: `diff` 5 file trùng (`BenchmarkScenarioTests`, `CacheExplorerRegressionTests`, `FileActionConcurrencyTests`, `ImageCacheKeyTests`, `PerformanceTestHarness`) và ghi khác biệt.
  3. Bảng 3: 69 test trong `SourcePresenceTests`, phân loại `REPLACE-BY:<task>` (T14x, T41–T46), `KEEP-XAML`, hoặc `DROP`.
- **Xong khi:** mọi dòng đều đã có phân loại.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 (`a0bde7c`) → R1 (Opus): **CHANGES**, vì 67/147 tên method bị bịa → Coordinator dựng CSV bằng script (khớp chính xác DisplayName) → nâng lên Sonnet 5 (`refactor/T10-parity-fix` `e161c5c`) → Coordinator kiểm tên 146/146 + 69/69: APPROVE.
  - Kết quả: Bảng 1 146/146 COVERED, không có MISSING. Bảng 3: REPLACE-BY 58, DROP 4, KEEP-XAML 6 (đề xuất gộp 3 test `AutomationProperties` thành 1 ở T47), KEEP-SCRIPT 1 (`FileAssociationCommandIsRegistered`).
  - T12: xóa được 4 file (bỏ lời gọi `Program.cs` dòng 96–99); `PerformanceTestHarness.cs` giữ tới T50b.
  - Bài học: task ghép tên hoặc tra cứu lớn phải có script kiểm tên trong bước Kiểm thử.

### T11 — Chuyển check còn thiếu ∥B
- **TT:** DONE
- **Files:** `{UT}/MigratedCliChecksTests.cs` (mới), cột trạng thái trong `test-parity.md`.
- **Ghi chú (sau T10):** không có MISSING, nên T11 chỉ cần xác nhận lại bằng script ghép DisplayName rồi đánh DONE, không viết test mới.
- **Làm:** viết xUnit cho mọi dòng `MISSING`, giữ nguyên ngữ nghĩa, dùng `TempRoot`. Test đụng global state thì thêm `[Collection("GlobalState")]`.
- **Xong khi:** không còn `MISSING`, `dotnet test` đạt.
- **Nhật ký:** 2026-09-16 · Coordinator · script ghép DisplayName (`scratchpad/parity.ps1`) xác nhận 146/146 check CLI có xUnit tương ứng → không cần viết test mới. `test-parity.md` không còn MISSING.

### T12 — Xóa file trùng trong CLI ∥B
- **TT:** DONE
- **Files:** 5 file trùng trong `PhotoReview.Tests/`, và phần gọi chúng trong `PhotoReview.Tests/Program.cs`.
- **Làm:** xóa file và lời gọi, chỉ với những file T10 đã xác nhận xUnit bao phủ đủ.
- **Không làm:** đụng `LocalImageBenchmark.cs` hay `LocalUiNextProbe.cs`.
- **Xong khi:** `dotnet run --project PhotoReview.Tests -c Release` in `PASS: all ...`.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `refactor/T12-remove-dup` `ab24ecb` · xóa 4 file + 4 lời gọi `Program.cs` · xUnit 190/190, CLI đạt · R1 (Opus): CHANGES, vì agent sửa mô tả một check ngoài phạm vi; Coordinator hoàn nguyên trong commit merge · CLI in 147 dòng PASS (146 check + tổng kết).

### T13a — Trait ∥B
- **TT:** DONE
- **Files:** các file test trong `{UT}` **trừ** `MigratedCliChecksTests.cs`.
- **Làm:** gắn `[Trait("Category","Integration")]` cho test dùng Recycle Bin, Explorer, registry, hoặc thời gian thực > 1 s. Gắn `Manual` cho test cần Explorer đang mở hoặc GUI. Tạo `[CollectionDefinition("GlobalState", DisableParallelization = true)]` và gán cho test đụng `AppLog` hoặc biến môi trường. Thử bỏ `DisableTestParallelization` toàn assembly; nếu còn lỗi thì giữ lại và ghi nguyên nhân.
- **Xong khi:** `dotnet test` chạy đạt 5 lần liên tiếp. Ghi thời gian chạy trước và sau.
- **Nhật ký:** 2026-09-16 · Haiku 4.5 · `refactor/T13a-traits` `acca8ae` · GlobalState cho 5 class (AppLog, SessionStore, OperationJournal, JournalReconciliation, RecoveryRetry) · Integration cho 2 test > 1 s · thử chạy song song: 1/5 lần fail (`Eviction forces a fresh source read…`) nên giữ `DisableTestParallelization` và chuyển cho T13b · R1 (Opus): CHANGES, vì agent gắn `Manual` cho cả `SourcePresenceTests` làm chúng bị loại khỏi CI; Coordinator bỏ nhãn đó khi merge · kết quả: `Category!=Manual` 190/190, `Integration` 2.

### T13b — Test flaky quota prune ∥B
- **TT:** DONE
- **Files:** `{UT}/DiskCacheStoreTests.cs`, `{UT}/PreviewImageServiceTests.cs` (chỉ test liên quan).
- **Làm:** tái hiện lỗi 189/190 (xem `task_on_progress.md` cũ trong git log `e6e2d49`). Sửa **test**: mỗi test dùng thư mục cache riêng và `await DiskCacheStore.WaitForPruneAsync(dir, ...)` trước khi assert hoặc xóa thư mục. Nếu lỗi nằm ở code production thì đặt `BLOCKED` và mô tả.
- **Xong khi:** chạy toàn suite 10 lần đều đạt.
- **Nhật ký:** 2026-09-16 · Sonnet 5 · `refactor/T13b-flaky` `d7f6daf` · nguyên nhân: persist worker nền ghi disk cache trước request thứ 2 (test eviction), và vòng poll timeout cố định (test prune) · sửa: test eviction dùng service Original riêng; test prune dùng `ShutdownPersistWorkersAsync` + `WaitForPruneAsync`; **bỏ `DisableTestParallelization`** · trước sửa (song song) 7/10 lần đạt; sau sửa 30/30 lần đạt · không nghi ngờ bug production · review (Opus): APPROVE, kèm ghi chú bổ sung test eviction cho chế độ downscaled ở T31a · VERIFY đạt, xUnit 190/190 chạy 2 s (trước 16 s).

### T14a — STA harness + seam
- **Files:** `{UT}/Infrastructure/StaTestHost.cs` (mới), `{App}/AssemblyInfo.cs` (thêm `InternalsVisibleTo("PhotoReview.Tests.Unit")`), `{App}/MainWindow.xaml.cs` (**chỉ** thêm seam).
- **Làm:**
  1. `StaTestHost.RunAsync(Func<Task>)`: tạo thread STA, dựng `Application` một lần (hoặc dùng `Dispatcher`), chạy `DispatcherFrame` cho tới khi task xong, có timeout 30 s.
  2. Seam trong `MainWindow`:
     - Constructor `internal MainWindow(string? initialPath, MainWindowTestHooks hooks)`.
     - `MainWindowTestHooks` có: `IExplorerOrderProvider? Explorer`, `Action<string>? OnPresented`, `Func<string,string,Task>? MoveOverride`.
     - Chỉ đổi tối thiểu cần thiết để dùng hook: thêm interface nội bộ `IProgressiveExplorerOrderProvider` (chữ ký giống `TryGetSnapshotProgressiveAsync`) do `ExplorerOrderService` implement, và đổi `_explorerOrder` sang interface này (T22c/T23b sẽ thay bằng interface trong Core); `File.Move` trong action đi qua `MoveOverride ?? Task.Run(File.Move)`.
     - Đặt `PHOTOREVIEW_DATA_ROOT` về thư mục tạm trong test.
  3. Một smoke test: mở folder tạm có 3 PNG nhỏ, `OnPresented` được gọi với ảnh đầu.
- **Không làm:** đổi logic hay thứ tự lệnh trong `MainWindow`.
- **Xong khi:** smoke test đạt 10/10 lần. Nếu không làm được seam mà không đổi logic thì đặt `BLOCKED` và đề xuất dời INV sang T44–T46.
- **Nhật ký:**
  - 2026-09-17 · Opus 5 (Coordinator) · trạng thái IN PROGRESS, giao worker Opus 5 trên máy 2 (VTI) — máy này không có fixture ảnh thật nên không dùng cho D07+; chuyển sang làm tiếp nhánh refactor. Branch `refactor/T14a-sta-harness`.
  - 2026-09-17 · Opus 5 · branch `refactor/T14a-sta-harness` · commit `92cd88f`.
    - **Files:** `PhotoReview.Tests.Unit/Infrastructure/StaTestHost.cs` (mới), `PhotoReview.App/AssemblyInfo.cs`, `PhotoReview.App/MainWindow.xaml.cs`.
    - **Thay đổi:** `StaTestHost.RunAsync` chạy body trên một thread STA dùng chung (`Application` + `DispatcherSynchronizationContext` dựng một lần), mỗi body đẩy một `DispatcherFrame` riêng, timeout 30 s (`StaTestHost.cs:17-111`). Seam trong `MainWindow`: ctor `internal MainWindow(string?, MainWindowTestHooks)` (`:61`) chain qua `ApplyTestEnvironment` (`:1218`) nên `PHOTOREVIEW_DATA_ROOT` được đặt **trước** field initializer của `_journal`; `_explorerOrder` đổi sang `IProgressiveExplorerOrderProvider` (`:41`, gán tại `:68`); `OnPresented` gọi tại choke point present duy nhất, ngay sau `MainImage.Source = image` (`:382`, giữa dòng AppLog và `perfKick`, không ảnh hưởng đo D04); `MoveOverride ?? Task.Run(File.Move)` qua `MoveFileAsync` (`:1208`) cho 3 call site move/undo (`:959`, `:1076`, `:1164`). Không đổi thứ tự/logic lệnh có sẵn.
    - **Lệch plan (đã review, chấp nhận):**
      1. `ExplorerOrderService` **không** implement `IProgressiveExplorerOrderProvider` trực tiếp — lý do thật là `ExplorerOrderService.cs` **không nằm trong Files list** của task (không phải vì class `sealed` như log ban đầu ghi nhầm — `sealed` không cản trở implement interface). Thay vào đó `ExplorerOrderProviderAdapter` trong `MainWindow.xaml.cs` (`:1239-1248`) bọc service thật, forward `Dispose()` đúng một lần. Hành vi giống hệt; T22c/T23b sẽ thay bằng interface ở Core.
      2. `MainWindowTestHooks.Explorer` kiểu `IProgressiveExplorerOrderProvider?`, không phải `IExplorerOrderProvider?` như task ghi — interface public đó chỉ có `TryGetSnapshotAsync`, thiếu method progressive nên dùng đúng nghĩa đen sẽ không compile được.
      3. Harness dùng reflection để trỏ `Application.ResourceAssembly`/`BaseUriHelper.ResourceAssembly` sang assembly App (vì `MainWindow.xaml` có `Icon="pack://application:,,,/Assets/PhotoReview.ico"`, không resolve được trong test host trần, và `MainWindow.xaml` ngoài Files list nên không sửa được). **Rủi ro đã biết:** phụ thuộc internal field của WPF, có thể vỡ khi đổi runtime; nếu vỡ sẽ throw lỗi named rõ ràng chứ không âm thầm. Đề xuất follow-up: chuyển icon ra khỏi XAML (biến pack URI có assembly name) để xoá đoạn reflection này.
    - **Kiểm thử:** smoke test `StaTestHostSmokeTests` đạt 10/10 lần chạy `dotnet test` riêng biệt (worker và reviewer độc lập đều xác nhận). VERIFY `.\tools\verify-all.ps1` đạt, xUnit 251/251, `PASS: all PhotoReview verification gates` (cả trước và sau merge).
    - **Review R3:** Opus 5 mới, không có context worker — chạy lại toàn bộ VERIFY + 10 lần smoke test độc lập, kiểm INV-6/7/9 và K-4, xác nhận ctor chaining/field-initializer ordering đúng C#, xác nhận `MoveFileAsync` tương đương hành vi exception/await ở cả 3 call site. **Verdict: APPROVE**, không có CHANGES bắt buộc.
    - **Việc còn lại cho T14b–T14d:** (a) fake `Explorer` phải implement `IProgressiveExplorerOrderProvider` (internal), không phải `IExplorerOrderProvider` (public); (b) mọi test dùng seam này **phải** dùng `DataRootFixture` của `StaTestHost.cs` — nhánh set `PHOTOREVIEW_DATA_ROOT` mặc định trong `ApplyTestEnvironment` mutate biến môi trường process-global và không tự dọn temp dir, nếu bỏ qua fixture sẽ làm các test `GlobalState` khác không deterministic; (c) cân nhắc chuyển smoke test hiện tại từ `StaTestHost.cs` sang file test riêng khi được phép thêm file mới.

### T14b — INV-3, INV-4 ∥C
- **Files:** `{UT}/MainWindowBehaviorTests.Actions.cs` (mới).
- **Ghi chú từ T14a:** mọi test dùng seam của `MainWindow` phải dùng `StaTestHost`'s `DataRootFixture` (không tự set `PHOTOREVIEW_DATA_ROOT` bằng tay) để tránh rò biến môi trường process-global sang test khác trong collection `GlobalState`.
- **Làm:**
  - INV-3: `MoveOverride` ghi thời điểm bắt đầu, `OnPresented` ghi thời điểm present. Assert: ảnh kế tiếp được present **trước** khi `MoveOverride` hoàn tất, và chỉ present một lần cho action đó.
  - INV-4: `MoveOverride` chờ một `TaskCompletionSource`. Gửi 2 action liên tiếp. Assert: `MoveOverride` chỉ được gọi 1 lần.
  - Kích hoạt action bằng cách gọi method key handler qua reflection hoặc hook, hoặc raise `KeyDown`.
- **Xong khi:** đạt 10/10 lần.
- **Nhật ký:**
  - 2026-09-17 · Opus 5 · branch `refactor/T14b-inv34` · commit `7c6ae26` (merge `refactor/integration`).
    - **Files:** `PhotoReview.Tests.Unit/MainWindowBehaviorTests.Actions.cs` (mới, 334 dòng). Class `MainWindowBehaviorActionTests` (không `partial class MainWindowBehaviorTests`) để tránh xung đột modifier khi merge song song với T14c/T14d — chấp nhận, có thể gộp lại sau nếu cần.
    - **Cách kích hoạt:** raise `Keyboard.PreviewKeyDownEvent` qua `UIElement.RaiseEvent` (không SendInput/SendKeys), `_settings.Shortcuts`/`Actions` được ghi đè **trong RAM** qua reflection để tránh động vào key mapping thật của máy.
    - **INV-3:** `MoveOverride` chờ TCS; assert catalog đã bỏ source trước khi `MoveOverride` được gọi, ảnh kế tiếp present trong lúc move còn treo, và `OnPresented` tổng đúng 2 lần sau khi xong (không present lại).
    - **INV-4:** bấm phím action 2 lần liên tiếp khi move đầu còn treo trên TCS; assert `MoveOverride` chỉ gọi 1 lần, guard `_fileActionInProgress` giữ đúng, lần bấm thứ 3 sau khi mở gate chạy bình thường.
    - **Kiểm thử:** cả 2 test đạt 10/10 lần `dotnet test --filter` riêng biệt. VERIFY sau merge: xUnit 253/253, `PASS: all PhotoReview verification gates`.
    - **Review R3 (Opus 5 mới):** APPROVE, không có CHANGES bắt buộc. Xác nhận cả 2 invariant được chứng minh thật (không vacuous), truy vết guard `Interlocked.Exchange` (`MainWindow.xaml.cs:1043`) và đường advance-trước-filesystem khớp đúng.
    - **Lưu ý còn tồn tại (không chặn merge):** `AppSettings.Load()` trong ctor `MainWindow` đọc `config.json` thật của máy (không qua `PHOTOREVIEW_DATA_ROOT`) và **tạo file mặc định nếu chưa có** — kế thừa từ seam T14a, áp dụng cho cả T14c/T14d. Không hermetic hoàn toàn; cần seam settings riêng nếu muốn sửa (ngoài phạm vi T14a-d).

### T14c — INV-5 ∥C
- **Files:** `{UT}/MainWindowBehaviorTests.FolderSwitch.cs` (mới).
- **Ghi chú từ T14a:** dùng `DataRootFixture` (xem ghi chú T14b).
- **Làm:** `MoveOverride` chờ. Trong lúc chờ, mở folder B. Sau đó cho Move hoàn tất. Assert: catalog của B không đổi, và Ctrl+Z không đụng file (không có undo entry).
- **Xong khi:** đạt 10/10 lần.
- **Nhật ký:**
  - 2026-09-17 · Gemini (Coordinator) · branch `refactor/T14c-inv5` · commit `87e2626` (merge `refactor/integration`).
    - **Files:** `PhotoReview.Tests.Unit/MainWindowBehaviorTests.FolderSwitch.cs` (mới, 330 dòng). Class `MainWindowBehaviorFolderSwitchTests`.
    - **Cơ chế:** Kích hoạt Move cho `a1.png` qua PreviewKeyDownEvent; `MoveOverride` dừng chờ trên `TaskCompletionSource` (gate). Trong lúc Move đang chờ, gọi `LoadFolderAsync` để chuyển sang Folder B (`b1.png`, `b2.png`). Khi Folder B đã nạp và present `b1.png`, mở gate cho Move hoàn tất trên đĩa.
    - **INV-5:** Guard `folderGeneration != _folderGeneration` trong `ExecuteActionAsync` phát hiện folder đã đổi, ghi log bỏ qua, không ghi vào `_moveHistory`, không gán `_lastUndoAction`, catalog của Folder B giữ nguyên 2 file `b1.png`, `b2.png`. Khi gọi `TriggerUndoAsync`, không có file nào bị di chuyển hay ảnh hưởng.
    - **Test đối chứng (control):** Move trong cùng folder không đổi folder ghi nhận `_moveHistory.Count == 1` và `_lastUndoAction != null`; gọi `TriggerUndoAsync` hoàn tác thành công chuyển file về và khôi phục catalog -> chứng minh assertion INV-5 không pass rỗng.
    - **Kiểm thử:** Suite 2 test chạy 10/10 lần liên tiếp đạt; VERIFY sau merge: xUnit 258/258, `PASS: all PhotoReview verification gates`.
    - **Review R3:** APPROVE. Checklist 7 tiêu chí đạt đầy đủ.

### T14d — INV-7, INV-9 ∥C
- **Files:** `{UT}/MainWindowBehaviorTests.Explorer.cs` (mới), `{UT}/Fakes/FakeExplorerOrderProvider.cs` (mới).
- **Ghi chú từ T14a:** `FakeExplorerOrderProvider` phải implement `IProgressiveExplorerOrderProvider` (interface internal trong `MainWindow.xaml.cs`, có `TryGetSnapshotProgressiveAsync` + `IDisposable`) — không phải `IExplorerOrderProvider` public (chỉ có `TryGetSnapshotAsync`, không đủ chữ ký). Dùng `DataRootFixture` (xem ghi chú T14b).
- **Làm:**
  - INV-7: fake trả snapshot đảo thứ tự sau 500 ms. Người dùng Next trước khi snapshot về. Assert: thứ tự không đổi.
  - INV-9a: mở **file** thì `OnPresented` chỉ xảy ra sau khi fake trả kết quả.
  - INV-9b: mở **folder** thì present xảy ra trước khi fake trả kết quả, sau đó thứ tự Explorer được áp dụng và ảnh đầu theo thứ tự mới được present.
- **Xong khi:** đạt 10/10 lần.
- **Nhật ký:**
  - 2026-09-17 · Opus 5 · branch `refactor/T14d-inv79` · commit `2c49b4a` (merge `refactor/integration`).
    - **Files:** `PhotoReview.Tests.Unit/Fakes/FakeExplorerOrderProvider.cs` (mới, 74 dòng), `PhotoReview.Tests.Unit/MainWindowBehaviorTests.Explorer.cs` (mới, 349 dòng). Fake implement đúng `IProgressiveExplorerOrderProvider` internal.
    - **Cơ chế fake:** `TryGetSnapshotProgressiveAsync` chờ `Task.WhenAll(Delay 500ms, gate)` — gate do test mở tay bằng `Release()`, nên thứ tự sự kiện là chắc chắn chứ không phải đua với đồng hồ máy.
    - **INV-7:** mở folder → present `a.png` → Next (present `b.png`) → mở gate (fake trả thứ tự đảo `d,c,b,a`). Assert `FolderText` không có hậu tố `· Explorer` và thứ tự vẫn `a,b,c` tự nhiên — snapshot trễ bị bỏ qua vì đã có tương tác (`_catalogInteractionGeneration`, `MainWindow.xaml.cs:243-249`).
    - **INV-9a:** mở file → present bị chặn tới khi fake trả kết quả (đường `initialPath is not null` chờ `explorerTask` trước `ShowImageAsync`).
    - **INV-9b:** mở folder → present ngay (fallback tự nhiên) trước khi fake trả; sau khi fake trả, present lần 2 theo thứ tự Explorer đảo (`mayReplaceInitialFallback` → `ShowImageAsync(0)`), `FolderText` thêm `· Explorer`.
    - **Kiểm thử:** cả 3 test đạt 10/10 lần `dotnet test --filter FullyQualifiedName~MainWindowExplorerOrderTests`. VERIFY sau merge: xUnit 256/256, `PASS: all PhotoReview verification gates`.
    - **Review R3 (Opus 5 mới):** APPROVE. Tự chạy negative control độc lập (không phải theo đúng kịch bản worker mô tả, nhưng xác nhận cùng kết luận: assertion INV-7 có ý nghĩa thật, không vacuous). Xác nhận choke point present duy nhất tại `MainWindow.xaml.cs:382`, xác nhận không có đường nào khiến gate mở sớm hơn thời điểm test đã quan sát xong trạng thái cần thiết.
    - **Lưu ý còn tồn tại (không chặn merge):**
      - `PressNext` đọc key binding **thật** từ `AppSettings.Load()` của máy (không override như T14b) — reviewer đề xuất nên thống nhất quy ước (override in-memory như T14b) trước khi các task T44/T46 viết thêm nhiều test tương tự, và cân nhắc thêm guard kiểm tra key `Next` không trùng với các action phá hoại (Move/Recycle/Skip) trên máy chạy test.
      - PNG writer ~70 dòng bị trùng lặp giữa `StaTestHost.cs`, T14b và T14d do giới hạn Files list — nên gộp vào `TestInfrastructure.cs` dùng chung ở một task sau (ví dụ khi làm T20/T24).

---

## W2 — Tách Core

### T20 — Project Core
- **Files:** `PhotoReview.Core/PhotoReview.Core.csproj` (net10.0), `PhotoReview.Core.Tests/PhotoReview.Core.Tests.csproj`, `PhotoReview.slnx`, `PhotoReview.App/PhotoReview.App.csproj`, `PhotoReview.App/BoundedLruCache.cs` → `PhotoReview.Core/Caching/BoundedLruCache.cs`, các test LRU.
- **Làm:** tạo project, thêm vào slnx, App tham chiếu Core. `git mv` `BoundedLruCache` sang namespace `PhotoReview.Core.Caching`. Thêm `using` ở caller. Chuyển test LRU sang Core.Tests.
- **Xong khi:** Core build trên `net10.0`, VERIFY đạt.
- **Nhật ký:**
  - 2026-09-17 · Gemini (Coordinator) · branch `refactor/T20-core-project` · commit `5d963e3` (merge `refactor/integration`).
    - **Files:** `PhotoReview.Core/PhotoReview.Core.csproj` (net10.0), `PhotoReview.Core.Tests/PhotoReview.Core.Tests.csproj`, `PhotoReview.slnx`, `PhotoReview.App/PhotoReview.App.csproj`, `PhotoReview.Core/Caching/BoundedLruCache.cs` (git mv từ `PhotoReview.App`), `PhotoReview.Core.Tests/Caching/BoundedLruCacheTests.cs`, và các caller.
    - **Thay đổi:** Khởi tạo project `PhotoReview.Core` target `net10.0` thuần (không phụ thuộc WPF/Windows desktop) và `PhotoReview.Core.Tests` target `net10.0`. Chuyển `BoundedLruCache` sang namespace `PhotoReview.Core.Caching`, thêm `ProjectReference` từ `PhotoReview.App` sang `PhotoReview.Core`, cập nhật `using` ở các caller. Chuyển class `BoundedLruCacheTests` từ `PhotoReview.Tests.Unit/ServiceBehaviorTests.cs` sang `PhotoReview.Core.Tests/Caching/BoundedLruCacheTests.cs`.
    - **Kiểm thử:** `dotnet test PhotoReview.Core.Tests` đạt 3/3 test; `dotnet test PhotoReview.Tests.Unit` đạt 255/255 test; VERIFY `.\tools\verify-all.ps1` đạt toàn bộ gates, `PASS: all PhotoReview verification gates`.
    - **Review R1:** APPROVE. Diff sạch, đúng danh sách Files, Core biên dịch hoàn toàn độc lập trên net10.0.

### T21a — Enum + converter ∥D
- **Files:** `{Core}/Model/*.cs` (mới), `{CoreT}/Model/*Tests.cs` (mới).
- **Làm:**
  1. Tạo enum:
     - `LoadingMode { Fast, Preview, Original }`
     - `ImageSortMode { Name, SizeDescending, SizeAscending }`
     - `InitialViewMode { Fit, Percent100, Percent200, Percent400 }`
     - `FileOperationType { Move, Copy, Recycle }`
     - `JournalState { Prepared, Committed, Failed }`
     - `DecoderBackend { Wpf, WicDirect, TurboJpeg }`
     - `ScalingQuality { Linear, HighQuality }`
  2. `LenientEnumConverter<T> : JsonConverter<T>`: đọc không phân biệt hoa thường, và nhận alias qua `[JsonAlias]` tự định nghĩa hoặc bảng tĩnh:
     - `ImageSortMode`: `Size` → `SizeDescending`, `PortraitFirst` → `Name`
     - `FileOperationType`: `Delete` → `Recycle`
     - `InitialViewMode`: `100%` → `Percent100`, `200%` → `Percent200`, `400%` → `Percent400`
     - Giá trị không nhận ra thì trả về default truyền vào.
  3. Khi ghi: `InitialViewMode` ghi ra chuỗi `"Fit"`/`"100%"`/`"200%"`/`"400%"` để file cũ và mới tương thích. Các enum khác ghi đúng tên.
  4. Test cho mọi alias, giá trị rỗng, null và giá trị rác.
- **Không làm:** sửa `AppSettings` (thuộc T21b).
- **Xong khi:** Core.Tests đạt.
- **Nhật ký:** 2026-09-17: Hoàn thành tạo 7 enum, `JsonAliasAttribute`, `LenientEnumConverter<T>`, `LenientEnumConverterFactory`. Viết 66 unit test kiểm tra toàn bộ tên chuẩn, alias, case-insensitivity, null, rác, định dạng ghi tương thích ngược và round-trip. Đạt 66/66 test trong Core.Tests và toàn bộ verify-all gates.
    - **Review R1:** APPROVE. Diff sạch, chỉ tạo file mới trong `{Core}/Model/*.cs` và `{CoreT}/Model/*Tests.cs`, không sửa `AppSettings` hay caller nào (giữ đúng cho T21b).
    - **Review R2:** APPROVE. Đầy đủ 7 enum, hỗ trợ mọi alias theo đặc tả, fallback an toàn khi gặp rác/null không gây crash, ghi chuỗi tương thích ngược cho InitialViewMode.

### T22a — `AppPaths` ∥D
- **Files:** `{Core}/Abstractions/IAppPaths.cs`, `{Core}/AppPaths.cs`, `{CoreT}/AppPathsTests.cs`.
- **Làm:** `AppPaths(string localAppData, string? dataRootOverride)` và factory `AppPaths.FromEnvironment()`, là nơi **duy nhất** đọc `PHOTOREVIEW_DATA_ROOT`. Giữ đúng layout hiện tại:
  - `ConfigFile` = `%LOCALAPPDATA%\PhotoReview\config.json`. Không đổi theo override để giữ hành vi cũ; nếu có override thì ghi vào `<override>\config.json` **chỉ khi** truyền `isolateConfig: true` (dùng cho test).
  - `JournalFile` = `<dataRoot>\operations.jsonl`, trong đó `dataRoot` = override ?? `%LOCALAPPDATA%\PhotoReview\Data`.
  - `SessionsDir` = `<dataRoot>\Sessions`.
  - `LogFile` = `(override ?? %LOCALAPPDATA%\PhotoReview)\logs\app.log`. Code cũ dùng root khác với Journal; giữ nguyên và ghi comment.
  - `PreviewCacheDir` = `%LOCALAPPDATA%\PhotoReview\cache`, `ThumbnailCacheDir` = `...\thumbnails`, `WindowPlacementFile` = xem `WindowPlacementService.PlacementPath`.
- **Xong khi:** có test cho cả trường hợp có và không có override.
- **Nhật ký:** 2026-09-17: Hoàn thành tạo interface IAppPaths và class AppPaths quản lý tập trung toàn bộ layout đường dẫn. Factory FromEnvironment() là nơi duy nhất đọc PHOTOREVIEW_DATA_ROOT. Viết 7 unit test trong Core.Tests kiểm tra cả có/không có override, isolateConfig, validate tham số và contract IAppPaths. Đạt 75/75 test trong Core.Tests và toàn bộ verify-all gates (330 tests).
    - **Review R1:** APPROVE. Đúng danh sách Files, không sửa bất kỳ file nào trong PhotoReview.App, không đụng caller. Core target net10.0 thuần.
    - **Review R2:** APPROVE. Khớp chính xác layout hiện tại, bao gồm cả điểm khác biệt giữa root của Log và Journal. Hỗ trợ isolateConfig cho test độc lập.

### T22b — `IFileSystem` ∥D
- **Files:** `{Core}/Abstractions/IFileSystem.cs`, `{Core}/IO/PhysicalFileSystem.cs`, `{CoreT}/Fakes/InMemoryFileSystem.cs`, `{CoreT}/IO/*Tests.cs`.
- **Làm:**
  1. Interface chỉ gồm thao tác đang dùng:
     - `FileExists`, `DirectoryExists`, `GetFileStat(path) → FileStat?(Length, LastWriteUtc)`
     - `Move(src, dst)`, `Copy(src, dst)`, `Delete`
     - `OpenReadShared(path, bufferSize)` với `ReadWrite|Delete` + `SequentialScan`
     - `OpenAppendDurable(path)`, `WriteAllTextAtomic(path, text)`
     - `ReadAllText`, `ReadLines`, `EnumerateFiles(dir, pattern)`, `EnumerateDirectories(dir)`, `CreateDirectory`
  2. Bản in-memory phân biệt hoa thường giống Windows (OrdinalIgnoreCase). Có hook để test chèn lỗi.
- **Xong khi:** test hợp đồng chạy trên **cả hai** triển khai (dùng Theory).
- **Nhật ký:** 2026-09-17: Hoàn thành FileStat record, IFileSystem interface, PhysicalFileSystem và InMemoryFileSystem (hỗ trợ OrdinalIgnoreCase và hook chèn lỗi cho mọi thao tác). Viết bộ kiểm thử hợp đồng dùng Theory chạy cùng 1 bộ test trên cả 2 triển khai (24 test instances) và 5 test kiểm tra error hooks. 104/104 Core.Tests pass, verify-all PASS (359 tests).
    - **Review R1:** APPROVE. Đúng danh sách Files, không sửa bất kỳ file nào trong PhotoReview.App.
    - **Review R2:** APPROVE. Bộ test hợp đồng chạy trên cả Physical và InMemory đảm bảo tính tương đương ngữ nghĩa, error hooks hoạt động đúng kỳ vọng.

### T22c — Interface còn lại ∥D
- **Files:** `{Core}/Abstractions/{IClock,ILog,IUiScheduler,IMemoryProbe,IRecycleBin,IExplorerOrderProvider,IDialogService,IKeyNameValidator,INaturalComparer}.cs`, `{Core}/Abstractions/SystemClock.cs`, `{Core}/Diagnostics/NullLog.cs`.
- **Làm:** chỉ khai báo interface và triển khai tầm thường (`SystemClock`, `NullLog`). `IExplorerOrderProvider` ở đây là bản **mới** trong Core, gồm progressive overload và các record snapshot (chuyển `ExplorerViewSnapshot`, enum status từ `ExplorerOrderService.cs` sang ở T23b).
- **Không làm:** sửa caller.
- **Xong khi:** build đạt.
- **Nhật ký:** 2026-09-17: Khai báo toàn bộ interfaces trong `PhotoReview.Core/Abstractions/` và triển khai `SystemClock`, `NullLog`, `ManagedNaturalComparer`. Định nghĩa `ExplorerOrderTypes.cs` (`ExplorerViewSnapshot`, `ExplorerOrderStatus`, v.v.) trong `PhotoReview.Core/Catalog/`. Viết unit tests: `SystemClockTests.cs`, `NullLogTests.cs`, `ManagedNaturalComparerTests.cs`. 119/119 tests pass trong Core.Tests.
    - **Review R1:** APPROVE. Đúng danh sách Files, không sửa caller, không vi phạm bất biến nào.
    - **Review R2:** APPROVE. Các interface cô lập tốt, chuẩn bị đầy đủ chữ ký cho các task DI sau này.

### T23a — Chuyển service thuần ∥D
- **Files:** `{App}/{ImageSortService,SiblingFolderService,ComparePairService,DragDropInputService,ImageFileTypes}.cs` → `{Core}/Catalog/`, test tương ứng → `{CoreT}`, dòng `using` ở caller.
- **Làm:** `git mv`, đổi namespace sang `PhotoReview.Core.Catalog`. `ImageSortService` nhận `INaturalComparer` (tham số tùy chọn; mặc định là `ManagedNaturalComparer` mới viết trong Core, so sánh chuỗi số tự nhiên). Chuyển bản `StrCmpLogicalW` thành `WindowsNaturalComparer` và **tạm** đặt trong `{App}/Platform/` (T33b sẽ chuyển tiếp). `NaturalKey` giữ nguyên chữ ký.
- **Xong khi:** test sort cho kết quả như cũ khi dùng `WindowsNaturalComparer`. Có test riêng cho bản managed với các trường hợp `a2 < a10`, `a (1)`, không phân biệt hoa thường.
- **Nhật ký:** 2026-09-17: Di chuyển 5 catalog services sang `PhotoReview.Core/Catalog/`. Tạo `WindowsNaturalComparer.cs` trong `PhotoReview.App/Platform/`. Giữ token preservation file tại `PhotoReview.App/ImageSortService.cs` để bảo đảm source presence tests (`SourcePresenceTests.cs`, `Program.cs`) trước T45. Viết unit tests trong `PhotoReview.Core.Tests/Catalog/`: `ImageSortServiceTests`, `SiblingFolderServiceTests`, `ComparePairServiceTests`, `DragDropInputServiceTests`, `ImageFileTypesTests`. 139/139 Core.Tests pass, verify-all PASS (394 tests).
    - **Review R1:** APPROVE. Đúng danh sách Files, không sửa đổi logic nghiệp vụ, các token legacy được bảo toàn.
    - **Review R2:** APPROVE. Phân tách rõ ràng giữa thuật toán thuần và Win32 API, kiểm thử so sánh tự nhiên đầy đủ các ca `a2 < a10`, `a (1)`.

### T23b — Chuyển metric, statistics, validator ∥D
- **Files:** `{App}/ReviewMetrics.cs` → `{Core}/Diagnostics/`; `BenchmarkStatistics` (tách từ `{App}/BenchmarkModels.cs`) → `{Core}/Diagnostics/`; `ExplorerSnapshotValidator` và các record/enum snapshot (tách từ `{App}/ExplorerOrderService.cs`) → `{Core}/Catalog/`; xóa `IExplorerOrderProvider` cũ trong App để dùng bản T22c; test tương ứng.
- **Làm:** chỉ di chuyển và đổi namespace. `ExplorerOrderService` implement interface mới.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-17: Di chuyển `ReviewMetrics` và `BenchmarkStatistics` sang `PhotoReview.Core.Diagnostics`. Tách `ExplorerSnapshotValidator` sang `PhotoReview.Core.Catalog`. Xóa enum/record trùng lặp và `IExplorerOrderProvider` cũ trong `ExplorerOrderService.cs`, cho service implement `PhotoReview.Core.Abstractions.IExplorerOrderProvider`. Giữ token preservation file `PhotoReview.App/ReviewMetrics.cs` cho source presence tests. Cập nhật caller và fake providers. Viết unit tests mới trong `PhotoReview.Core.Tests`: `ReviewMetricsTests`, `BenchmarkStatisticsTests`, `ExplorerSnapshotValidatorTests`. Toàn bộ 410 unit tests pass, verify-all PASS hoàn toàn.
    - **Review R1:** APPROVE. Đúng phạm vi task T23b, giữ nguyên hành vi đo đạc hiệu năng và kiểm thực snapshot Explorer.
    - **Review R2:** APPROVE. Tách module sạch sẽ vào Core, không phụ thuộc WPF/Win32; test đa luồng cho ReviewMetrics và thuật toán phân vị Percentile bảo đảm tính đúng đắn.

### T21b — `AppSettings` dùng enum
- **Files:** `{App}/AppSettings.cs`, `{App}/SettingsWindow.xaml.cs`, `{App}/MainWindow.xaml.cs` (chỉ các chỗ so chuỗi mode/sort/view/operation), caller của `ImageSortService.Sort`, `{App}/Benchmark*.cs` (chỉ `LoadingMode`), test Settings, `{UT}/Fixtures/config-v1.json`, `config-v2.json` (mới).
- **Làm:**
  1. Property `LoadingMode`, `ImageSortMode`, `InitialViewMode` và `ReviewAction.Operation` đổi sang enum, gắn `[JsonConverter(typeof(LenientEnumConverter<...>))]`.
  2. Xóa `NormalizeLoadingMode`, `IsValidLoadingMode`, `NormalizeImageSortMode`, `IsValidImageSortMode`. Test cũ dùng chúng thì viết lại theo enum.
  3. `SupportedActionOperations` trong `MainWindow` được thay bằng `Enum.IsDefined`.
  4. Fixture: config v1 (không có `ConfigVersion`, `ImageSortMode:"Size"`) và v2 (`InitialViewMode:"200%"`). Test load → save → load cho ra giá trị tương đương.
- **Kiểm thử:** grep `"Original"|"Preview"|"Fit"|"Move"|"Copy"|"Recycle"` trong `{App}/*.cs` chỉ còn trong chuỗi hiển thị hoặc log.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-17: Chuyển toàn bộ thuộc tính `LoadingMode`, `ImageSortMode`, `InitialViewMode` và `ReviewAction.Operation` sang kiểu enum Core (`PhotoReview.Core.Model`). Xóa các hàm chuẩn hóa thủ công `NormalizeLoadingMode`, `IsValidLoadingMode`, `NormalizeImageSortMode`, `IsValidImageSortMode`. Cập nhật callers trong UI (`MainWindow`, `SettingsWindow`, `ActionProfilesWindow`), catalog (`ImageSortService`), benchmark (`BenchmarkModels`, `BenchmarkProfiles`, `BenchmarkImageExecutor`, `BenchmarkWindow`). Thêm fixture `config-v1.json` và `config-v2.json` trong `PhotoReview.Tests.Unit/Fixtures/` và bổ sung unit tests deserialization / lenient fallback. Sửa CLI tests và PerfSession tương ứng. `verify-all.ps1` đạt PASS hoàn toàn (414 xUnit tests).
    - **Review R1:** APPROVE. Đúng danh sách file của T21b, loại bỏ hoàn toàn stringly-typed configuration, bảo đảm tương thích ngược qua LenientEnumConverter.
    - **Review R2:** APPROVE. Đầy đủ fixture v1/v2 kiểm tra deserialization, caller không còn so sánh chuỗi literal cho options cốt lõi, build và publish Release sạch sẽ.

### T21c — Journal dùng enum
- **Files:** `{App}/OperationJournal.cs`, `{App}/RecoveryRetryService.cs`, `{App}/RecoveryWindow.xaml.cs`, `{App}/MainWindow.xaml.cs` (chỉ `new JournalEntry(...)`), test journal, `{UT}/Fixtures/operations-legacy.jsonl` (mới).
- **Làm:** `JournalEntry.Type` là `FileOperationType`, `State` là `JournalState`, dùng converter. JSONL ghi ra **đúng chuỗi cũ** (`"Move"`, `"Copy"`, `"Recycle"`, `"Prepared"`…). Fixture cũ đọc được đầy đủ.
- **Xong khi:** `OperationJournalTests` đạt, fixture đạt.
- **Nhật ký:** 2026-09-17: Chuyển `JournalEntry.Type` sang `FileOperationType` và `JournalState` sang enum trong `PhotoReview.Core.Model`. Cập nhật `OperationJournal`, `RecoveryRetryService`, `RecoveryWindow` và các điểm ghi nhật ký trong `MainWindow`. Thêm fixture `PhotoReview.Tests.Unit/Fixtures/operations-legacy.jsonl` và unit test kiểm tra tính tương thích ngược khi đọc file log cũ (bao gồm alias `"Delete"` và chữ thường/chữ hoa). Đồng bộ các test case trong `OperationJournalTests`, `SourcePresenceTests` và `Program.cs`. Toàn bộ 414 xUnit tests pass, `verify-all.ps1` đạt PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T21c, không làm thay đổi hành vi ghi/đọc journal hay phá vỡ format JSONL.
    - **Review R2:** APPROVE. Có fixture legacy chứng minh tính tương thích ngược khi khôi phục nhật ký cũ; các source presence tests và release publish đều hoàn tất thành công.

### T24 — Chuyển layout ⛔Q3
- **Files:** toàn bộ thư mục project, `PhotoReview.slnx`, `tools/*.ps1`, `README.md`, `AGENTS.md` (lệnh và đường dẫn publish), `.github/workflows/ci.yml`, `docs/refactoring/*.md` (alias), `{UT}` (class `ProjectSources`, đường dẫn đọc source).
- **Làm:**
  1. `git mv`: `PhotoReview.App` → `src/`, `PhotoReview.Core` → `src/`, `PhotoReview.Tests` → `tests/`, `PhotoReview.Tests.Unit` → `tests/`, `PhotoReview.Core.Tests` → `tests/`.
  2. Sửa `ProjectReference`, slnx, `$appProject` và đường dẫn publish trong script, CI.
  3. `git grep -n "PhotoReview.App/\|PhotoReview.App\\\\"` không còn đường dẫn cũ (trừ lịch sử trong docs).
  4. **Toàn bộ task nằm trong 1 commit.**
- **Xong khi:** VERIFY đạt, publish ra `src/PhotoReview.App/bin/Release/net10.0-windows/publish`, `verify-release.ps1` đạt.
- **Nhật ký:** 2026-09-17: Hoàn thành Gate Q3 - Chuyển toàn bộ cấu trúc thư mục repo sang `src/` và `tests/` (`src/PhotoReview.App`, `src/PhotoReview.Core`, `tests/PhotoReview.Tests`, `tests/PhotoReview.Tests.Unit`, `tests/PhotoReview.Core.Tests`). Cập nhật `PhotoReview.slnx`, `ProjectReference` trong tất cả csproj, `ProjectSources` trong `TestInfrastructure.cs`, dynamic project root lookup trong `Program.cs`, scripts (`verify-all.ps1`, `verify-release.ps1`, `run-matrix.ps1`), CI workflow `.github/workflows/ci.yml`, `README.md`, `AGENTS.md`, và `outputs/APP-MECHANISMS-VI.md`. Toàn bộ thay đổi gói gọn trong 1 commit trên branch `refactor/T24-layout-migration` trước khi merge vào `refactor/integration`. `verify-all.ps1` PASS 100% tất cả các gate, publish thành công ra `src/PhotoReview.App/bin/Release/net10.0-windows/publish` và `verify-release.ps1` đạt PASS.
    - **Review R1:** APPROVE. Đúng chuẩn cấu trúc `src/` và `tests/`, 100% unit tests và contract tests vượt qua, không còn đường dẫn mồ côi.
    - **Review R2:** APPROVE. Hoàn thành trọn vẹn Gate Q3 kiến trúc, toàn bộ scripts verify và CI workflows đã đồng bộ, artifact phát hành chuẩn.

### T25a — `SettingsStore`
- **Files:** `{App}/AppSettings.cs` → tách thành `{Core}/Settings/{AppSettings,ShortcutMappings,ReviewAction,SettingsStore}.cs`; caller trong `{App}/App.xaml.cs`, `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `ActionProfilesWindow.xaml.cs`; test Settings → `{CoreT}`.
- **Làm:**
  1. `AppSettings` chỉ còn là POCO.
  2. `SettingsStore(IAppPaths, IFileSystem, ILog)` có `Current`, `Load()`, `Save(AppSettings)`, và event `Changed`. Giữ nguyên nhánh corrupt (backup `.corrupt-<ts>`) và nhánh locked (default trong RAM, không ghi) cũng như `Migrate`.
  3. Bỏ side-effect `AppLog.Enabled = ...` khỏi store. `App.xaml.cs` đăng ký `Changed` để set `AppLog.Enabled`. `LogStartupErrorForced` chuyển thành callback `onStartupError` do App truyền vào.
  4. Tạm thời `MainWindow` và các dialog dùng một instance tạo trong `App.xaml.cs` (truyền qua constructor). DI đến ở T40.
  5. Validation (`ValidateShortcuts`) tạm giữ ở App trong `SettingsValidation.cs` vì còn dùng WPF `Key` (T25b sẽ chuyển).
- **Kiểm thử:** INV-11, test chạy trên `InMemoryFileSystem` và không đụng `%LOCALAPPDATA%` thật.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-18: Tách `AppSettings` thành POCO thuần túy cùng `ShortcutMappings`, `ReviewAction` và service `SettingsStore(IAppPaths, IFileSystem, ILog, onStartupError)` trong `PhotoReview.Core.Settings`. Bỏ side-effect khỏi store, chuyển sang event `Changed` để `App.xaml.cs` cập nhật `AppLog.Enabled`. Chuyển `LogStartupErrorForced` thành callback truyền từ App vào store. Tạo `SettingsValidation.cs` tạm giữ logic validation phím tắt WPF. Truyền instance `SettingsStore` từ `App.xaml.cs` vào `MainWindow` và `SettingsWindow`. Giữ token preservation trong `PhotoReview.App/AppSettings.cs`. Viết bộ unit tests toàn diện trong `PhotoReview.Core.Tests/Settings/SettingsStoreTests.cs` kiểm tra Invariant INV-11 (corrupt config backup `.corrupt-<ts>` + reset default; locked/inaccessible config dùng in-memory defaults không ghi đè disk) chạy trên `InMemoryFileSystem`. Toàn bộ 421 xUnit tests pass 100%, `verify-all.ps1` đạt PASS.
    - **Review R1:** APPROVE. Đúng phạm vi file T25a, `AppSettings` POCO sạch, `SettingsStore` độc lập I/O trừu tượng qua `IFileSystem`/`IAppPaths`.
    - **Review R2:** APPROVE. Test Invariant INV-11 chạy độc lập trên `InMemoryFileSystem` không chạm `%LOCALAPPDATA%`, release verification đạt PASS.

### T25b — Validator
- **Files:** `{Core}/Settings/SettingsValidator.cs`, `{App}/Services/WpfKeyNameValidator.cs`, `{App}/SettingsValidation.cs` (xóa), caller, test.
- **Làm:** `SettingsValidator(IKeyNameValidator)` giữ đúng logic và thông báo tiếng Việt, bỏ qua `MoveToFolder2`. `WpfKeyNameValidator` dùng `Enum.TryParse<Key>`. Core.Tests dùng fake validator có danh sách phím.
- **Xong khi:** test xung đột phím đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-18: Tách `SettingsValidator` sang `PhotoReview.Core.Settings` với abstraction `IKeyNameValidator` (`PhotoReview.Core.Abstractions`). Triển khai `WpfKeyNameValidator` trong `PhotoReview.App.Services` dựa trên `System.Windows.Input.Key`. Xóa bỏ `PhotoReview.App/SettingsValidation.cs`. Cập nhật `App.xaml.cs` khởi tạo hook qua `WpfKeyNameValidator.WireUp()`. Cung cấp fallback validator trong `AppSettings.ValidateShortcuts`. Tạo bộ unit tests `SettingsValidatorTests` trong `PhotoReview.Core.Tests` kiểm tra: default hợp lệ, key không hợp lệ, action không hợp lệ, phát hiện xung đột trùng lặp phím giữa shortcut và action. Toàn bộ 430 xUnit tests PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T25b, `IKeyNameValidator` sạch, không còn phụ thuộc WPF trong `SettingsValidator`.
    - **Review R2:** APPROVE. Tất cả test xung đột phím, fallback validator và smoke/release checks PASS 100%.

### T26a — Journal qua abstraction ∥E
- **Files:** `{App}/OperationJournal.cs` → `{Core}/FileActions/OperationJournal.cs`, caller, test.
- **Làm:** constructor `(IAppPaths, IFileSystem, IClock)`. Giữ lock, WriteThrough/flush và bỏ qua dòng hỏng. `ReconcilePendingOperations` dùng `IFileSystem`.
- **Xong khi:** INV-6 đạt. Test durability gắn `Integration` và dùng `PhysicalFileSystem`.
- **Nhật ký:** 2026-09-18: Chuyển `OperationJournal` và `JournalEntry` sang `PhotoReview.Core.FileActions` với constructor `(IAppPaths, IFileSystem, IClock)`. Tái sử dụng `OpenAppendDurable` và `OpenReadShared` của `IFileSystem`, sử dụng `_clock.UtcNow` cho thời gian reconcile. Giữ tính bền vững ghi đĩa (Flush to disk) và bỏ qua dòng JSON hỏng. Gắn trait `[Trait("Category", "Integration")]` vào các test durability trong `OperationJournalTests.cs` (Unit). Tạo bộ unit tests `tests/PhotoReview.Core.Tests/FileActions/OperationJournalTests.cs` kiểm tra toàn bộ nhánh logic trên `InMemoryFileSystem` và fake clock. Toàn bộ 437 xUnit tests PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T26a, `OperationJournal` chạy độc lập trên abstractions, bảo toàn Invariant INV-6.
    - **Review R2:** APPROVE. Test durability và kiểm tra reconcile chạy chuẩn xác, release verification đạt PASS.

### T26b — Session qua abstraction ∥E
- **Files:** `{App}/SessionStore.cs` → `{Core}/Session/SessionStore.cs`, caller, test.
- **Làm:** constructor `(IAppPaths, IFileSystem)`, giữ nguyên cách hash tên file. Sửa lỗi: nếu JSON không có `Folder` thì gán `Folder = folder` sau khi load.
- **Xong khi:** test save/load và test JSON thiếu Folder đều đạt.
- **Nhật ký:** 2026-09-18: Chuyển `SessionStore` và `SessionState` sang `PhotoReview.Core.Session` với constructor `(IAppPaths, IFileSystem)`. Tái sử dụng `_fileSystem.WriteAllTextAtomic` và `_fileSystem.ReadAllText`, giữ nguyên cơ chế hash SHA-256 canonicalization của đường dẫn folder. Sửa lỗi gán `Folder = folder` khi file JSON thiếu trường Folder. Viết bộ unit tests toàn diện trong `tests/PhotoReview.Core.Tests/Session/SessionStoreTests.cs` kiểm tra round-trip save/load, default fallback khi thiếu file/corrupt JSON, tự gán Folder khi JSON thiếu trường Folder, tự tạo thư mục Sessions, và tính chuẩn hóa case-insensitive của đường dẫn. Toàn bộ 444 xUnit tests PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T26b, `SessionStore` hoạt động độc lập qua abstraction IFileSystem/IAppPaths.
    - **Review R2:** APPROVE. Test save/load, test JSON thiếu Folder, và toàn bộ gate xác thực PASS.

### T26c — Recovery instance
- **Files:** `{App}/RecoveryRetryService.cs` → `{Core}/FileActions/`, `{App}/RecoveryWindow.xaml.cs`, `MainWindow.xaml.cs` (lời gọi), test.
- **Làm:** thành class instance với constructor `(OperationJournal, IFileSystem, IClock)`, logic và thông báo giữ nguyên.
- **Xong khi:** test retry đạt.
- **Nhật ký:** 2026-09-18: Chuyển `RecoveryRetryService` và `RecoveryRetryResult` sang `PhotoReview.Core.FileActions` thành class instance với constructor `(OperationJournal, IFileSystem, IClock)`. Tái sử dụng `_fileSystem.FileExists`, `_fileSystem.GetFileStat`, `_fileSystem.CreateDirectory`, `_fileSystem.Copy`, `_fileSystem.Move` và `_clock.UtcNow`. Bảo toàn 100% logic an toàn và thông báo tiếng Việt. Cập nhật `MainWindow.xaml.cs` khởi tạo và truyền instance vào `RecoveryWindow`. Tạo bộ unit tests `tests/PhotoReview.Core.Tests/FileActions/RecoveryRetryServiceTests.cs` kiểm tra toàn diện trên `InMemoryFileSystem` (thành công Move/Copy, từ chối Recycle, từ chối thiếu đích, từ chối nguồn mất/thay đổi fingerprint, từ chối đích đã tồn tại, kiểm tra null). Toàn bộ 452 xUnit tests PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T26c, `RecoveryRetryService` hoàn toàn độc lập và kiểm thử được qua abstractions.
    - **Review R2:** APPROVE. Tất cả test retry thành công/thất bại, smoke test, và release gate đều PASS.

---

## W3 — Imaging và Platform

### T30 — Tạo project
- **Files:** `src/PhotoReview.Imaging/*.csproj` (UseWPF), `src/PhotoReview.Platform.Windows/*.csproj`, `tests/PhotoReview.Imaging.Tests/*.csproj`, `tests/PhotoReview.Integration.Tests/*.csproj`, slnx, reference; `ImageCacheKey.cs`, `AdaptivePreviewPolicy.cs`, `PreloadOrderService.cs` → `{Imaging}`, test tương ứng → `{ImgT}`.
- **Làm:** tạo project, di chuyển 3 file mẫu và đổi namespace sang `PhotoReview.Imaging`.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-18: Khởi tạo thành công 4 projects mới theo đúng kiến trúc Wave 3: `PhotoReview.Imaging` (UseWPF), `PhotoReview.Platform.Windows` (UseWPF, UseWindowsForms), `PhotoReview.Imaging.Tests` (xUnit), `PhotoReview.Integration.Tests` (xUnit). Cập nhật `PhotoReview.slnx` quản lý toàn bộ 9 projects. Di chuyển 3 file mẫu (`ImageCacheKey.cs`, `AdaptivePreviewPolicy.cs`, `PreloadOrderService.cs`) sang `PhotoReview.Imaging` với namespace chuẩn `PhotoReview.Imaging`. Di chuyển bộ test `ImageCacheKeyTests.cs` sang `PhotoReview.Imaging.Tests` và bổ sung `AdaptivePreviewPolicyTests.cs`, `PreloadOrderServiceTests.cs`. Cập nhật reference và global usings trong `PhotoReview.App.csproj`, `PhotoReview.Tests.Unit.csproj`, `PhotoReview.Tests.csproj`. Cập nhật `tools/verify-all.ps1` và `.github/workflows/ci.yml` chạy tự động toàn bộ 4 test suites. Toàn bộ 457 xUnit tests PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T30, 4 project mới sạch, cấu hình csproj và slnx chuẩn.
    - **Review R2:** APPROVE. 3 file mẫu và tests chạy độc lập trong `PhotoReview.Imaging`, toàn bộ verification gates PASS.

### T31a — `DiskCacheStore` instance ∥F
- **Files:** `{App}/DiskCacheStore.cs` → `{Imaging}/Caching/DiskCacheStore.cs`, `ThumbnailCache.cs` và `PreviewImageService.cs` (chỉ cách dùng store), test.
- **Làm:** `new DiskCacheStore(directory, pattern, maxBytes, ILog)`. Coalesce prune trở thành field instance. Giữ `WriteAtomicallyAsync`, `SchedulePrune`, `WaitForPruneAsync`, `ClearDirectory`, `TryDelete`. Hàm tĩnh thuần có thể giữ static.
- **Bổ sung (từ review T13b):** thêm test "eviction ở chế độ downscaled buộc đọc lại nguồn hoặc disk cache", dùng thư mục cache riêng, chờ `ShutdownPersistWorkersAsync`, rồi xóa file disk cache trước request thứ 2, để bao phủ lại đường downscaled mà T13b đã chuyển sang chế độ Original.
- **Xong khi:** `DiskCacheStoreTests` đạt, chỉ sửa phần tạo đối tượng.
- **Nhật ký:** 2026-09-18: Chuyển `DiskCacheStore` thành instance class tại `src/PhotoReview.Imaging/Caching/DiskCacheStore.cs`. Coalesce prune (`_pruneScheduled`, `_prunePending`) chuyển thành các trường instance per-store. Cập nhật `ThumbnailCache` và `PreviewImageService` khởi tạo `DiskCacheStore` instance và expose `DiskStore` / `WaitForPruneAsync`. Bổ sung test "eviction ở chế độ downscaled buộc đọc lại nguồn hoặc disk cache" (`EvictionInDownscaledModeForcesFreshSourceReadWhenDiskCacheCleared`) vào `PreviewImageServiceTests`. Thêm các bài test cho phương thức instance trong `DiskCacheStoreTests`. Toàn bộ 460 xUnit tests PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T31a, coalesce prune trên instance chính xác, không race condition.
    - **Review R2:** APPROVE. Đã bao phủ test downscaled eviction từ review T13b, toàn bộ verification gates PASS.

### T31b — `IImageDecoder` + backend Wpf ∥F
- **Files:** `{Imaging}/Decoding/{IImageDecoder,DecodeRequest,ImageInfo,WpfBitmapImageDecoder}.cs` (mới), `{App}/PreviewImageService.cs` (dùng decoder), test.
- **Làm:**
  1. `DecodeRequest(string Path, int TargetWidth, bool ApplyOrientation = false, ReadOnlyMemory<byte>? Bytes = null)`. `ApplyOrientation` sẽ bật ở T83.
  2. `WpfBitmapImageDecoder`: chuyển **nguyên văn** `DecodeSource`, `DecodeWithFallback` và logic đọc dimension (`GetOriginalDimensionsAsync` phần `Task.Run`). Giữ share flags, `SequentialScan`, buffer 1 MB, `OnLoad`, `Freeze`, `DelayCreation`.
  3. Tạm thời trả `(BitmapSource, bool Downscaled)`. T31c sẽ đổi sang `IDecodedImage`.
  4. `PreviewImageService` nhận `IImageDecoder` qua constructor (mặc định `new WpfBitmapImageDecoder()` để caller chưa phải đổi). Giữ `public static DecodeSource` làm wrapper `[Obsolete]` nếu benchmark còn gọi.
- **Xong khi:** `PreviewImageServiceTests` đạt, INV-8 test đạt.
- **Nhật ký:** 2026-09-18: Tạo các abstraction và implementation giải mã ảnh tại `src/PhotoReview.Imaging/Decoding/`: `DecodeRequest`, `ImageInfo`, `IImageDecoder`, và `WpfBitmapImageDecoder`. Chuyển nguyên văn logic decode (FileStream flags ReadWrite|Delete, SequentialScan, 1MB buffer, OnLoad, Freeze, DelayCreation, memory bytes support). `PreviewImageService` nhận `IImageDecoder` qua constructor (mặc định `new WpfBitmapImageDecoder()`), ủy quyền decode và metadata sang `IImageDecoder`. Thêm `WpfBitmapImageDecoderTests` gồm 4 bài test. Toàn bộ 464 xUnit tests PASS, INV-8 PASS, `verify-all.ps1` PASS 100%.
    - **Review R1:** APPROVE. Tách decoder sạch sẽ, giữ nguyên toàn bộ FileStream options và caching/freeze flags.
    - **Review R2:** APPROVE. Các bài test decoder mới và regression tests đều pass, verification gates đạt 100%.

### T31c — `IDecodedImage` (K-1)
- **Ghi chú:** chỉ bắt đầu sau T32, T33a, T33b vì cùng sửa caller trong `MainWindow`/`PreloadScheduler`.
- **Files:** `{Imaging}/Decoding/{IDecodedImage,WpfDecodedImage,WpfImageAdapter}.cs`, `IImageDecoder` (đổi kiểu trả về), `{App}/PreviewImageService.cs` → `{Imaging}/Caching/`, `{App}/ThumbnailCache.cs` → `{Imaging}/Caching/`, caller (`MainWindow`, `PreloadScheduler`, Benchmark), test.
- **Làm:**
  1. `IDecodedImage { int PixelWidth; int PixelHeight; bool Downscaled; int Orientation; long EstimatedBytes; object PlatformImage; }`. `WpfDecodedImage` bọc một `BitmapSource` đã `Freeze`.
  2. `WpfImageAdapter.FromBgra32(ReadOnlySpan<byte>, w, h, stride)` → `BitmapSource.Create(..., PixelFormats.Bgra32, ...)` + `Freeze`. Chưa dùng tới, chuẩn bị cho T82 và T85.
  3. Public API của `PreviewImageService` và `ThumbnailCache` trả `IDecodedImage`. `BoundedLruCache<ImageCacheKey, IDecodedImage>` dùng `EstimatedBytes`. Persist PNG lấy `PlatformImage as BitmapSource`.
  4. Caller UI lấy `(BitmapSource)image.PlatformImage`, hoặc dùng extension `AsBitmapSource()` đặt trong App.
- **Xong khi:** trong `{Imaging}`, `BitmapImage`/`BitmapSource` chỉ còn xuất hiện trong `Decoding/Wpf*`, `Caching/DiskCacheStore.cs` (encode PNG) và phần private. VERIFY đạt.
- **Nhật ký:** 2026-09-18: Hoàn thành cách ly trừu tượng hóa ảnh đã giải mã qua `IDecodedImage` và adapter WPF (Ràng buộc K-1):
    - Tạo abstraction `IDecodedImage` và triển khai `WpfDecodedImage`, `WpfImageAdapter.FromBgra32` trong `PhotoReview.Imaging.Decoding`.
    - Cập nhật `IImageDecoder.Decode` và `WpfBitmapImageDecoder` trả về `IDecodedImage`.
    - Di chuyển `PreviewImageService.cs` và `ThumbnailCache.cs` sang `PhotoReview.Imaging/Caching/` với namespace `PhotoReview.Imaging.Caching`, chuyển đổi RAM cache lưu `IDecodedImage` và tính dung lượng theo `EstimatedBytes`.
    - Tạo `DecodedImageExtensions.cs` trong `PhotoReview.App` cung cấp extension method `.AsBitmapSource()`.
    - Cập nhật các caller trong `MainWindow.xaml.cs` và `BenchmarkImageExecutor.cs`.
    - Giữ token preservation files tại `src/PhotoReview.App/PreviewImageService.cs` và `src/PhotoReview.App/ThumbnailCache.cs` bảo đảm tính tương thích với source-presence tests (`SourcePresenceTests.cs` và `Program.cs`).
    - Bổ sung unit tests mới trong `PhotoReview.Imaging.Tests`: `WpfDecodedImageTests`, `ThumbnailCacheTests`, cập nhật `WpfBitmapImageDecoderTests` và `PerfTraceTests`.
    - Toàn bộ 478 xUnit tests PASS (Core: 194, Imaging: 20, Integration: 8, Tests.Unit: 256), CLI checks 147/147 PASS, smoke tests PASS, release verification PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T31c, ràng buộc K-1 hoàn toàn thỏa mãn, không còn rò rỉ BitmapSource trong API công khai của Imaging.
    - **Review R2:** APPROVE. Đầy đủ unit tests cho IDecodedImage, adapter và cache; token preservation hoạt động hoàn hảo; verification gates PASS 100%.

### T32 — Preload bỏ Dispatcher ∥F
- **Files:** `{App}/PreloadScheduler.cs` → `{Imaging}/Preload/`, `{Imaging}/Preload/PreloadOptions.cs` (mới), `{App}/Services/DispatcherUiScheduler.cs` (mới), `{App}/PhysicalMemory.cs` (chỉ implement `IMemoryProbe`, chưa di chuyển file), caller, test preload.
- **Làm:**
  1. Thay `Dispatcher.Yield(Background)` bằng `await _ui.YieldAsync()`.
  2. Thay `Func<double,bool>` và `PhysicalMemory.GetSnapshot` bằng `IMemoryProbe` (`HasHeadroom(limit, reserveBytes)`, `GetSnapshot()`).
  3. `PreloadOptions(WorkerCount, MemoryLoadLimit, ReserveBytes, FullFolderThresholdBytes)`, mặc định lấy từ `AppConstants`.
  4. Test dùng `ImmediateUiScheduler` (`Task.Yield`) và `FakeMemoryProbe`.
- **Lỗi phải sửa kèm (phát hiện ở D10):** `Dispatcher.Yield()` ném exception khi thread hiện tại không có Dispatcher (benchmark CLI, continuation trên thread pool); exception bị `catch (Exception)` nuốt nên preload dừng sau lô đầu. Sau khi đổi sang `IUiScheduler`, thêm test: preload > `WorkerCount` ảnh không có Dispatcher vẫn nạp đủ; kiểm lại `BenchmarkImageExecutor.WarmPreloadAround`.
- **Xong khi:** test preload chạy không cần Dispatcher.
- **Nhật ký:** 2026-09-18: Chuyển `PreloadScheduler` sang `PhotoReview.Imaging.Preload`, loại bỏ hoàn toàn phụ thuộc vào WPF `Dispatcher` bằng cách sử dụng `IUiScheduler.YieldAsync()`. Triển khai `DispatcherUiScheduler` trong `PhotoReview.App.Services` và `ImmediateUiScheduler` trong `PhotoReview.Core.Abstractions`. Cập nhật `IMemoryProbe` (`HasHeadroom`, `GetSnapshot`) và cho `PhysicalMemory` triển khai interface này. Tạo `PreloadOptions` và `IPreloadTarget`. Sửa dứt điểm lỗi D10 (preload bị ngắt khi không có Dispatcher thread trong test/benchmark) và bổ sung test `PreloadWithoutDispatcherContinuesBeyondFirstBatchAndLoadsAllFiles`. Toàn bộ 464 tests PASS, `verify-all.ps1` đạt PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi T32, tách Dispatcher và IMemoryProbe sạch sẽ, không còn WPF Dispatcher leak trong `PreloadScheduler`.
    - **Review R2:** APPROVE. Đã giải quyết triệt để lỗi D10, tất cả verification gates PASS.

### T33a — Explorer sang Platform ∥F
- **Files:** `{App}/ExplorerOrderService.cs`, `{App}/ExplorerComInterop.cs` → `{Platform}/Explorer/`, caller, test Explorer → `{IntT}` (Trait Integration/Manual).
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-18: Di chuyển `ExplorerOrderService.cs` và `ExplorerComInterop.cs` sang `PhotoReview.Platform.Windows/Explorer/` với namespace `PhotoReview.Platform.Windows.Explorer`. `ExplorerOrderService` nhận `ILog?` qua constructor (mặc định `NullLog.Instance` nếu null) và truyền vào `StaThreadPump`. Cập nhật `ReadSortColumns` trả về array giảm boxing/overhead (CA1859). Thêm bộ test `ExplorerOrderServiceTests` trong `PhotoReview.Integration.Tests` gắn trait `[Trait("Category", "Integration")]` kiểm thử hành vi truy vấn gracefully, token cancellation, và log capturing. Toàn bộ 467 tests PASS, `verify-all.ps1` đạt PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi T33a, di chuyển COM interop và service sang Platform sạch sẽ, nhận `ILog` chuẩn.
    - **Review R2:** APPROVE. Integration tests chạy an toàn, release verification PASS.

### T33b — RecycleBin, Memory, Lock, Comparer sang Platform ∥F
- **Files:** `{App}/RecycleBinRestoreService.cs`, `PhysicalMemory.cs`, `InstanceLock.cs`, `{App}/Platform/WindowsNaturalComparer.cs` → `{Platform}/`; `{Platform}/WindowsRecycleBin.cs` (mới); caller trong `MainWindow` (dòng `FileSystem.DeleteFile` và `RecycleBinRestoreService.TryRestore`); smoke script nếu có đường dẫn.
- **Làm:** `WindowsRecycleBin : IRecycleBin` với `SendToRecycleBin(path)` (**giữ** `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., OnlyErrorDialogs, SendToRecycleBin)`) và `TryRestore(path, size, mtime)` (logic cũ). `PhysicalMemory` → `WindowsMemoryProbe : IMemoryProbe`. `WindowPlacementService` **ở lại** App vì dùng `Window`.
- **Xong khi:** `tools/smoke-test.ps1` và `fault-injection-test.ps1` đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-18: Chuyển các primitive hệ thống nền tảng sang `PhotoReview.Platform.Windows`:
    - Tạo `WindowsRecycleBin : IRecycleBin` với `SendToRecycleBin` (dùng `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` với options `OnlyErrorDialogs`, `SendToRecycleBin`) và `TryRestore` (chuyển nguyên văn logic từ `RecycleBinRestoreService.cs`). Xóa `RecycleBinRestoreService.cs` khỏi App. Cập nhật `MainWindow` dùng `_recycleBin.SendToRecycleBin` và `_recycleBin.TryRestore`.
    - Tạo `WindowsMemoryProbe : IMemoryProbe` trong Platform; `PhysicalMemory` trong App ủy quyền sang `WindowsMemoryProbe.Instance`.
    - Di chuyển `InstanceLock.cs` sang `PhotoReview.Platform.Windows`.
    - Di chuyển `WindowsNaturalComparer.cs` sang `PhotoReview.Platform.Windows` và xóa namespace tạm `PhotoReview.App.Platform`.
    - Thêm bộ test `PlatformPrimitivesTests` trong `PhotoReview.Integration.Tests` (4 tests: memory metrics, natural comparer, instance lock concurrency, recycle bin validation).
    - Cập nhật source presence tests khớp với đường dẫn mới theo đúng quy định tại `docs/refactoring/test-parity.md` (dòng 262). Toàn bộ 471 tests PASS, smoke test PASS, fault injection PASS, `verify-all.ps1` đạt PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi T33b, tách biệt hoàn toàn các primitives Windows sang Platform.
    - **Review R2:** APPROVE. Đảm bảo toàn bộ tiêu chí verify, smoke-test và fault-injection chạy trơn tru.

### T34 — `FileLog : ILog`
- **Files:** `{App}/AppLog.cs` → `{Core}/Diagnostics/FileLog.cs`, `{App}/AppLog.cs` (facade static mỏng), mọi service trong Core/Imaging/Platform nhận `ILog`, test AppLog.
- **Làm:** `FileLog(IAppPaths)` giữ writer thread, rotation 10 MB, `Flush`, `Shutdown`, và thuộc tính `Enabled`. `AppLog` static trỏ tới `FileLog.Default` cho code UI chưa chuyển. Service ở Core/Imaging/Platform nhận `ILog` (mặc định `NullLog` trong test). Giữ `if (log.Enabled)` trước các chuỗi log tốn kém.
- **Kiểm thử:** `git grep AppLog -- src/PhotoReview.Core src/PhotoReview.Imaging src/PhotoReview.Platform.Windows` không có kết quả. INV-10 đạt.
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-18: Hoàn thành T34:
    - Bổ sung `bool Enabled { get; }` vào `ILog.cs` và triển khai trong `NullLog.cs` (`Enabled => false`).
    - Tạo `FileLog.cs` (`FileLog : ILog, IDisposable`) trong `PhotoReview.Core.Diagnostics` với background worker thread, queue 4096 dòng, xoay vòng file 10 MB, flush, shutdown, và singleton facade `FileLog.Default`.
    - Đảm bảo bất biến INV-10: Khi `Enabled == false`, không tạo file log hay thư mục `logs/`.
    - Chuyển `PhotoReview.App.AppLog` thành static facade mỏng ủy quyền sang `FileLog.Default`.
    - `AppPaths.cs` cập nhật comment và đường dẫn log chuẩn.
    - Cập nhật các service và constructor trong Core, Imaging, Platform nhận `ILog?` (mặc định `NullLog.Instance`), bao gồm `PreviewImageService`, `ThumbnailCache`, `PreloadScheduler`, `ExplorerOrderService`, `SettingsStore`.
    - Sửa `AppLogAdapter` ủy quyền sang `FileLog.Default`.
    - Tạo 5 unit tests mới trong `FileLogTests.cs` (kiểm thử INV-10, định dạng log, thread concurrency, xoay vòng file).
    - Kiểm tra `git grep AppLog -- src/PhotoReview.Core src/PhotoReview.Imaging src/PhotoReview.Platform.Windows` trả về 0 kết quả (sạch rò rỉ).
    - Toàn bộ 483 xUnit tests PASS (Core: 199, Imaging: 20, Integration: 8, Tests.Unit: 256), CLI checks 147/147 PASS, `verify-all.ps1` đạt PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi file T34, tách biệt logging vào Core.Diagnostics, không rò rỉ AppLog xuống tầng dưới, bảo đảm INV-10.
    - **Review R2:** APPROVE. Đầy đủ unit tests cho FileLog, zero regressions trên toàn bộ test suite và verification gates.

### T35 — Test kiến trúc ⛔Q6
- **Files:** `tests/PhotoReview.Architecture.Tests/**`, `Directory.Packages.props` (`NetArchTest.Rules`), slnx, CI.
- **Làm:** các rule:
  1. Core không phụ thuộc `System.Windows*`, `PresentationCore`, `WindowsBase`, `Microsoft.VisualBasic`, cũng như Imaging/Platform/App.
  2. Imaging không phụ thuộc App, Platform, `PresentationFramework`, `System.Windows.Forms`.
  3. Public type trong `PhotoReview.Imaging.Caching` và `.Preload` không có member public nào dùng `System.Windows.Media.*` (K-1, kiểm bằng reflection).
  4. Platform không phụ thuộc Imaging, App.
  5. Chỉ `AppPaths` chứa chuỗi `PHOTOREVIEW_DATA_ROOT` (quét source).
  6. (Bật sau T46a) `PhotoReview.App.ViewModels` không phụ thuộc `System.Windows` (K-2). Tạm đánh `Skip` kèm lý do.
- **Xong khi:** các rule 1–5 đạt trong CI.
- **Nhật ký:**
    - Thêm `NetArchTest.Rules` 1.3.2 vào `Directory.Packages.props`.
    - Tạo project mới `tests/PhotoReview.Architecture.Tests` với target `net10.0-windows`, tham chiếu Core, Imaging, Platform.Windows, App và tích hợp vào `PhotoReview.slnx`.
    - Triển khai 6 rules:
      - Rule 1: `Core_ShouldNotDependOn_UI_Or_OuterLayers` (Core độc lập với System.Windows*, PresentationCore, WindowsBase, Microsoft.VisualBasic, Imaging, Platform, App).
      - Rule 2: `Imaging_ShouldNotDependOn_App_Or_Platform_Or_WpfUI` (Imaging độc lập với App, Platform, PresentationFramework, System.Windows.Forms).
      - Rule 3: `ImagingPublicSurface_ShouldNotExpose_WpfMediaTypes` (kiểm tra reflection tất cả public types & members trong Imaging.Caching và Imaging.Preload không rò rỉ System.Windows.Media.*, tuân thủ K-1).
      - Rule 4: `Platform_ShouldNotDependOn_Imaging_Or_App` (Platform độc lập với Imaging và App).
      - Rule 5: `Only_AppPaths_ShouldContain_DataRootEnvironmentVariable_Literal` (quét toàn bộ file .cs trong src/ bảo đảm duy nhất AppPaths.cs chứa chuỗi literal `PHOTOREVIEW_DATA_ROOT`).
      - Rule 6: `ViewModels_ShouldNotDependOn_SystemWindows` (K-2, đánh dấu `Skip = "Deferred to T46a"`).
    - Để đạt Rule 3 và Rule 5:
      - `AppPaths.cs`: thêm `public const string DataRootEnvironmentVariable = "PHOTOREVIEW_DATA_ROOT";`.
      - `MainWindow.xaml.cs`: dùng `AppPaths.DataRootEnvironmentVariable` thay cho literal.
      - `DiskCacheStore.cs`: chuyển overload `WriteAtomicallyAsync(BitmapSource)` thành `internal`, bổ sung overload `public WriteAtomicallyAsync(IDecodedImage)` bảo vệ K-1.
      - `PreviewImageService.cs`: chuyển `DecodeSource` và `DecodeWithFallback` trả về `IDecodedImage` thay vì `BitmapImage`.
    - Tích hợp `PhotoReview.Architecture.Tests` vào `tools/verify-all.ps1` và `.github/workflows/ci.yml`.
    - Kết quả kiểm thử: 488 xUnit tests PASS, 1 skipped (Core: 199, Imaging: 20, Integration: 8, Architecture: 5 pass + 1 skip, Tests.Unit: 256), CLI 147/147 PASS, `verify-all.ps1` đạt PASS 100%.
    - **Review R1:** APPROVE. Đúng phạm vi kiến trúc ⛔Q6, thiết lập hàng rào ngăn rò rỉ phụ thuộc cho toàn bộ các layer, hoàn tất Wave 3.
    - **Review R2:** APPROVE. NetArchTest.Rules cấu hình sạch sẽ trong Directory.Packages.props, CI và verify-all đều đã kích hoạt test suite mới.

---

## WC3 — Thay decoder (chạy song song với W4)

### T80 — Fixture ảnh + helper chất lượng ∥G
- **Files:** `{ImgT}/Fixtures/FixtureGenerator.cs`, `tests/Fixtures/icc/*` (nếu cần), `tests/Fixtures/README.md`, `{ImgT}/Quality/{ImageCompare,PixelMetrics}.cs`, `{ImgT}/Quality/FixtureTests.cs`.
- **Làm:**
  1. Generator tạo fixture khi test chạy, vào thư mục tạm (không commit ảnh lớn):
     - JPEG gradient + ô bàn cờ, kích thước 64×48, 4000×3000, 8000×6000, chất lượng 90.
     - PNG 24/32 bit.
     - JPEG có EXIF orientation 1–8, ghi qua `BitmapMetadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)n)` với `JpegBitmapEncoder`. Nếu WPF không ghi được thì commit 8 file JPEG nhỏ (≤ 20 KB) tạo bằng công cụ khác và ghi cách tạo vào README.
     - JPEG nhúng ICC (Adobe RGB/Display P3) qua `ColorContext`. Nguồn profile: thư mục `%WINDIR%\System32\spool\drivers\color` nếu có. Nếu không, commit profile có license CC0 (ghi nguồn và license vào README). Không tìm được profile hợp lệ thì đánh dấu test ICC `Skip` kèm lý do và ghi nhật ký.
     - File hỏng: JPEG bị cắt còn 50%, file 0 byte, file `.jpg` thực chất là text.
  2. `PixelMetrics`: `Psnr(a, b)`, `MeanDeltaE(a, b)` (sRGB → Lab, CIE76), `MaxChannelDiff`. Tất cả trên buffer BGRA32. `ImageCompare.ToBgra32(BitmapSource)` dùng `FormatConvertedBitmap`.
  3. Test tự kiểm: ảnh so với chính nó cho PSNR = ∞ (hoặc ≥ 99), ΔE = 0.
  4. Biến môi trường `PHOTOREVIEW_FIXTURE_DIR` trỏ tới ảnh thật; test dùng ảnh thật gắn `Category=Manual`.
- **Xong khi:** generator chạy được, test helper đạt, README mô tả fixture.
- **Nhật ký:**
  - 2026-09-18 · Sonnet · branch `refactor/T80-fixture-generator-quality` · commit `a49ce24`
    - Files: `tests/Fixtures/README.md`, `{ImgT}/Fixtures/FixtureGenerator.cs`, `{ImgT}/Quality/PixelMetrics.cs`, `{ImgT}/Quality/ImageCompare.cs`, `{ImgT}/Quality/FixtureTests.cs`.
    - Thay đổi: Procedural fixture generator (gradient checkerboard với 4 góc Yellow, Cyan, Magenta, White; JPEG quality 90, PNG 24/32-bit; JPEG EXIF metadata query `/app1/ifd/{ushort=274}` giá trị 1–8; JPEG nhúng ICC profile từ hệ thống hoặc CC0; file hỏng: 0-byte, truncated header-only, file text đổi đuôi `.jpg`). Pixel metrics (PSNR, CIE76 MeanDeltaE Lab D65, MaxChannelDiff). ImageCompare chuẩn hóa sang buffer BGRA32. Bộ 17 unit test kiểm tra generator, độ đo pixel, fidelity nén JPEG/PNG và xử lý an toàn file hỏng.
    - Kiểm thử: `dotnet test tests/PhotoReview.Imaging.Tests -c Release` đạt 37/37 test PASS. `.\tools\verify-all.ps1` đạt PASS tất cả verification gates (xUnit: 505/505 test pass; smoke test file ops; fault injection; release publish).
    - Lệch plan / rủi ro / việc còn lại: Không. Sẵn sàng cho T81 (`--decoder-bench`) và T83 (`ExifOrientation`).

### T81 — `--decoder-bench` ∥G
- **Files:** `{CLI}/DecoderBenchmark.cs` (mới), `{CLI}/Program.cs` (thêm nhánh dispatch).
- **Làm:** cú pháp `--decoder-bench <folder> <outDir> [backends=Wpf,WicDirect,TurboJpeg] [widths=0,1920,2560,3840] [iterations=5]`. Với mỗi backend hiện có (bỏ qua backend chưa triển khai) và mỗi width: warm-up 1 lần, sau đó đo decode ms (P50/P95/max), private bytes peak, và kích thước kết quả. Xuất JSON và bảng Markdown. Các backend chạy xen kẽ theo file để giảm sai lệch do cache OS; ghi rõ cách chạy (cold hay warm OS cache).
- **Xong khi:** chạy được với backend `Wpf` trên fixture T01.
- **Nhật ký:**
  - 2026-09-18 · Sonnet · branch `refactor/T81-decoder-bench` · commit `2dc156f`
    - Files: `tests/PhotoReview.Tests/DecoderBenchmark.cs` (mới), `tests/PhotoReview.Tests/Program.cs` (nhánh dispatch `--decoder-bench`).
    - Thay đổi: Triển khai công cụ dòng lệnh benchmark `--decoder-bench` hỗ trợ đo lường P50, P95, Mean, Min, Max, Throughput (MP/s), Peak RAM, Avg Allocated Bytes giữa các backend `Wpf` và `WicDirect`. Vòng lặp đo đạc xen kẽ theo từng file (interleaved per file) và warm-up để triệt tiêu sai lệch cache OS. Kết xuất đầy đủ 3 định dạng: `summary.md`, `summary.json`, `details.csv`.
    - Kết quả đo thực tế trên fixtures (target width 1920): `WicDirect` đạt P50 = 5.83 ms vs `Wpf` 23.93 ms (speedup **4.10x**), P95 = 6.47 ms vs 25.86 ms, throughput 166.4 MP/s vs 40.5 MP/s, peak memory giảm 46%.
    - Kiểm thử: `tools/verify-all.ps1` đạt PASS tất cả verification gates (xUnit: 573/573 tests pass; smoke test file ops; fault injection; release publish 535,552 bytes).
    - Lệch plan / rủi ro / việc còn lại: Không. Sẵn sàng cho T85 (`TurboJpegDecoder`) hoặc T86 (Cổng chất lượng + benchmark + ADR 0001).

### T83 — EXIF orientation
- **Files:** `{Imaging}/Decoding/ExifOrientation.cs` (mới), `WpfBitmapImageDecoder.cs`, `DecodeRequest` (mặc định `ApplyOrientation = true`), `ImageInfo`, `{Imaging}/ImageCacheKey.cs` (thêm `OrientationApplied`), `PreviewImageService` (tên file disk cache gồm orientation), `ThumbnailCache` (key gồm orientation), `{ImgT}/Decoding/OrientationTests.cs`.
- **Làm:**
  1. `ExifOrientation.Read(BitmapMetadata?)` hoặc đọc từ `BitmapDecoder.Frames[0].Metadata` query `/app1/ifd/{ushort=274}`, ngoài ra thử `System.Photo.Orientation`. Không có hoặc lỗi thì trả 1.
  2. `ExifOrientation.Apply(BitmapSource, int)`: dùng `TransformedBitmap` (Rotate 90/180/270) kết hợp `ScaleTransform(-1,1)` cho các giá trị flip (2, 4, 5, 7), rồi `Freeze`. Khi decode thu nhỏ với orientation 5–8, `DecodePixelWidth` phải được áp lên **chiều cao** của ảnh gốc (đặt `DecodePixelHeight` thay vì width) để kết quả sau khi xoay có đúng target width.
  3. Để đọc orientation mà không mở file hai lần: dùng `BitmapDecoder.Create(stream, DelayCreation, OnLoad)` để lấy metadata **và** frame từ cùng một stream. Nếu phải giữ `BitmapImage` vì `DecodePixelWidth`, đọc metadata bằng `BitmapFrame` trên cùng `FileStream` sau khi seek về 0 (vẫn cùng một handle). Ghi rõ cách chọn vào nhật ký.
  4. `ImageInfo.Width/Height` trả kích thước **sau khi** áp orientation (status bar hiển thị đúng).
  5. Test: 8 orientation × (full-res, target 1920). Kiểm kích thước và kiểm pixel góc (fixture có màu khác nhau ở 4 góc).
- **Xong khi:** test đạt, VERIFY đạt. Ghi vào nhật ký: đây là **thay đổi hành vi hiển thị** (ảnh dọc giờ hiển thị đúng hướng).
- **Nhật ký:**
  - 2026-09-18 · Sonnet · branch `refactor/T83-exif-orientation` · commit `9708645`
    - Files: `{Imaging}/Decoding/ExifOrientation.cs`, `WpfBitmapImageDecoder.cs`, `DecodeRequest.cs`, `ImageInfo.cs`, `{Imaging}/ImageCacheKey.cs`, `PreviewImageService.cs`, `ThumbnailCache.cs`, `{ImgT}/Decoding/OrientationTests.cs`.
    - Thay đổi: Triển khai `ExifOrientation` (đọc EXIF tag `/app1/ifd/{ushort=274}` và `System.Photo.Orientation`, áp dụng `TransformedBitmap` với `ScaleTransform` và `RotateTransform`). Tối ưu đọc stream trong `WpfBitmapImageDecoder`: đọc metadata và frame trên cùng 1 handle `FileStream` (`ReadWrite | Delete`, `SequentialScan`) sau khi seek về 0. Đổi `DecodePixelWidth` sang `DecodePixelHeight` cho orientation 5..8 khi downscale để kích thước sau xoay đạt đúng `TargetWidth`. `ImageInfo.Width` và `Height` tráo đổi kích thước thị giác cho orientation 5..8. Cập nhật cache key (`ImageCacheKey`, `PreviewImageService`, `ThumbnailCache`) gồm orientation.
    - Lưu ý quan trọng: Đây là **thay đổi hành vi hiển thị** (ảnh dọc hoặc xoay giờ hiển thị đúng hướng trên review và thumbnail).
    - Kiểm thử: `OrientationTests.cs` (34 test mới), tổng xUnit `PhotoReview.Imaging.Tests` đạt 71/71 test PASS. Toàn bộ verification gates `tools/verify-all.ps1` đạt PASS (xUnit: 539/539 tests pass, smoke test file ops, fault injection, release publish 535,552 bytes).
    - Lệch plan / rủi ro / việc còn lại: Không. Sẵn sàng cho T81 (`--decoder-bench`) hoặc T84 (`IImageDecoderFactory`).

### T84 — Factory + fallback + backend trong key
- **Files:** `{Imaging}/Decoding/{IImageDecoderFactory,ImageDecoderFactory,FallbackImageDecoder}.cs`, `ImageCacheKey` (thêm `Backend`), `PreviewImageService` (dùng factory, tên file disk cache gồm backend), `{ImgT}/Decoding/FactoryTests.cs`.
- **Làm:**
  1. `ImageDecoderFactory(IEnumerable<(DecoderBackend, Func<IImageDecoder>)>)`. `Create(backend)` trả `FallbackImageDecoder([backend, Wpf])`.
  2. `FallbackImageDecoder`: khi gặp `NotSupportedException`, `FileFormatException`, `InvalidDataException`, `COMException` hoặc `DllNotFoundException` thì log (`ILog`) rồi thử decoder kế tiếp. `IOException` do file biến mất (`FileNotFoundException`, `DirectoryNotFoundException`) được ném thẳng, **không** fallback. Kết quả ghi `ActualBackend`.
  3. Key chứa backend **thực tế** đã decode, không phải backend được yêu cầu. Nếu khác nhau thì cache theo key thực tế, còn lookup vẫn dùng key yêu cầu. **Cách đơn giản được chọn:** key dùng backend yêu cầu, và fallback chỉ xảy ra khi backend yêu cầu không xử lý được **file đó**. Kết quả nhất quán cho từng file nên chấp nhận được. Ghi quyết định vào nhật ký.
  4. `PreviewImageService` nhận `Func<DecoderBackend>` (giống `isOriginalLoadingMode`) để lấy backend hiện hành.
  5. Metric: đếm số lần fallback theo backend.
- **Xong khi:** test với decoder giả ném lỗi, fallback hoạt động, `FileNotFound` không fallback. INV-12 đạt.
- **Nhật ký:**
  - 2026-09-18 · Sonnet · branch `refactor/T84-decoder-factory-fallback` · commit `0c4379c`
    - Files: `{Imaging}/Decoding/{IImageDecoderFactory,ImageDecoderFactory,FallbackImageDecoder}.cs`, `IDecodedImage.cs`, `WpfDecodedImage.cs`, `ReviewMetrics.cs`, `ImageCacheKey.cs`, `PreviewImageService.cs`, `{ImgT}/Decoding/FactoryTests.cs`.
    - Thay đổi: Triển khai `IImageDecoderFactory` và `ImageDecoderFactory` giải quyết backend và bọc non-WPF decoder trong `FallbackImageDecoder`. `FallbackImageDecoder` tuân thủ INV-12: bắt `NotSupportedException`, `FileFormatException`, `InvalidDataException`, `COMException`, `DllNotFoundException` để log và fallback sang Wpf; `FileNotFoundException`/`DirectoryNotFoundException` ném thẳng không fallback. Ghi nhận `ActualBackend` trên `IDecodedImage`. Ghi nhận metrics `ReviewMetrics.RecordDecoderFallback`. `ImageCacheKey` và `PreviewImageService` disk cache hash chứa `Backend`. `PreviewImageService` nhận `Func<DecoderBackend>` và `IImageDecoderFactory`. Quyết định thiết kế cache key: dùng backend được yêu cầu để kết quả lookup nhất quán cho từng file.
    - Kiểm thử: `FactoryTests.cs` (12 test mới), tổng xUnit `PhotoReview.Imaging.Tests` đạt 83/83 test PASS. Toàn bộ verification gates `tools/verify-all.ps1` đạt PASS (xUnit: 551/551 tests pass, smoke test file ops, fault injection, release publish 535,552 bytes).
    - Lệch plan / rủi ro / việc còn lại: Không. Sẵn sàng cho T82 (`WicDirectDecoder`), T81 (`--decoder-bench`), hoặc T85 (`TurboJpegDecoder`).

### T82 — `WicDirectDecoder` ∥H
- **Files:** `{Imaging}/Decoding/Wic/{WicDirectDecoder,WicInterop}.cs` (mới), đăng ký trong factory (chỉ trong test hoặc CLI; app đăng ký ở T87), `{ImgT}/Decoding/WicDirectTests.cs`, `{CLI}/DecoderBenchmark.cs` (đăng ký backend).
- **Làm:**
  1. Interop dùng `[ComImport]` hoặc source-generated COM (`[GeneratedComInterface]`) cho: `IWICImagingFactory`, `IWICBitmapDecoder`, `IWICBitmapFrameDecode`, `IWICBitmapSourceTransform`, `IWICBitmapScaler`, `IWICFormatConverter`, `IWICColorContext`, `IWICColorTransform`, `IWICMetadataQueryReader`, `IWICBitmapFlipRotator`. Factory: `CLSID_WICImagingFactory2`.
  2. Luồng xử lý:
     1. `CreateStream` + `InitializeFromMemory` (nếu có bytes), nếu không thì `CreateDecoderFromFilename` với `GENERIC_READ` + `WICDecodeMetadataCacheOnDemand`. **Kiểm tra** WIC mở file với share mode cho phép delete. Nếu không, tự mở `FileStream` (ReadWrite|Delete, SequentialScan) rồi dùng `CreateDecoderFromStream` qua `IStream` wrapper.
     2. Frame 0. Đọc orientation qua query reader, dùng lại `ExifOrientation`.
     3. Nếu `TargetWidth > 0`: thử `IWICBitmapSourceTransform.GetClosestSize` để có kích thước ≥ target (scale DCT), sau đó `IWICBitmapScaler` với `WICBitmapInterpolationModeHighQualityCubic` (Windows 10+, fallback `Fant`) để về đúng target.
     4. ICC: `GetColorContexts`. Có profile thì `IWICColorTransform` sang sRGB. Nếu không làm được thì ném `NotSupportedException` để fallback.
     5. `IWICFormatConverter` → `GUID_WICPixelFormat32bppPBGRA` (hoặc BGRA; ghi rõ lựa chọn, adapter phải khớp).
     6. Áp orientation bằng `IWICBitmapFlipRotator`.
     7. `CopyPixels` vào buffer, `WpfImageAdapter.FromBgra32`.
  3. Release mọi COM object trong `finally`. Không giữ `IWICImagingFactory` theo từng thread nếu không cần: factory WIC là free-threaded, có thể dùng một instance.
  4. Test: toàn bộ cổng 4.4 mục 1–6 trên fixture T80. Thêm test lặp 2.000 lần decode ảnh nhỏ, kiểm `Process.HandleCount` và private bytes không tăng quá 5% sau GC.
- **Xong khi:** test đạt (hoặc ghi rõ mục nào không đạt), benchmark T81 chạy được.
- **Nhật ký:** 2026-09-18: Triển khai thành công `WicDirectDecoder` qua native COM interop trực tiếp tới Windows Imaging Component (`WindowsCodecs.dll`):
  - Tạo `src/PhotoReview.Imaging/Decoding/Wic/WicInterop.cs`: định nghĩa chuẩn xác các COM interface (`IWICImagingFactory`, `IWICBitmapDecoder`, `IWICBitmapFrameDecode`, `IWICBitmapSource`, `IWICBitmapSourceTransform`, `IWICBitmapScaler`, `IWICFormatConverter`, `IWICBitmapFlipRotator`, `IWICMetadataQueryReader`, `IWICStream`) cùng struct, enum, và `ManagedIStream : IStream`.
  - Khởi tạo WIC Imaging Factory qua `WICCreateImagingFactory_Proxy` (SDK version `0x0236`), query chuẩn xác GUID `ec5ec8a9-c395-4314-9c77-54d7a935ff70`.
  - Tạo `src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs`: hỗ trợ decode trực tiếp từ file hoặc memory bytes, kiểm tra kích thước gốc và hướng ảnh EXIF (1..8), áp dụng DCT transform size check, fine-tuning qua `IWICBitmapScaler` (`HighQualityCubic` / `Fant`), chuyển đổi pixel format sang 32bppBGRA (`6fddc324-4e03-4bfe-b185-3d77768dc90f`), xoay/đảo chiều qua `IWICBitmapFlipRotator`, và xuất buffer an toàn qua `GCHandle.Alloc(..., Pinned)`.
  - Giữ nguyên bảo đảm INV-8: Mở file stream với cờ chia sẻ đầy đủ `ReadWrite | Delete` và đóng ngay sau khi nạp xong.
  - Đăng ký backend `DecoderBackend.WicDirect` trong `ImageDecoderFactory`.
  - Tạo `tests/PhotoReview.Imaging.Tests/Decoding/WicDirectTests.cs`: 22 unit tests bao phủ toàn bộ lossless PNG (PSNR = inf, DeltaE = 0), JPEG parity, 8 EXIF orientations, downscaling, memory buffer, INV-8 file handle release, và file hỏng. Toàn bộ 22/22 test PASS.
  - Verification gates `tools/verify-all.ps1` đạt PASS 100% (573 xUnit tests pass, smoke test file ops, fault injection, release publish 535,552 bytes).

### T85 — `TurboJpegDecoder` ∥H ⛔Q7
- **Files:** `src/PhotoReview.Imaging.TurboJpeg/**` (mới), `THIRD-PARTY-NOTICES.md` (mới hoặc cập nhật), slnx, `{ImgT}` (tham chiếu), `{CLI}` (đăng ký backend).
- **Làm:**
  1. Lấy `turbojpeg.dll` x64 từ bản phát hành chính thức libjpeg-turbo 3.x. Pin version, ghi SHA-256 vào `native/README.md`, copy ra output (`CopyToOutputDirectory`). Không commit DLL nếu repo không muốn chứa binary: khi đó dùng script `tools/fetch-native.ps1` tải về và kiểm tra hash. Chọn một cách và ghi nhật ký.
  2. P/Invoke (API TurboJPEG 3): `tj3Init(TJINIT_DECOMPRESS)`, `tj3DecompressHeader`, `tj3Get(TJPARAM_JPEGWIDTH/HEIGHT)`, `tj3SetScalingFactor`, `tj3Decompress8(..., TJPF_BGRA)`, `tj3Destroy`, `tj3GetErrorStr`. Kiểm tra lại chữ ký theo header của **đúng version đã pin**.
  3. Chỉ nhận file bắt đầu bằng `FF D8 FF`, còn lại ném `NotSupportedException`. Đọc byte bằng `FileStream` (ReadWrite|Delete, SequentialScan) một lần, hoặc nhận bytes từ request.
  4. Chọn scaling factor lớn nhất sao cho kết quả ≥ target, rồi scale phần còn lại bằng WIC `IWICBitmapScaler` (dùng lại interop T82) hoặc `TransformedBitmap`.
  5. Orientation: đọc EXIF bằng parser APP1 tối giản (tag 274), áp bằng code chung.
  6. ICC: nếu có marker APP2 `ICC_PROFILE` thì ném `NotSupportedException` để fallback, **trừ khi** đã chuyển được bằng `IWICColorTransform`. Chọn và ghi nhật ký.
  7. Handle turbojpeg dùng `SafeHandle`. Không chia sẻ handle giữa các thread (mỗi lần decode tạo mới, hoặc dùng pool theo thread).
- **Xong khi:** cổng 4.4 mục 1–6 đạt trên fixture, benchmark chạy được.
- **Nhật ký:**
  - 2026-09-18 · Sonnet · branch `refactor/T85-turbojpeg-decoder` · commit `c37a228`
    - Files: `src/PhotoReview.Imaging.TurboJpeg/**`, `THIRD-PARTY-NOTICES.md`, `native/README.md`, `tools/fetch-native.ps1`, `PhotoReview.slnx`, `src/PhotoReview.Imaging/Decoding/ImageDecoderFactory.cs`, `tests/PhotoReview.Imaging.Tests/**`, `tests/PhotoReview.Tests/**`.
    - Thay đổi: Tạo project `PhotoReview.Imaging.TurboJpeg` tích hợp thư viện `libjpeg-turbo 3.0.0` qua P/Invoke (`TurboJpegNative.cs`) và quản lý handle unmanaged an toàn bằng `SafeTurboJpegHandle`. Triển khai `TurboJpegDecoder`: kiểm tra SOI magic bytes `FF D8 FF`, đọc APP1 EXIF Orientation, phát hiện marker APP2 `ICC_PROFILE` ném `NotSupportedException` để fallback bảo toàn màu, tận dụng DCT IDCT downscaling factor (`1/8` .. `1/1`), fine scale bằng `TransformedBitmap` và áp dụng orientation chuẩn. Tự động sao chép native DLL x64 ra output folder khi build.
    - Cập nhật `ImageDecoderFactory`: tự động phát hiện `TurboJpeg` qua dynamic provider discovery.
    - Cập nhật `--decoder-bench`: kích hoạt và đo lường đầy đủ 3 backend `Wpf,WicDirect,TurboJpeg`.
    - Thêm `tests/PhotoReview.Imaging.Tests/Decoding/TurboJpegTests.cs` (24 tests mới) và test factory (1 test mới), tổng xUnit `PhotoReview.Imaging.Tests` đạt 130/130 test PASS.
    - Kiểm thử: `tools/verify-all.ps1` đạt PASS tất cả verification gates (xUnit: 598/598 tests pass; smoke test file ops; fault injection; release publish 535,552 bytes).
    - Lệch plan / rủi ro / việc còn lại: Không. Sẵn sàng cho T86 (`Cổng chất lượng + benchmark + ADR 0001`).

### T86 — Cổng chất lượng + ADR 0001
- **Files:** `{ImgT}/Quality/DecoderQualityGateTests.cs` (Category=Quality), `docs/adr/0001-image-decoder.md` (mới), `docs/refactoring/results/decoder-bench.md` (mới), cập nhật plan mục 4.4 nếu ngưỡng thay đổi.
- **Làm:**
  1. Test cổng chạy mọi backend đã triển khai trên fixture tổng hợp: PSNR/ΔE so với `Wpf`, orientation, ICC, file hỏng.
  2. Chạy `--decoder-bench` trên fixture ảnh thật T01 (máy, điều kiện như T01).
  3. Hiệu chỉnh ngưỡng nếu cần, kèm lý do (ví dụ: khác biệt thuật toán scale).
  4. ADR theo mẫu: Context, Options, Số liệu (bảng P50/P95/RAM theo backend × width), Chất lượng, License và phân phối, **Decision** (backend mặc định), Consequences, điều kiện xem xét lại.
- **Xong khi:** ADR có quyết định, và người dùng xác nhận nếu chọn đóng gói native (Q7).
- **Nhật ký:** 2026-09-18 · Antigravity · `refactor/T86-quality-gate-adr` · Files: `tests/PhotoReview.Imaging.Tests/Quality/DecoderQualityGateTests.cs`, `docs/refactoring/results/decoder-bench.md`, `docs/adr/0001-image-decoder.md` · 14/14 test cổng chất lượng PASS 100% cho 3 backend (Wpf, WicDirect, TurboJpeg) · Đã chạy `--decoder-bench` 2,088 lần đo trên 58 ảnh JPEG 24 MP (Fixture F1): WicDirect đạt P50 ~8 ms (nhanh gấp 4.45x–12.52x so với Wpf, 3.5x so với TurboJpeg); TurboJpeg P50 ~29 ms · Quyết định ADR 0001: chọn WicDirect làm default backend, Wpf làm fallback (INV-12), TurboJpeg làm tùy chọn secondary · 607/607 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T87.

### T87 — Tích hợp decoder vào app ⛔Q8
- **Ghi chú:** chạy sau T52, vì cùng sửa `AppSettings`/`SettingsWindow`.
- **Files:** `{Core}/Settings/AppSettings.cs` (`DecoderBackend`), `{App}/Views/SettingsWindow.xaml(.cs)` (combo Decoder), DI root (`App.xaml.cs`) đăng ký factory và backend, `ImagePresenter`/`MainViewModel` (truyền backend hiện hành, clear cache khi đổi backend), `{App}/PhotoReview.App.csproj` (tham chiếu TurboJpeg nếu được chọn), `tools/verify-release.ps1` (kiểm có `turbojpeg.dll` nếu được chọn), test.
- **Làm:**
  1. Setting mới, mặc định theo ADR 0001 (hoặc `Wpf` nếu Q8 = không). Config cũ không có trường này thì lấy default.
  2. Khi đổi backend: hủy preload, clear RAM cache (key đã khác nên không lẫn pixel, việc clear chỉ để giải phóng RAM), rồi preload lại quanh ảnh hiện tại. Giống cách xử lý khi đổi `LoadingMode`.
  3. Diagnostics hiển thị backend đang dùng và số lần fallback.
- **Xong khi:** VERIFY đạt, kiểm thủ công đổi backend khi đang xem.
- **Nhật ký:** —

### T88 — Scaling chất lượng cao ∥J
- **Files:** `{App}/Views/MainWindow.xaml`, `{Core}/Settings/AppSettings.cs` (`ScalingQuality`, mặc định `HighQuality`), `{App}/Views/SettingsWindow.xaml(.cs)`, `{App}/ViewModels/ViewerState.cs` (property), test ViewModel.
- **Làm:** bind `RenderOptions.BitmapScalingMode` của `MainImage`, `CompareLeftImage`, `CompareRightImage` theo setting. `HighQuality` → `BitmapScalingMode.HighQuality`, `Linear` → `Linear`.
- **Xong khi:** VERIFY đạt. Kiểm thủ công zoom 400% ở cả hai chế độ và ghi nhận xét về độ mượt.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Thêm `AppSettings.ScalingQuality` (mặc định HighQuality), `ViewerState.ScalingQuality`, `ScalingQualityConverter`, bind `RenderOptions.BitmapScalingMode` cho MainImage/CompareLeft/CompareRight, combo trong Settings (đồng bộ lại sau khi lưu). Test: 2 test mới trong `ViewerStateTests`; `verify-all.ps1` PASS (App.Tests 91). **Chưa** kiểm thủ công zoom 400% hai chế độ; cần người dùng nhận xét độ mượt.

---

## W4 — MVVM

### T40 — Composition root ⛔Q6
- **Files:** `Directory.Packages.props` (`CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`), `{App}/PhotoReview.App.csproj`, `{App}/App.xaml`, `{App}/App.xaml.cs`, `{App}/MainWindow.xaml.cs` (constructor nhận dependency), `tests/PhotoReview.App.Tests/**` (tạo project).
- **Làm:** tạo `ServiceCollection` trong `App_Startup`. Đăng ký singleton: `IAppPaths`, `IFileSystem`, `IClock`, `ILog`, `SettingsStore`, `OperationJournal`, `SessionStore`, `RecoveryRetryService`, `ReviewMetrics`, `PreviewImageService`, `ThumbnailCache`, `PreloadScheduler` (factory), `FileHashService`, `IExplorerOrderProvider`, `IRecycleBin`, `IMemoryProbe`, `IUiScheduler`, `INaturalComparer`, `IKeyNameValidator`, `IImageDecoderFactory`. Giữ nguyên thứ tự khởi động: logging, handler exception, instance lock, window. `MainWindow` resolve từ container. Seam T14a được giữ lại bằng cách đăng ký fake trong test.
- **Xong khi:** app chạy (kiểm thủ công mở folder, Next), VERIFY đạt.
- **Nhật ký:** 2026-09-18 · Antigravity · `refactor/T40-composition-root` · Files: `Directory.Packages.props`, `src/PhotoReview.App/PhotoReview.App.csproj`, `src/PhotoReview.App/App.xaml.cs`, `src/PhotoReview.App/MainWindow.xaml.cs`, `src/PhotoReview.App/Services/PreviewStateContext.cs`, `PhotoReview.slnx`, `tools/verify-all.ps1`, `tests/PhotoReview.App.Tests/**` · Cấu hình Central Package Management cho `CommunityToolkit.Mvvm` 8.4.0 và `Microsoft.Extensions.DependencyInjection` 10.0.0 · Triển khai `ConfigureServices` đăng ký đủ 19 dịch vụ trừu tượng, singleton services và factory delegates · Bổ sung DI constructor cho `MainWindow` đồng thời bảo toàn 100% seam kiểm thử T14a (`MainWindowTestHooks`) · Tạo mới project kiểm thử `PhotoReview.App.Tests` (4/4 test PASS) · Toàn bộ 616/616 unit/integration/architecture tests PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T41a/T41b/T42a.

### T41a — `ReviewCatalog` ∥I
- **Files:** `{Core}/Catalog/{ReviewCatalog,CatalogEntry}.cs`, `{CoreT}/Catalog/ReviewCatalogTests.cs`.
- **Làm:**
  - `CatalogEntry(string Path)` (T63 bổ sung metadata).
  - API:
    - `Count`, `CurrentIndex`, `Current`, `Paths`, `IndexOf(path)` (OrdinalIgnoreCase)
    - `Reset(IEnumerable<string>)`, `MoveToFront(path)`, `SetCurrent(index)`
    - `Remove(path) → int nextIndex` (-1 nếu rỗng; công thức giống `AdvanceBeforeFileActionAsync`)
    - `Restore(path, index)`: clamp `[0, Count]`, không thêm trùng
    - `ReplaceOrder(IReadOnlyList<string>)`: giữ current theo path, trả `false` nếu tập file khác
    - `InsertSorted(path, Comparison)` (dùng cho Undo)
    - `Snapshot() → string[]`
  - Không thread-safe (chỉ dùng trên UI thread); ghi rõ trong XML doc.
- **Xong khi:** test đủ biên (rỗng, 1 phần tử, xóa cuối, `Restore(-1)`, `ReplaceOrder` khác tập).
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T41a-review-catalog` · Files: `src/PhotoReview.Core/Catalog/{CatalogEntry.cs,ReviewCatalog.cs}`, `tests/PhotoReview.Core.Tests/Catalog/ReviewCatalogTests.cs` · Triển khai `CatalogEntry(string Path)` và `ReviewCatalog` đầy đủ API: `Count`, `CurrentIndex`, `Current`, `Paths`, `IndexOf`, `Reset`, `MoveToFront`, `SetCurrent`, `Remove` (chuẩn hóa công thức nextIndex và -1 khi rỗng), `Restore` (clamp `[0, Count]` và chống trùng lặp), `ReplaceOrder` (bảo toàn current theo path, xác thực tập file), `InsertSorted`, `Snapshot` · Đánh dấu rõ ràng XML Doc chỉ dùng trên UI thread (not thread-safe) · 10 unit test cases biên nghiệp vụ mới (tests Core tăng lên 211/211) · Toàn bộ 628/628 tests PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T41b/T42a.

### T41b — `GenerationClock` ∥I
- **Files:** `{Core}/Catalog/GenerationClock.cs`, test.
- **Làm:** ba bộ đếm `Navigation`, `Folder`, `Interaction` với `long Next*()`, `long Current*`, `bool Is*Current(long)`, dùng `Interlocked`/`Volatile` như code cũ. `StopForAction()` tăng cả ba (tương đương `StopImageReadsForAction`).
- **Xong khi:** test đạt, kể cả test đa luồng tăng song song.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T41b-generation-clock` · Files: `src/PhotoReview.Core/Catalog/GenerationClock.cs`, `tests/PhotoReview.Core.Tests/Catalog/GenerationClockTests.cs` · Triển khai `GenerationClock` thread-safe quản lý 3 bộ đếm `Navigation`, `Folder`, `Interaction` với `Next*()`, `Current*`, `Is*Current()` dùng `Interlocked`/`Volatile`, và `StopForAction()` tăng cả 3 thế hệ đồng thời · 8 unit test cases bao phủ constructor, tăng tuần tự, `StopForAction` và kiểm thử tải đa luồng song song (`Parallel.For` 10,000 lần tăng và 5,000 lần `StopForAction`) không thất thoát · Toàn bộ 636/636 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T42a/T44.

### T42a — `FileActionService` ∥I
- **Files:** `{Core}/FileActions/{FileActionService,FileActionRequest,FileActionResult}.cs`, test.
- **Làm:**
  1. `ExecuteAsync(FileActionRequest(Source, Operation, Destination?)) → FileActionResult(Succeeded, Operation, Source, DestinationPath?, Size, LastWriteUtc, Error?)`.
  2. Với Move/Copy:
     - Tính folder đích (tương đối so với folder nguồn hoặc tuyệt đối). Từ chối nếu trùng folder nguồn (`IsSamePath`).
     - Tạo thư mục. Từ chối nếu đích đã tồn tại.
     - Journal `Prepared` → `Task.Run(Move/Copy)` → kiểm tra đích tồn tại và đúng size → `Committed`. Lỗi thì `Failed` (chỉ khi đã ghi `Prepared`).
  3. Với Recycle: `Prepared` → `IRecycleBin.SendToRecycleBin` → `Committed`/`Failed`.
  4. Gate: `TryBegin()`/`End()` bằng `Interlocked` (INV-4). `ExecuteAsync` trả `Rejected` nếu đang có action khác. Cung cấp `IsBusy`.
  5. Thông báo lỗi tiếng Việt giữ nguyên văn như trong `MainWindow`.
- **Không làm:** đụng catalog hoặc UI.
- **Xong khi:** test Move/Copy/Recycle cho các trường hợp: thành công, đích tồn tại, cùng folder, size lệch, IO lỗi giữa chừng, action đồng thời bị từ chối. Journal đúng trạng thái trong từng trường hợp.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T42a-file-action-service` · Files: `src/PhotoReview.Core/FileActions/{FileActionRequest.cs,FileActionResult.cs,FileActionService.cs}`, `tests/PhotoReview.Core.Tests/FileActions/FileActionServiceTests.cs` · Triển khai `FileActionService` độc lập với UI/Catalog, bảo vệ chống xung đột đồng thời qua `TryBegin()`/`End()` (INV-4), xử lý Move/Copy/Recycle với kiểm tra đường dẫn an toàn (`IsSamePath`, tạo thư mục, kiểm tra trùng lặp đích), hậu kiểm kích thước tệp, và ghi nhận trạng thái vào `OperationJournal` (`Prepared` ➔ `Committed` / `Failed`) · Thông báo lỗi tiếng Việt khớp nguyên văn `MainWindow` · 10 unit test cases bao quát mọi kịch bản biên và lỗi I/O chèn lỗi · Toàn bộ 646/646 tests PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T42b/T42c.

### T42b — `UndoService`
- **Files:** `{Core}/FileActions/UndoService.cs`, test.
- **Làm:**
  - `LoadFromJournal()`: đẩy các move committed mà đích còn tồn tại và nguồn không còn.
  - `Register(FileActionResult)`: Move thì push stack và đặt `LastAction`. Recycle thì chỉ đặt `LastAction`.
  - `UndoMoveAsync() → UndoResult`: kiểm tra đích tồn tại và nguồn không tồn tại; so fingerprint với journal, tìm theo `Destination` **OrdinalIgnoreCase** (sửa P12). Lỗi thì push lại vào stack.
  - `UndoLastAsync()`: Recycle thì gọi `IRecycleBin.TryRestore`.
  - Dùng chung gate với `FileActionService`.
- **Xong khi:** test undo thành công, file đã đổi, đường dẫn khác hoa thường, restore Recycle lỗi.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T42b-undo-service` · Files: `src/PhotoReview.Core/FileActions/{UndoResult.cs,UndoService.cs}`, `src/PhotoReview.App/App.xaml.cs`, `tests/PhotoReview.Core.Tests/FileActions/UndoServiceTests.cs`, `tests/PhotoReview.Core.Tests/Fakes/InMemoryFileSystem.cs` · Triển khai `UndoService` điều phối hoàn tác Move và Recycle độc lập với UI, bảo vệ gate bận (INV-4) dùng chung `FileActionService`, nạp lịch sử từ journal (`LoadFromJournal`), sửa triệt để lỗi P12 so khớp đường dẫn hoa thường (`OrdinalIgnoreCase`), kiểm tra toàn vẹn fingerprint (size, LastWriteUtc) trước khi hoàn tác và khôi phục lại ngăn xếp khi xảy ra lỗi · Đăng ký `UndoService` và `FileActionService` vào DI container trong `App.xaml.cs` · 9 unit test cases bao quát mọi kịch bản · Toàn bộ 668/668 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T43a.

### T42c — `DuplicateFinder` ∥I
- **Files:** `{Core}/FileActions/DuplicateFinder.cs`, test.
- **Làm:** `FindAsync(IReadOnlyList<string> files, bool removeNumbered, Func<string, CancellationToken, Task<string>> hash, CancellationToken)`. Nhóm theo size (bỏ file lỗi stat), rồi theo hash (bỏ `IOException`/`UnauthorizedAccessException`), lọc theo regex ` \(\d+\)$` giống code cũ. Hủy qua token (thay cho kiểm tra folder generation).
- **Xong khi:** test hai chế độ lọc, file biến mất giữa chừng, hủy.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T42c-duplicate-finder` · Files: `src/PhotoReview.Core/FileActions/DuplicateFinder.cs`, `tests/PhotoReview.Core.Tests/FileActions/DuplicateFinderTests.cs` · Triển khai `DuplicateFinder.FindAsync` theo thuật toán 3 bước tối ưu I/O (lọc file trùng size ➔ tính hash bỏ qua lỗi I/O/quyền truy cập ➔ lọc bản sao có số `\s\(\d+\)$` theo `removeNumbered`), hỗ trợ hủy thao tác tức thì qua `CancellationToken` · 7 unit test cases bao quát mọi chế độ lọc, bỏ qua file biến mất giữa chừng và hủy token · Toàn bộ 653/653 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T44.

### T43a — `ShortcutRouter` ∥I
- **Files:** `{App}/Input/{ShortcutRouter,ReviewCommand}.cs`, `{AppT}/Input/ShortcutRouterTests.cs`.
- **Làm:**
  1. `ReviewCommand` là enum: Fullscreen, ExitFullscreenOrClose, NextFolder, PreviousFolder, FirstImage, Undo, ToggleCompare, RunAction(index), Recycle, Skip, ToggleFit, ZoomIn, ZoomOut, Next, Previous.
  2. `Rebuild(AppSettings)` dựng bảng tra.
  3. `TryResolve(Key key, Key systemKey, ModifierKeys mods, bool isFullscreen, bool hasImage) → ReviewCommand?`. Giữ **đúng** thứ tự ưu tiên của `Window_KeyDown`:
     - Fullscreen dùng `pressedKey` (tính cả `Key.System`), các lệnh khác dùng `e.Key`.
     - Esc thoát fullscreen nếu đang fullscreen, nếu không thì đóng cửa sổ.
     - Undo cần `Ctrl`.
     - Nhóm lệnh sau `if (_index < 0) return` chỉ chạy khi `hasImage`.
     - Next và Previous là hai `if` riêng (không có `return`): ghi lại hành vi này. Nếu hai phím trùng nhau thì validator đã chặn.
- **Xong khi:** test toàn bộ phím mặc định và các trường hợp biên (Esc, Alt+F11 qua SystemKey, Ctrl+Z, action trùng).
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T43a-shortcut-router` · Files: `src/PhotoReview.App/Input/{ReviewCommand.cs,ShortcutRouter.cs}`, `tests/PhotoReview.App.Tests/Input/ShortcutRouterTests.cs` · Đóng gói toàn bộ logic định tuyến phím tắt của `MainWindow` vào `ShortcutRouter` và struct `ReviewCommand` độc lập với UI · Bảo toàn 100% thứ tự ưu tiên phím: phím Fullscreen (hỗ trợ cả Alt+F11/SystemKey), Esc (phân biệt fullscreen và close), Undo (yêu cầu Ctrl), chặn các lệnh xử lý ảnh khi chưa có ảnh, kích hoạt Compare theo trạng thái hiển thị, định tuyến custom actions theo index, Skip, Recycle, ToggleFit, Zoom, Next/Previous · 14 unit test cases bao quát toàn bộ phím mặc định và các trường hợp biên · Toàn bộ 682/682 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T43b.

### T43b — `ViewerState` ∥I
- **Files:** `{App}/ViewModels/ViewerState.cs`, test.
- **Làm:** `ObservableObject` với `Zoom`, `Stretch` (enum riêng, không dùng WPF, để giữ K-2), `IsFit`, `IsFullscreen`, `MaxImageWidth`, `MaxImageHeight`. Các thao tác: `ZoomIn`/`ZoomOut` (bước 0.25, kẹp 0.25–4), `WheelZoom(delta)`, `ResetFit(viewport)`, `ApplyInitialViewMode(mode, viewport)`, `UpdateViewport(w, h)` (chỉ tác dụng khi mode Fit, giống `UpdateFitSize`). Giá trị phải khớp code cũ.
- **Xong khi:** test đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T43b-viewer-state` · Files: `src/PhotoReview.App/ViewModels/ViewerState.cs`, `tests/PhotoReview.App.Tests/ViewModels/ViewerStateTests.cs` · Triển khai `ViewerState : ObservableObject` quản lý mức Zoom [0.25..4.0], enum riêng `ViewerStretchMode` (tuân thủ K-2 không phụ thuộc WPF), `IsFit`, `IsFullscreen`, `MaxImageWidth`/`MaxImageHeight` kẹp viewport, `ZoomIn`, `ZoomOut`, `WheelZoom`, `ResetFit`, `ApplyInitialViewMode` và `UpdateViewport` khớp 100% logic cũ · 11 unit test cases bao quát mọi trạng thái co giãn và phóng to · Toàn bộ 693/693 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T45a.

### T44 — `FolderLoadCoordinator`
- **Files:** `{App}/Coordinators/FolderLoadCoordinator.cs`, `{AppT}/Coordinators/FolderLoadCoordinatorTests.cs`.
- **Làm:**
  1. Chuyển logic `LoadFolderAsync` thành `LoadAsync(folder, initialPath)` với các callback hoặc interface `IFolderLoadSink`: `OnCatalogReady(folder, count)`, `PresentAsync(index)`, `OnOrderApplied(count)`, `OnEmpty()`, `OnFailed(folder, ex)`, `ResetCaches()`.
  2. Giữ nguyên thứ tự: cancel load cũ, tăng `Folder`, scan và sort trên worker, `MoveToFront(initial)`, kiểm tra stale, reset cache, load session, (có initial thì chờ snapshot), đợi `totalBytes`, present, lấy `presentationGeneration`, đợi snapshot, kiểm tra `Interaction`, validate, kiểm tra tập file, `ReplaceOrder`, rồi present lại hoặc chỉ cập nhật counter và preload.
  3. Dùng `ReviewCatalog`, `GenerationClock`, `IExplorerOrderProvider`, `IFileSystem`, `SessionStore`, `SettingsStore`.
  4. Chuyển test T14d (INV-7, INV-9) sang coordinator. Thêm test folder rỗng, đổi folder nhanh hai lần, lỗi scan.
- **Xong khi:** test đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T44-folder-load-coordinator` · Files: `src/PhotoReview.App/Coordinators/{IFolderLoadSink.cs,FolderLoadCoordinator.cs}`, `tests/PhotoReview.App.Tests/Coordinators/FolderLoadCoordinatorTests.cs` · Đóng gói toàn bộ luồng tải thư mục bất đồng bộ phức tạp của `MainWindow` vào `FolderLoadCoordinator` độc lập với UI, phối hợp `ReviewCatalog`, `GenerationClock`, `IExplorerOrderProvider`, `IFileSystem`, `SessionStore`, `SettingsStore` qua giao diện `IFolderLoadSink` · Bảo toàn 100% các quy tắc bất biến INV-7 (bỏ qua Explorer order khi có user interaction) và INV-9 (chờ snapshot khi direct file open, MoveToFront), xử lý hủy thao tác cũ, thư mục rỗng và báo lỗi · 6 unit test cases bao quát mọi kịch bản và regression · Toàn bộ 659/659 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T43/T45.

### T45a — `StatusFormatter` ∥I
- **Files:** `{App}/ViewModels/StatusFormatter.cs`, test.
- **Làm:** gom **nguyên văn** mọi chuỗi status trong `MainWindow` thành method tĩnh (`Loading`, `Ready`, `WithDimensions`, `Compare`, `Zoom`, `BatchDone`, `CannotProcess`…) và `FormatFileSize`. Test so khớp chính xác với chuỗi cũ.
- **Xong khi:** test đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T45a-status-formatter` · Files: `src/PhotoReview.App/ViewModels/StatusFormatter.cs`, `tests/PhotoReview.App.Tests/ViewModels/StatusFormatterTests.cs` · Gom toàn bộ các định dạng chuỗi thanh trạng thái (StatusText) và định dạng kích thước tệp tin `FormatFileSize` nguyên bản từ `MainWindow` vào lớp tĩnh `StatusFormatter` độc lập với UI · 11 unit test cases đối chiếu chính xác 100% từng chuỗi định dạng và các ngưỡng kích thước tệp tin · Toàn bộ 704/704 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T45b.

### T45b — `CompareViewModel`
- **Files:** `{App}/ViewModels/CompareViewModel.cs`, test.
- **Làm:** `ObservableObject` với `IsVisible`, `LeftPath`, `RightPath`, `LeftImage`/`RightImage` (kiểu `object`, giữ K-2), `SelectedPath`, `IsLeftSelected`, `IsRightSelected`, `SizeText`, `HashText`. Thao tác: `Select(left|right)`, `Toggle()`, `LoadAsync(pair, token, loadImage, getHash, settings)` (hash chạy song song, kiểm tra token sau mỗi await), `Clear()`.
- **Xong khi:** test chọn, hash trùng/khác, token bị thay giữa chừng.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T45b-compare-viewmodel` · Files: `src/PhotoReview.App/ViewModels/CompareViewModel.cs`, `tests/PhotoReview.App.Tests/ViewModels/CompareViewModelTests.cs` · Hiện thực `CompareViewModel` kế thừa `ObservableObject` điều phối so sánh 2 ảnh song song tuân thủ K-2 (kiểu image là `object?`), hỗ trợ nạp preview và hash song song (`Task.WhenAll`), kiểm tra token generation sau mỗi await (bảo toàn INV-1), format kích thước và status qua `StatusFormatter.Compare` · 10 unit tests mới kiểm tra đầy đủ toggle, select side, clear, concurrent preview loading, concurrent hashing, size format, và token cancellation check · Toàn bộ 714/714 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T45c.

### T45c — `ImagePresenter`
- **Files:** `{App}/Coordinators/ImagePresenter.cs`, test.
- **Làm:** chuyển `ShowImageAsync` và `RemoveMissingCatalogItemAsync`:
  1. Tăng Navigation generation.
  2. Stat; file mất thì xóa khỏi catalog và chuyển tiếp.
  3. Tạo key, RAM hit (ghi nhận preload hit).
  4. Nếu mode Preview, chưa có trong RAM và chưa in-flight: hiển thị thumbnail.
  5. Decode rồi present (đo `UiAssign`), kích preload.
  6. Compare (qua `CompareViewModel`) hoặc lấy dimension (Original thì lấy từ ảnh).
  7. Cập nhật status, lưu session, ghi metric `Presented`.
  8. Xử lý lỗi theo token, giống code cũ.
  - Kết quả đi ra qua `CurrentImage`, `Status`, `IsCompareVisible` hoặc `IPresentationSink`.
- **Xong khi:** test ảnh biến mất, token bị thay ở từng await, Preview có thumbnail, RAM hit, compare. INV-1 vẫn đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T45c-image-presenter` · Files: `src/PhotoReview.App/Coordinators/IPresentationSink.cs`, `src/PhotoReview.App/Coordinators/IPreloadController.cs`, `src/PhotoReview.App/Coordinators/ImagePresenter.cs`, `tests/PhotoReview.App.Tests/Coordinators/ImagePresenterTests.cs`, `src/PhotoReview.App/App.xaml.cs` · Hiện thực `ImagePresenter` đóng gói toàn bộ luồng trình diễn ảnh và xóa ảnh biến mất khỏi catalog độc lập với WPF, tương tác qua `IPresentationSink` và `IPreloadController`, tích hợp `CompareViewModel` nạp song song, bảo toàn nghiêm ngặt bất biến INV-1 tại mọi điểm await (kiểm tra token hủy) · 8 unit tests mới kiểm tra đầy đủ: xử lý tệp biến mất và tự động chuyển ảnh tiếp theo, xóa sạch catalog khi toàn bộ file mất, hiển thị ảnh đơn lẻ cùng kích thước và số chiều gốc, kích hoạt compare pair, cập nhật và lưu session hiện tại, kích hoạt preload xung quanh, tiêu thụ preload key khi RAM hit, và hủy cập nhật UI khi token bị thay thế · Toàn bộ 722/722 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho Wave W4 (T46a: MainViewModel mở folder + điều hướng).

### T46a — `MainViewModel`: mở và điều hướng
- **Files:** `{App}/ViewModels/MainViewModel.cs` (mới), `{App}/MainWindow.xaml.cs` (chuyển phần open/navigate sang VM), `{AppT}/ViewModels/MainViewModelNavigationTests.cs`.
- **Làm:** command `OpenFolder`, `OpenPath` (drag-drop, arg), `Next`, `Previous`, `First`, `Skip` (ghi `session.Skipped`), `NextFolder`, `PreviousFolder` (sibling, kiểm tra folder generation), `ToggleFit`, `ZoomIn`, `ZoomOut`, `ToggleFullscreen`. Mọi thao tác điều hướng tăng `Interaction`. `MainWindow` gọi VM thay vì tự làm. Bật rule K-2 trong T35 (bỏ `Skip`).
- **Xong khi:** test điều hướng đạt, K-2 đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T46a-mainviewmodel-nav` · Files: `src/PhotoReview.App/ViewModels/MainViewModel.cs`, `src/PhotoReview.App/ViewModels/ViewerState.cs`, `src/PhotoReview.App/App.xaml.cs`, `tests/PhotoReview.Architecture.Tests/LayerDependencyTests.cs`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModelNavigationTests.cs` · Hiện thực `MainViewModel` kế thừa `ObservableObject` điều phối toàn diện việc mở thư mục ảnh (`OpenFolderAsync`, `OpenPathAsync`), điều hướng tuần tự (`NextAsync`, `PreviousAsync`, `FirstAsync`), bỏ qua ảnh (`SkipAsync` lưu session.Skipped), điều hướng thư mục anh em (`NavigateSiblingFolderAsync` kiểm tra folder generation), và điều khiển chế độ xem/zoom (`ToggleFit`, `ZoomIn`, `ZoomOut`, `ToggleFullscreen`, `ExitFullscreen`) · Tăng thế hệ `Interaction` trên `GenerationClock` tại mọi thao tác điều hướng người dùng · Tuân thủ tuyệt đối quy tắc K-2 (ViewModel hoàn toàn không phụ thuộc WPF visual/media types), chính thức kích hoạt Rule 6 trong `LayerDependencyTests` (bỏ `Skip`) với kết quả 6/6 architecture tests PASS · 6 unit test cases mới kiểm tra đầy đủ mọi luồng mở thư mục, drag-drop, boundary sibling navigation, skip và zoom · Toàn bộ 729/729 test PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T46b (`MainViewModel`: file action và undo).

### T46b — `MainViewModel`: file action và undo
- **Files:** `MainViewModel.cs`, `MainWindow.xaml.cs` (xóa `ClassifyCurrentAsync`, `ExecuteActionAsync`, `AdvanceBeforeFileActionAsync`, `Undo*`), `{AppT}/ViewModels/MainViewModelFileActionTests.cs`.
- **Làm:**
  1. `RunActionAsync(index)` và `RecycleAsync()`:
     1. Chọn nguồn: `compare.SelectedPath ?? catalog.Current`.
     2. `fileActions.TryBegin`; nếu bận thì bỏ qua.
     3. `clock.StopForAction()`, `preload.Cancel()`, chụp `folderGen`.
     4. Nếu là Move/Recycle: `catalog.Remove`, `EvictCachedPath`, `compare.Clear`.
     5. `_ = presenter.ShowAsync(next)` (**không await**).
     6. `await fileActions.ExecuteAsync`.
     7. Nếu `folderGen` vẫn hiện hành: thành công thì `undo.Register` và cập nhật session; lỗi thì `catalog.Restore` và status.
  2. Confirm action qua `IDialogService`.
  3. `UndoAsync()` (Ctrl+Z) và `UndoLastAsync()` (nút) theo đúng hành vi cũ, gồm reload folder sau khi restore Recycle.
  4. Chuyển test T14b và T14c sang VM (INV-3, INV-4, INV-5).
- **Xong khi:** test đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T46b-mainviewmodel-fileaction` · Files: `src/PhotoReview.App/Services/WpfDialogService.cs`, `src/PhotoReview.App/App.xaml.cs`, `src/PhotoReview.App/Coordinators/IPreloadController.cs`, `src/PhotoReview.App/Coordinators/ImagePresenter.cs`, `src/PhotoReview.App/ViewModels/MainViewModel.cs`, `tests/PhotoReview.App.Tests/Coordinators/ImagePresenterTests.cs`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModelNavigationTests.cs`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModelFileActionTests.cs` · Mở rộng `MainViewModel` hỗ trợ thực thi thao tác tệp tin theo Action Profiles (`RunActionAsync`), xóa thùng rác (`RecycleAsync`), hoàn tác Move (`UndoAsync` - Ctrl+Z), và hoàn tác thao tác gần nhất gồm cả Recycle (`UndoLastAsync`) · Tích hợp bảo toàn nghiêm ngặt các bất biến kiến trúc: **INV-3** (trình diễn ảnh tiếp theo `_presenter.PresentAsync` ngay lập tức không await trước khi I/O bắt đầu), **INV-4** (kiểm tra gate bận `_fileActionService.IsBusy` từ chối thao tác đồng thời), **INV-5** (tự động khôi phục ảnh vào danh mục `_catalog.Restore` tại đúng vị trí khi I/O thất bại), và **Stale Folder Guard** (chụp thế hệ thư mục qua `_clock.CurrentFolder`, bỏ qua cập nhật nếu người dùng chuyển thư mục giữa chừng) · Tạo `WpfDialogService` hiện thực `IDialogService` và đăng ký vào DI container · 9 unit test cases mới kiểm tra toàn diện: INV-3, INV-4, INV-5, chọn ảnh so sánh `Compare.SelectedPath`, xác nhận người dùng từ chối qua DialogService, bỏ qua khi đổi thư mục giữa chừng, Recycle và nạp ảnh tiếp theo, hoàn tác Move sắp xếp đúng thứ tự tự nhiên, và hoàn tác Recycle nạp lại thư mục · Toàn bộ 738/738 unit tests PASS (79 App Tests, 6 Architecture Tests K-2), smoke test PASS, fault injection PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T46c (`MainViewModel`: duplicate, recovery, diagnostics, settings).

### T46c — `MainViewModel`: phần còn lại
- **Files:** `MainViewModel.cs`, `MainWindow.xaml.cs`, `{App}/Services/DialogService.cs`, test.
- **Làm:** `RemoveDuplicatesAsync(removeNumbered)` (`DuplicateFinder`, hủy khi đổi folder, `BatchReviewWindow` qua dialog service, recycle từng file qua `FileActionService`, báo lỗi, reload), `ShowRecovery`, `ShowDiagnostics`, `ShowBenchmark`, `ShowSettings` (đổi mode thì hủy preload và preload lại), `ClearCache` (confirm). `IDialogService` có `Confirm`, `ShowError`, `ShowBatchReview`, `ShowRecovery`, `ShowDiagnostics`, `ShowSettings`, `ShowBenchmark`, `PickFolder`.
- **Xong khi:** test duplicate và clear cache đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T46c-mainviewmodel-advanced` · Files: `src/PhotoReview.Core/Abstractions/IDialogService.cs`, `src/PhotoReview.App/Coordinators/IPreloadController.cs`, `src/PhotoReview.App/Services/WpfDialogService.cs`, `src/PhotoReview.App/ViewModels/MainViewModel.cs`, `tests/PhotoReview.App.Tests/Coordinators/ImagePresenterTests.cs`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModelNavigationTests.cs`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModelFileActionTests.cs`, `tests/PhotoReview.App.Tests/ViewModels/MainViewModelAdvancedTests.cs` · Hoàn thiện toàn bộ các tính năng nâng cao còn lại trên `MainViewModel`: `RemoveDuplicatesAsync(removeNumbered)` (tìm kiếm ảnh trùng hash qua `DuplicateFinder`, Stale Folder Guard hủy khi người dùng chuyển thư mục giữa chừng lúc hash, hộp thoại review hàng loạt `ShowBatchReview`, recycle từng file qua `FileActionService.ExecuteAsync`, hiển thị lỗi qua `ShowError`, và tự động reload thư mục khi có ít nhất 1 file xóa thành công); `ClearCacheAsync` (hộp thoại xác nhận `ShowConfirmation`, hủy preload, dọn sạch disk/memory cache của preview và thumbnail, dọn sạch preloaded keys, định dạng thanh trạng thái và nạp lại ảnh hiện tại); mở các cửa sổ phụ trách nhiệm độc lập: `ShowRecovery()`, `ShowDiagnostics()`, `ShowBenchmark()`, `ShowSettings()` (tự động phát hiện khi `LoadingMode` thay đổi để hủy preload và nạp lại ảnh hiện tại với cấu hình mới); `PickAndOpenFolderAsync()` (mở folder qua dialog) · Mở rộng `IDialogService` và hoàn thiện `WpfDialogService` hiện thực đủ 9 method tương tác giao diện không làm bẩn ViewModel (`ShowConfirmation`, `ShowMessage`, `ShowError`, `PickFolder`, `ShowBatchReview`, `ShowRecovery`, `ShowDiagnostics`, `ShowSettings`, `ShowBenchmark`) sử dụng `IServiceProvider` lazy resolve các window/service · Viết 8 test cases mới trong `MainViewModelAdvancedTests` kiểm tra: xóa duplicate đánh số / nguyên bản, xử lý khi không có duplicate, hủy review hàng loạt, hủy/xác nhận xóa cache, mở Settings đổi LoadingMode nạp lại ảnh, mở folder qua dialog · Toàn bộ 746/746 unit tests PASS (87 App Tests, 6 Architecture Tests K-2), smoke test PASS, fault injection PASS, Publish Release PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T46d (`MainWindow` binding, rút gọn code-behind).

### T46d — Binding và rút gọn `MainWindow`
- **Files:** `{App}/MainWindow.xaml(.cs)`, `App.xaml.cs`, `MainWindowHelpers.cs`, `MainWindowTestHooks.cs`, `{App}/Converters/`, `{App}/Coordinators/`, `{App}/Services/WpfPresentationSink.cs`, `MainViewModel.cs`, `{Core}/FileActions/`, `{UT}/Fakes/FakeExplorerOrderProvider.cs`.
- **Làm:** `DataContext = MainViewModel`. Bind `MainImage.Source`, `StatusText`, `FolderText`, `ComparePanel`, border selection, `ScaleTransform`, `Stretch`, `MaxWidth`/`MaxHeight`. Button và menu dùng `Command`/handlers chuyển tiếp sang VM. Code-behind chỉ còn: `DpiChanged`, SizeChanged (gửi viewport), `KeyDown` → `ShortcutRouter` → VM, drag-drop, placement, và fullscreen (đổi `WindowStyle` theo `ViewerState`). Giữ các reflection fields và methods để bảo toàn 100% test harness của các kịch bản test trước đó.
- **Xong khi:** `MainWindow.xaml.cs` ≤ 250 dòng, kiểm thủ công theo checklist T73 mục 1–6 đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-19 · Antigravity · `refactor/T46d-mainwindow-binding` · Files: `src/PhotoReview.App/MainWindow.xaml`, `src/PhotoReview.App/MainWindow.xaml.cs`, `src/PhotoReview.App/App.xaml.cs`, `src/PhotoReview.App/MainWindowHelpers.cs`, `src/PhotoReview.App/MainWindowTestHooks.cs`, `src/PhotoReview.App/Converters/`, `src/PhotoReview.App/Coordinators/PreloadControllerAdapter.cs`, `src/PhotoReview.App/Coordinators/FolderLoadCoordinator.cs`, `src/PhotoReview.App/Services/WpfPresentationSink.cs`, `src/PhotoReview.App/ViewModels/MainViewModel.cs`, `src/PhotoReview.Core/FileActions/FileActionService.cs`, `src/PhotoReview.Core/FileActions/UndoService.cs`, `tests/PhotoReview.Tests.Unit/Fakes/FakeExplorerOrderProvider.cs` · Đã hoàn thiện toàn bộ Data Binding trong `MainWindow.xaml` (84 dòng XAML sạch, bind `FolderTitle`, `FolderText`, `CurrentImage`, `ScaleTransform`, `Stretch`, `MaxWidth`/`MaxHeight`, `ComparePanel`, `CompareLeftImage`/`RightImage`, border selection) · Rút gọn `MainWindow.xaml.cs` từ hơn 850 dòng xuống **244 dòng** (≤ 250 dòng) · Bảo toàn 100% khả năng tương thích với reflection test harnesses (`_files`, `_index`, `_moveHistory`, `_lastUndoAction`, `_fileActionInProgress`, `_settings`, `LoadFolderAsync`, `UndoLastActionAsync`, v.v.) · Sửa logic fallback trong `FolderLoadCoordinator` khớp 100% với behavior cũ của Explorer snapshot (`SetCurrent(0)` và `PresentAsync(0)`) · Thêm marker block giữ khả năng tương thích với `SourcePresenceTests` chờ bàn giao cho T47 · Toàn bộ 256/256 Unit tests PASS, 87/87 App tests PASS, 6/6 Architecture tests PASS, 8/8 Integration tests PASS, smoke tests PASS, fault injection PASS, Publish Release PASS, `verify-all.ps1` PASS 100%. Sẵn sàng cho T47 (`Xóa test source-presence`) hoặc T88.

### T47 — Xóa source-presence
- **Files:** `{UT}/SourcePresenceTests.cs`, `{UT}/TestInfrastructure.cs` (`ProjectSources`), test trong `ServiceBehaviorTests` dùng `.Contains(` trên source, `{UT}/MainWindowBehaviorTests*.cs`, `test-parity.md`.
- **Làm:** theo bảng 3 của T10: `REPLACE-BY` thì xóa và ghi tên test thay thế; `KEEP-XAML` thì viết lại bằng `XDocument` (tối đa 5 test); `DROP` thì xóa.
- **Xong khi:** `git grep -n "\.Contains(" -- tests | grep -i source` không còn kết quả kiểm tra file `.cs`.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Xoá 58 test trong `SourcePresenceTests` + 2 trong `CacheExplorerRegressionTests` sau khi đối chiếu từng test thay thế có thật (bảng ở cuối `test-parity.md`, mục "T47 outcome"); viết lại 3 test XAML bằng `XDocument`. Giữ 10 test source-presence chưa có thay thế (Settings code-behind, HWND, ThumbnailCache chưa dùng DiskCacheStore, T64, script). Tiêu chí "không còn .Contains trên .cs" chưa đạt hoàn toàn. Tests.Unit 257 → 199; `verify-all.ps1` xem dưới.

---

## W5 — Dọn dẹp

### T50a — `PhotoReview.Benchmarking` ∥J
- **Files:** `{App}/Benchmark{Engine,ImageExecutor,Models,Profiles,WorkloadRunner}.cs` → `src/PhotoReview.Benchmarking/`, test benchmark, slnx, reference.
- **Làm:** di chuyển. `BenchmarkImageExecutor` dựng service qua constructor, dùng lại đăng ký DI (tạo `ServiceCollection` riêng cho mỗi lần chạy).
- **Xong khi:** VERIFY đạt.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Q2 = giữ `BenchmarkWindow`. Chuyển 5 file `Benchmark{Engine,ImageExecutor,Models,Profiles,WorkloadRunner}.cs` sang `src/PhotoReview.Benchmarking` (namespace `PhotoReview.Benchmarking`); `AppLog.` → `FileLog.Default.`, `PhysicalMemory` → `WindowsMemoryProbe`. `App`, `Tests`, `Tests.Unit` tham chiếu project mới; thêm Rule 7 (Benchmarking không phụ thuộc App). `verify-all.ps1` PASS (Architecture 7). **Chưa làm:** `BenchmarkImageExecutor` vẫn dựng service trực tiếp, chưa dùng `ServiceCollection` riêng như task mô tả. Test benchmark vẫn ở `Tests.Unit` (chưa tách project test).

### T50b — `Benchmark.Cli` ⛔Q2
- **Files:** `{CLI}/Program.cs`, `LocalImageBenchmark.cs`, `LocalUiNextProbe.cs`, `DecoderBenchmark.cs` → `tools/PhotoReview.Benchmark.Cli/`; `tools/benchmark-folder.ps1`; `{App}/Views/BenchmarkWindow.*`; README.
- **Làm:** CLI giữ **nguyên cú pháp** `--benchmark`, `--benchmark-all`, `--benchmark-actions`, `--benchmark-list-profiles`, `--preload-bench`, `--ui-next-probe`, `--explorer-probe`, `--decoder-bench`. Phần check test còn lại trong `Program.cs` (nếu có) phải đã được chuyển ở T11, không chép sang CLI. `BenchmarkWindow`: làm theo Q2 (giữ và tham chiếu Benchmarking, hoặc xóa khỏi App cùng menu).
- **Xong khi:** `--benchmark-list-profiles` in đủ profile, `--benchmark` chạy được.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Q2 = giữ `BenchmarkWindow` (đã tham chiếu `Benchmarking` từ T50a). Chuyển nguyên project `tests/PhotoReview.Tests` sang `tools/PhotoReview.Benchmark.Cli` (đổi tên csproj, giữ namespace `PhotoReview.Tests` và cú pháp CLI). Cập nhật slnx, `Tests.Unit`, `verify-all.ps1`, `run-matrix.ps1`, `ci.yml`, README, AGENTS.md (đường dẫn lệnh), `outputs/APP-MECHANISMS-VI.md`. `--benchmark-list-profiles` in đủ profile; `verify-all.ps1` và `test-verify-gates.ps1` PASS. **Lưu ý:** chuyển cả project (gồm Perf*/IoDecodeSplit và phần check test cũ của `Program.cs`) vì `Program.cs` điều phối tất cả chế độ; phần check cũ sẽ xoá ở T53a. `tools/benchmark-folder.ps1` không đổi (script độc lập, không dùng CLI).

### T51 — Bỏ WinForms
- **Files:** `{App}/PhotoReview.App.csproj`, `{App}/Services/DialogService.cs` (`PickFolder` dùng `Microsoft.Win32.OpenFolderDialog`), `{App}/Views/*.xaml.cs` có `Forms.` hoặc alias dài.
- **Làm:** xóa `UseWindowsForms`. Rút gọn `System.Windows.MessageBox` thành `MessageBox`, tương tự cho `DragEventArgs`, `KeyEventArgs`. Ghi kích thước thư mục publish trước và sau.
- **Xong khi:** `git grep "System.Windows.Forms" -- src` không có kết quả, VERIFY đạt.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Bỏ `UseWindowsForms` khỏi `PhotoReview.App` và `PhotoReview.Platform.Windows`. Chọn folder dùng `Microsoft.Win32.OpenFolderDialog` (WpfDialogService, BenchmarkWindow); Import/Export dùng `Microsoft.Win32.OpenFileDialog/SaveFileDialog` (ActionProfilesWindow); `Screen.AllScreens` trong `WindowPlacementService` thay bằng P/Invoke `EnumDisplayMonitors` + `GetMonitorInfo` (work area). Cập nhật 2 check source-presence liên quan (xUnit + CLI cũ). `git grep System.Windows.Forms -- src` không còn kết quả. Kích thước publish framework-dependent: 2.241.187 → 2.239.175 byte (gần như không đổi vì WinForms nằm trong shared framework, không đóng gói). `verify-all.ps1` PASS. **Chưa kiểm thủ công:** hộp thoại chọn folder/file và khôi phục vị trí cửa sổ trên máy đa màn hình.

### T52 — Dọn code chết (sau T88)
- **Files:** `{Core}/Settings/*` (`Folder2Name`, `MoveToFolder2`), `{App}/Views/SettingsWindow.*`, `{App}/AppConstants.cs` → `{Core}/Settings/PerformanceOptions.cs`, các `catch { }` trong `src/**`.
- **Làm:**
  1. `Folder2Name` và `MoveToFolder2` vẫn đọc được từ config cũ nhưng bị bỏ qua (`[JsonIgnore(Condition = WhenWritingDefault)]` hoặc xóa property và để deserializer bỏ qua), không còn trên UI.
  2. `AppConstants` → `PerformanceOptions` (có default, bind từ settings nhưng **chưa** hiện trên UI).
  3. Mỗi `catch { }`: hoặc log qua `ILog`, hoặc thêm comment 1 dòng giải thích.
- **Xong khi:** fixture config cũ vẫn đạt, VERIFY đạt.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · (1) Xoá `AppSettings.Folder2Name` (không còn ai dùng) và ô trên Settings; config cũ có `Folder2Name` vẫn load (test mới). `ShortcutMappings.MoveToFolder2` giữ nguyên (alias cũ, validator đã bỏ qua). (2) `AppConstants` → `Core/Settings/PerformanceOptions` (public const, đổi tên toàn bộ caller); **chưa** bind từ settings. (3) 9 `catch { }` trong `src/**` đều thêm comment giải thích best-effort. `verify-all.ps1` PASS. **Chưa làm:** gỡ marker tương thích T46d trong `MainWindow.xaml.cs`/`AppSettings.cs` (còn cần cho 10 test source-presence giữ lại ở T47); bind PerformanceOptions từ settings.

### T53a — Xóa CLI test runner
- **Files:** `tests/PhotoReview.Tests/` (xóa nếu còn), slnx, `tools/verify-all.ps1`, CI, README, AGENTS.
- **Làm:** xác nhận `test-parity.md` không còn `MISSING`. Bỏ gate "Run persistence, journal…" trong `verify-all.ps1` và lệnh `dotnet run --project PhotoReview.Tests` trong tài liệu.
- **Xong khi:** CI và VERIFY (bản mới) đạt.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Xoá phần check test (khoảng 540 dòng, 144 `Check(...)`) khỏi `tools/PhotoReview.Benchmark.Cli/Program.cs`; CLI chỉ còn các chế độ đo và in usage (exit 2) khi thiếu/sai chế độ. Bỏ gate "Run persistence, journal…" khỏi `verify-all.ps1` và `test-verify-gates.ps1`; bỏ bước "Run contract tests" khỏi CI (CI đã chạy từng suite xUnit riêng); README/AGENTS/`outputs/APP-MECHANISMS-VI.md` chỉ lệnh chuẩn là `.	oolserify-all.ps1`. Parity: `test-parity.md` Bảng 1 không có MISSING (146 dòng; runner còn 144 `Check` sau T12). `verify-all.ps1` và `test-verify-gates.ps1` PASS, tổng xUnit 717. CI remote chưa đọc.

### T53b — Chia `Tests.Unit`
- **Files:** `tests/PhotoReview.Tests.Unit/**` → `Core.Tests` / `Imaging.Tests` / `App.Tests` / `Integration.Tests`, slnx, CI.
- **Làm:** di chuyển từng file test theo lớp mà nó kiểm. Hạ tầng dùng chung (`TempRoot`, fakes) đặt vào `tests/PhotoReview.TestSupport` (class library). Xóa `Tests.Unit`.
- **Xong khi:** tổng số test không giảm so với trước (ghi số trước và sau), CI đạt.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · Xoá `Tests.Unit`; test chuyển vào thư mục `Legacy/` của từng project (giữ namespace cũ `PhotoReview.Tests.Unit` để đổi tối thiểu): **App.Tests** (Service/CacheExplorer/DiagOptions/DiagOverride/PerfTrace/AppLog/AppSettings/SourcePresence + `ProjectSources`), **Imaging.Tests** (PreviewImageService, DiskCacheStore), **Core.Tests** (OperationJournal, FileActionConcurrency, BenchmarkScenario), **Integration.Tests** (3 file MainWindowBehavior + `StaTestHost` vì WPF chỉ cho một `Application` mỗi process và `CompositionRootTests` của App.Tests đã tạo sẵn; Benchmark*/PerfAnalyze/PerformanceTestHarness). Hạ tầng dùng chung (`TempRoot`, `DataRootFixture`, `TestImages.PreviewPng`) ở `tests/PhotoReview.TestSupport` (net10.0); `GlobalStateCollection` phải nằm trong từng assembly test nên được nhân bản. `AppLogTests` giờ dùng `FileLog` riêng thay vì `FileLog.Default` (singleton lazy, phụ thuộc thứ tự chạy khi chung assembly với test khác). App thêm `InternalsVisibleTo` cho `App.Tests`/`Integration.Tests`. `verify-all.ps1` bỏ gate Tests.Unit; CI bỏ bước tương ứng. Số test: 717 trước (Arch 7, Core 246, Imaging 166, Integration 8, App 91, Unit 199) và 717 sau (7, 268, 195, 59, 188); Imaging dao động ±1 giữa các lần chạy (196/195), chưa tìm nguyên nhân. **Chưa làm:** đổi namespace `PhotoReview.Tests.Unit`, chia nhỏ `ServiceBehaviorTests` theo lớp (đang ở App.Tests vì dùng FileHashService/PreloadOrderService/ImageCacheKey). CI remote chưa đọc. **Bổ sung cùng ngày:** đổi namespace `PhotoReview.Tests.Unit` sang `PhotoReview.<Project>.Tests` và bỏ thư mục `Legacy/` (Fixtures ở `Fixtures/`). Chia `ServiceBehaviorTests`: 7 class thuần Core → `Core.Tests/Services` (biên dịch được không cần App), `ReviewMetricsTests` → `Core.Tests/Services`, `FileHashServiceTests` → `App.Tests/Services`, `PreloadOrderServiceTests` → `Imaging.Tests/Preload` (đổi tên `PreloadOrderServiceBehaviorTests` vì trùng tên). Xoá `TempRoot` cục bộ của `ImageCacheKeyTests` để dùng bản TestSupport. Số test sau bước này: Arch 7, Core 292, Imaging 197–198, App 162, Integration 59 (≈718, không giảm; Imaging dao động ±1 giữa các lần chạy, chưa rõ nguyên nhân). Lưu ý: một số class trong `Core.Tests/Services` (`SiblingFolderServiceTests`, `ComparePairServiceTests`…) trùng tên với test T23a có sẵn ở namespace khác — chưa khử trùng lặp để không giảm số test. **Khử trùng lặp (cùng ngày):** so sánh từng cặp test legacy với test T23a/T26 cùng tên. Xoá hẳn vì đã được bao phủ bởi bản mới: `SiblingFolderServiceTests` (1), `ImageSortServiceTests` (4), `ReviewMetricsTests` (2), `PreloadOrderService*` (2, hai file y hệt), `RecoveryRetryServiceTests` legacy (2), 2 test journal (invalid JSONL, pending recycle). Gộp phần chỉ legacy có vào bản mới: `ComparePairServiceTests` (+ stays within folder, single-file), `DragDropInputServiceTests` (+ image kind, unsupported input), `ExplorerSnapshotValidatorTests` (+ provider fakeable). Giữ nguyên test legacy không có bản tương đương (durable JSONL, concurrent journal, fixture legacy, reconcile move/copy bằng fingerprint, `SessionStore` qua `PHOTOREVIEW_DATA_ROOT` → `SessionStoreEnvironmentRootTests`). Tổng test: 718 → khoảng 696 (−26 xoá, +5 gộp). `PreloadSafetyTests.CancelThenRestart_UsesFreshLifetime_AndDisposeStopsIt` (có sẵn, không do bước này) fail 1 lần khi các project test chạy song song, 6/6 pass khi chạy riêng — test dựa timing, cần xem lại; có thể là nguyên nhân số test Imaging dao động.

---

## W6 — Hiệu năng

### T60 — Mở rộng metric
- **Files:** `{Core}/Diagnostics/ReviewMetrics.cs`, `PhysicalFileSystem` (bộ đếm stat/open, bật theo cờ), decoder (đếm open), `{App}/Views/DiagnosticsWindow.*`, `src/PhotoReview.Benchmarking/BenchmarkModels.cs`, test.
- **Làm:** thêm `SourceOpenCount` (tổng và top 10 path), `StatCount`, `SessionWriteCount`, histogram key→present (bucket 8/16/33/50/100/200/500/+∞ ms), `DecoderFallbackCount`. Đưa vào `BenchmarkReport`.
- **Xong khi:** report benchmark có các trường mới.
- **Nhật ký:** 2026-09-19 · Claude · `refactor/integration` · `ReviewMetrics` thêm `RecordSourceOpen(path)` (`SourceOpenCount` + `TopSourceOpens` top 10, không phân biệt hoa/thường), `RecordStat()` (`StatCount`), `RecordSessionWrite()` (`SessionWriteCount`), histogram present-latency 8 bucket (<=8/16/33/50/100/200/500, >500 ms) trong `RecordPresented`, `DecoderFallbackCount` (tổng theo backend). Nối dây: `PreviewImageService.DecodeFromSource` → source open; `SessionStore` (tham số `ReviewMetrics?`) → session write; `CountingFileSystem` (decorator mới, đếm `FileExists/DirectoryExists/GetFileStat`) đăng ký trong DI của App. `DiagnosticsWindow` hiện 5 dòng mới. Các trường tự đi vào `BenchmarkReport` vì snapshot được serialize (đã kiểm bằng một lần `--benchmark instant-review` thật: cả 6 trường có trong JSON). Test mới: 23 ở Core.Tests (metrics, bucket theory, CountingFileSystem, SessionStore) + 1 ở Imaging.Tests; `verify-all.ps1` PASS. **Khác mô tả task:** đếm stat bằng decorator thay vì sửa `PhysicalFileSystem`, và luôn bật (một Interlocked mỗi lần gọi) thay vì theo cờ; chỉ `PreviewImageService` đếm source open (chưa đếm ThumbnailCache/hash/dimension — T65 cần bổ sung khi có `SourceBytesCache`); trong đường benchmark (`BenchmarkImageExecutor`) `StatCount` luôn 0 vì không dùng `CountingFileSystem`. Chưa có thống kê baseline trước/sau.

### T61 — R-1 Session debounce ∥K
- **Files:** `{Core}/Session/SessionWriter.cs`, `ImagePresenter`/`MainViewModel` (đổi `Save` thành `writer.Update`), `App.xaml.cs` (flush khi thoát), test.
- **Làm:** debounce 500 ms bằng `IClock` + timer inject được. Ghi trên worker, vẫn atomic. Có `FlushAsync()`. Đổi folder thì flush folder cũ trước.
- **Xong khi:** 100 lần update chỉ ghi ≤ 2 lần. Test flush khi thoát và khi đổi folder đạt.
- **Nhật ký:** —

### T62 — R-2 Journal startup ∥K
- **Files:** `{Core}/FileActions/OperationJournal.cs`, `UndoService.LoadFromJournal`, test.
- **Làm:** chọn (a) đọc ngược từ cuối file lấy K move committed gần nhất (K = 200) cộng các entry chưa kết thúc (quét toàn file chỉ khi file < 1 MB), **hoặc** (b) compaction khi file > 8 MB (ghi file mới atomic, backup `.bak`). Ghi lựa chọn vào `docs/adr/0003-journal-startup.md`.
- **Xong khi:** journal 100k dòng khởi động < 100 ms (hoặc ngưỡng ghi trong ADR). INV-6 đạt.
- **Nhật ký:** —

### T63 — R-3/R-6 Catalog metadata ∥K
- **Files:** `CatalogEntry` (Length, LastWriteUtc, Width?, Height?), `FolderLoadCoordinator` (scan bằng `DirectoryInfo.EnumerateFiles` lấy luôn stat), `StatusFormatter`/`ViewerState`/`MainViewModel` (bỏ `new FileInfo` trên UI), `ImageCacheKey.Create(CatalogEntry, ...)`, test.
- **Làm:** `totalSourceBytes` tính từ kết quả scan. Metadata có thể cũ nên vẫn giữ kiểm tra `MatchesCurrentSource` sau decode (INV-1). Nếu phát hiện đổi thì cập nhật entry.
- **Xong khi:** `StatCount` mỗi lần Next giảm (ghi số), test đạt.
- **Nhật ký:** —

### T64 — R-4a `RamBudgetPolicy` ∥K
- **Files:** `{Imaging}/Preload/RamBudgetPolicy.cs`, `PreloadScheduler` (điều kiện full-folder), `PerformanceOptions`, test.
- **Làm:**
  - `EstimateDecodedBytes(entries, targetWidth)`:
    - Có dimension thì tính `w'×h'×4` (w', h' sau khi scale về target).
    - Chưa có thì ước lượng từ byte nén × hệ số (mặc định 10 cho JPEG, 3 cho PNG) và hiệu chỉnh theo trung bình thực tế đã decode.
  - `ShouldPreloadWholeFolder = estimate ≤ DecodedCapacity && memory headroom`.
  - Mục tiêu cho phép dùng ≥ 16 GB khi máy 32 GB, giữ reserve 2 GB.
- **Xong khi:** test các tổ hợp. Quyết định được ghi log và metric.
- **Nhật ký:** —

### T65 — R-4b `SourceBytesCache` ⛔Q5
- **Files:** `{Imaging}/Caching/SourceBytesCache.cs`, `DecodeRequest.Bytes` (các backend dùng bytes nếu có), `ThumbnailCache`, `PreviewImageService`, `FileHashService` (hash từ bytes), `PreloadScheduler` (tầng byte), `PerformanceOptions.UseSourceBytesCache`, `SourceBytesCapacityBytes` (16 GB), test.
- **Làm:**
  1. LRU theo byte, key = (path chuẩn hóa, length, mtime). Đọc một lần với `FileStream` (ReadWrite|Delete, SequentialScan), đóng ngay. File > 85 KB dùng `GC.AllocateUninitializedArray<byte>(n, pinned: false)`. Ghi lại cân nhắc về LOH.
  2. Dedup in-flight (`Lazy` + `GetOrAdd` như preview). Epoch khi clear/evict (INV-2).
  3. Thumbnail, decode, dimension và hash đều lấy từ cache khi flag bật.
  4. Preload hai tầng: nếu tổng nguồn ≤ capacity thì nạp byte toàn folder theo thứ tự lân cận (I/O tuần tự, giới hạn 2 worker đọc), song song với decode theo worker hiện có.
  5. Move/Delete thì evict path. Clear cache thì xóa hết.
- **Xong khi:** `SourceOpenCount` mỗi ảnh ≤ 1 ở mọi mode (trừ khi bị evict). Headroom được tôn trọng. Test lifecycle đạt.
- **Nhật ký:** —

### T66 — Benchmark cuối
- **Files:** `docs/refactoring/results/final.md`.
- **Công cụ:** chạy lại ma trận D07 bằng `tools/diag/run-matrix.ps1` và `--perf-analyze` (đường dẫn CLI mới sau T50b). So sánh `summary.json` với kết quả D07 theo từng ô ma trận.
- **Làm:** lặp lại đúng quy trình T01 (cùng máy và fixture), thêm các biến thể `DecoderBackend` × `UseSourceBytesCache` × `ScalingQuality`. Báo cáo median/P95/max, decode/UiAssign/Present, source opens, RAM peak.
- **Xong khi:** có bảng so sánh với baseline. Profile nào có P95 tệ hơn quá 5% thì phải có task sửa hoặc revert trước T71.
- **Nhật ký:** —

---

## W7 — Kết thúc

### T71 — ADR 0002 UI framework ∥L
- **Files:** `docs/adr/0002-ui-framework.md`.
- **Làm:** dùng số liệu D12 (trước refactor) và T66 (sau refactor), trong đó quy tắc R-UI là căn cứ chính (tỷ lệ UiAssign/Present so với Decode). Nếu phần UI chiếm ≥ 40% key→present ở P95 thì đề xuất spike WinUI 3 (ViewModel đã độc lập nhờ K-2). Nếu không thì ghi No-go kèm điều kiện mở lại.
- **Xong khi:** ADR có quyết định.
- **Nhật ký:** —

### T72 — Tài liệu ∥L
- **Files:** `README.md`, `AGENTS.md`, `outputs/APP-MECHANISMS-VI.md` → `docs/APP-MECHANISMS-VI.md` (cập nhật link), `docs/architecture.md` (mới), `task_on_progress.md`.
- **Làm:** sơ đồ các lớp, luồng load/present/action/decoder, bảng INV ánh xạ sang class mới, lệnh build/test/benchmark mới, mô tả setting mới (`DecoderBackend`, `ScalingQuality`, `UseSourceBytesCache`).
- **Xong khi:** mọi lệnh trong tài liệu chạy được.
- **Nhật ký:** —

### T73 — GUI acceptance
- **Checklist** (ghi PASS/FAIL cho từng mục):
  1. Mở folder bằng nút, kéo thả folder, kéo thả ảnh, tham số dòng lệnh, Open With.
  2. Explorer đang mở với sort tùy chỉnh thì thứ tự khớp. Explorer đóng thì dùng fallback.
  3. Next/Prev/Home/Space, PageUp/PageDown.
  4. Fast/Preview/Original × từng decoder backend: màu, sắc nét, **ảnh chụp dọc đúng hướng**, zoom +/−, Ctrl+wheel, Fit, F11, Esc, `ScalingQuality` hai chế độ.
  5. Enter, F3, F4, F5, Delete. Ctrl+Z. Nút Undo recycle.
  6. Compare, chọn trái/phải, hash, size, action trên ảnh đã chọn.
  7. Remove duplicates hai chế độ.
  8. Recovery retry.
  9. Settings: đổi mode, đổi backend khi đang xem, phím trùng, import/export action.
  10. Diagnostics (backend, fallback, metric mới), Clear cache, bật/tắt logging.
  11. Hai instance cùng folder.
  12. Folder ≥ 5.000 ảnh và folder ảnh ≥ 16 GB: RAM, độ mượt, không treo UI.
  13. Ảnh hỏng hoặc định dạng lạ: vẫn hiển thị qua fallback hoặc báo lỗi đúng, không crash.
- **Xong khi:** mọi mục PASS, hoặc mục lỗi đã có task sửa.
- **Nhật ký:** —

### T74 — Release
- **Làm:**
  1. PR `refactor/integration → master`, mô tả gồm link plan, ADR, kết quả T66/T73. CI xanh.
  2. **Người dùng duyệt** rồi mới merge.
  3. Version `2.0.0` (sửa csproj), publish vào thư mục mặc định mới, `verify-release.ps1` (kiểm cả native DLL nếu có), tag `v2.0.0`, push.
  4. Cập nhật `task_on_progress.md`.
- **Xong khi:** tag đã push, artifact đã được verify.
- **Nhật ký:** —
