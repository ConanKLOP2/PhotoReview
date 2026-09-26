# AR — Rà soát kiến trúc 2026-09-26 (tóm tắt)

**Trạng thái:** 🔄 Đang thực thi — Wave 1–3 (mã) trong PR `integration/arch-review-2026-09-26`; còn AR16 đo, AR15c đo, AR13 GUI · **Tier:** T1 · **Base:** review tại `master@1c84c59`, số dòng kiểm chứng lại tại `14dfe71` · **Plan từng task:** `docs/refactoring/arch-review/AR10..AR19-*.md` — chỉ đọc file của task đang làm.

## Kết luận

Kiến trúc **không cần thiết kế lại**. Phân tầng Core → Imaging/Platform → App được khoá hai lớp (NetArchTest + CA2007 là lỗi biên dịch ở tầng dưới, App bị cấm `ConfigureAwait(false)` bằng test). Một composition root (`AppHost`). Pipeline decode/cache/preload đã đo và tinh chỉnh (burst P95 < 4 ms, RAM 50 % = 16 GB theo ưu tiên dự án). Phần còn lại nằm ở **biên**: vài setting chụp một lần lúc khởi động (đã được chấp nhận ở Q-R19), mã chết quanh `AppSettings`, hai class code-behind quá lớn, và vài điểm hạ tầng build.

Đã kiểm chứng **không phải vấn đề** (không lập task): Rule 6 "ViewModels không phụ thuộc System.Windows" đang chạy thật (namespace `PhotoReview.App.ViewModels` tồn tại); ngân sách 16 GB chỉ tính ở một nơi (`RamBudgetPolicy`); không có service-locator ngoài `WpfDialogService` (adapter có chủ đích); các singleton tĩnh đều có lý do ghi trong `App.xaml.cs:33-38`; assembly `PhotoReview.Benchmarking` chỉ được nạp khi mở `BenchmarkWindow` (không ảnh hưởng khởi động).

## Phương pháp

Bốn lane đọc mã độc lập (Imaging, App, Core/Platform, build/test) + lead kiểm chứng từng nhận định trên source; hai nhận định của lane bị loại vì sai (Rule 6 no-op; RAM % "không được ghi rõ" — thực ra ghi ở `vi.json:392`). Không build/test/GUI trong bước review. Không trùng với `REVIEW-2026-09-26-FULL-SOURCE-AUDIT.md` (F0–F4 là lỗi hành vi; ở đây là cấu trúc).

## Findings đã kiểm chứng

| # | Finding | Bằng chứng | Mức | Task |
|---|---|---|---|---|
| F10 | `ImageCacheRamPercent`/`ImageCacheCapacityBytes` chụp một lần lúc dựng DI; LRU `_capacity` readonly; `PreloadOptions.FullFolderThresholdBytes` bất biến | `App.xaml.cs:140,146,160`; `BoundedLruCache.cs:7`; `PreloadScheduler.cs:118,322,366` | P3 (đã chấp nhận Q-R19, UI ghi rõ) | AR10 |
| F11 | `AppSettings.Load/Save/Saver/Validator/ConfigPath` tĩnh: `Saver` không nơi nào gán → `AppSettings.Save` là no-op; `Load` dùng `File.*` (vi phạm quy ước `IFileSystem` duy nhất trong Core); `Load()` không tham số trả về default nên `MainWindowBehaviorTests.Explorer.cs:299` đọc default thay vì config thật | `AppSettings.cs:102-119`; `SettingsWindow.xaml.cs:557`; `PerfSession.cs:165` | P2 | AR11 |
| F12 | `SettingsWindow` map tay ~30 thuộc tính có control (`LoadFields` :293, `Save_Click` :472); test round-trip hiện có chỉ phủ thuộc tính **không** có control | `SettingsWindowRedesignTests.cs:29-45` | P3 | AR11 |
| F13 | `native/x64/turbojpeg.dll` không bị commit chỉ nhờ pattern chung `x64/` | `.gitignore:24` | P2 | AR12 |
| F14 | Không có `TreatWarningsAsErrors` dù quy ước là 0 warning | `Directory.Build.props`; `.editorconfig:29,36` | P2 | AR12 |
| F15 | GC/runtime để mặc định, chưa ghi lý do (khác `PublishReadyToRun` có ghi) | `PhotoReview.App.csproj:10-14` | P3 | AR12 |
| F16 | `MainWindow.xaml.cs` 715 dòng gộp: lifecycle/DPI, máy trạng thái pan/kinetic/wheel/click-zoom (:34-50, :283-548), hội tụ Fit (:646-698), phím tắt (:565-616), drag-drop, menu | `MainWindow.xaml.cs` | P2 | AR13 |
| F17 | `MainViewModel` tự `new` 3 controller trong ctor; các controller nhận `this` làm sink → không inject thẳng được | `MainViewModel.cs:106-119` | P3 | AR14 |
| F18 | Hai bản "temp → encode → rename" (`DiskCacheStore.cs:84`, `PreviewCacheFile.cs:110`); hai overload `logContext` không ai gọi và bỏ tham số | `DiskCacheStore.cs:255,317` | P3 | AR15 |
| F19 | `SourceBytesCache.GetOrRead` chặn đồng bộ trên `Task.Run` dù caller đã ở worker | `SourceBytesCache.cs:52-61` | P3 (flag mặc định tắt) | AR15 |
| F20 | Mỗi ảnh bị mở một lần (probe đọc được) khi mở folder: 1 syscall/file trước frame đầu | `PhysicalFileSystem.cs:214-231` | P2 (ưu tiên #1: giảm I/O) — cần đo | AR16 |
| F21 | TurboJpeg: fine-scale/xoay qua `TransformedBitmap` + `Materialize` (2–3 copy) trong khi WicDirect 1 copy; ICC fallback bằng exception mỗi lần decode | `TurboJpegDecoder.cs:67,159-178`; `FallbackImageDecoder.cs:46-69` | P3 (WicDirect mặc định, nhanh hơn) | AR17 |
| F22 | App tham chiếu `Benchmarking` → `PerfAnalysis` (~2.4k dòng) chỉ cho `BenchmarkWindow`; chưa ghi là tính năng sản phẩm | `PhotoReview.App.csproj:22-26` | P3 | AR18 |
| F23 | `IDialogService` gộp prompt chung + 5 hàm mở cửa sổ app; `SkippedFilesWindow` mở thẳng trong code-behind | `IDialogService.cs`; `MainWindow.xaml.cs:700` | P3 | AR19 |

## Tasks

| ID | Tên | Quyết định | Phụ thuộc | Máy thật / GUI | Agent gợi ý | Trạng thái |
|---|---|---|---|---|---|---|
| AR10 | Ngân sách RAM live (đảo Q-R19) | Q-AR6 | — | Không (perf gate không đổi đường nóng) | sonnet | ❌ CLOSED — Q-AR6 (a), không sửa mã |
| AR11 | Dọn `AppSettings` tĩnh; round-trip Settings UI | — | — | Không | sonnet | ✅ DONE (integration PR) — allowlist Core `File.*` 6 file có lý do; `SettingsWindow` giữ store tuỳ chọn |
| AR12 | Vệ sinh build: `.gitignore`, warnings-as-errors, ghi chú GC | — | — | Không | haiku (12a/12c), sonnet (12b) | ✅ DONE (integration PR) — 0 warning cần sửa khi bật warnings-as-errors |
| AR13 | Tách `MainWindow`: `PointerInputController`, `FitViewController` | — | AR14 nếu làm (cùng vùng) | **GUI** (pan, wheel, click-zoom, Fit = T89) | strongest | ✅ CODE DONE (integration PR) — `MainWindow.xaml.cs` 715 → 404 dòng (phần còn lại: ctor/wiring, lifecycle, `Window_KeyDown`, forward menu — plan cho giữ); **GUI check: người dùng** |
| AR14 | Lắp ráp controller của `MainViewModel` | Q-AR10 | — | Không | sonnet | ✅ DONE — Q-AR10 (a), chỉ comment |
| AR15 | Dọn cache Imaging (overload chết, 1 writer nguyên tử, `SourceBytesCache` không hop thread) | — | — | Không | sonnet | ✅ DONE (integration PR) — 15c chờ đo perf `UseSourceBytesCache=true` |
| AR16 | Probe đọc được khi mở folder: đo rồi quyết | Q-AR7 | — | **Đo trên máy thật** (F4) | strongest (quyết định), sonnet (đo) | 🔄 Bước 1 (đo) chưa chạy |
| AR17 | TurboJpeg: thử nghiệm hay đầu tư | Q-AR8 | — | Đo trên máy thật nếu chọn (b) | strongest nếu (b) | ✅ DONE — Q-AR8 (a), nhãn "thử nghiệm" |
| AR18 | Benchmark trong app: ghi nhận | Q-AR9 | — | Không | haiku | ✅ DONE — Q-AR9 (a), ghi chú architecture.md |
| AR19 | Tách `IDialogService` | Q-AR10 | AR13 (cùng file) | Không | sonnet | ✅ DONE — Q-AR10 (a), `ShowSkippedFiles` qua `IDialogService` |

## Quyết định cần người dùng

Chi tiết phương án, ưu/nhược, việc phải test nằm trong file task tương ứng (AGENTS.md "Asking the user to decide"). Tóm tắt khuyến nghị:

| ID | Câu hỏi | Khuyến nghị | Task |
|---|---|---|---|
| Q-AR6 | Đảo Q-R19: RAM % có hiệu lực ngay thay vì sau khởi động lại? | **(a) giữ Q-R19**, đóng AR10 (đổi % hiếm, khởi động lại ~2 s, UI đã ghi rõ). Làm (b) chỉ nếu muốn nhất quán với `LoadingMode`/`DecoderBackend` | AR10 |
| Q-AR7 | Probe đọc được từng file khi mở folder: giữ / lười / nền | **Đo trước** (AR16 bước 1). Nếu probe > 5 % thời gian "first visual" trên F4 → (c) probe nền sau frame đầu, vẫn báo "Bỏ qua N file" (giữ ADR 0007); nếu không → (a) giữ | AR16 |
| Q-AR8 | TurboJpeg: (a) đánh dấu thử nghiệm, (b) đầu tư WIC-native path + ICC không exception, (c) bỏ | **(a)** — WicDirect đã nhanh hơn trên F4; đầu tư chỉ khi có số đo TurboJpeg thắng ở ít nhất một profile | AR17 |
| Q-AR9 | `BenchmarkWindow` trong app sản phẩm | **(a) giữ và ghi vào `architecture.md`** là tính năng; assembly nạp lười nên không tốn khởi động | AR18 |
| Q-AR10 | AR14 + AR19: làm hay đóng | **Đóng AR14** (vòng phụ thuộc sink → cần factory delegate, thêm phức tạp không đổi hành vi); **AR19 chỉ làm khi AR13 chạm cùng file** | AR14, AR19 |

## Thứ tự và song song

```text
Wave 1 (không cần quyết định, không GUI, chạy song song, 3 worktree):
  AR11 ── AR12 ── AR15                 → 3 PR nhỏ, base master
Wave 2 (đo + quyết định):
  AR16 bước 1 (đo)  → Q-AR7 → AR16 bước 2 (nếu chọn)
  Q-AR6, Q-AR8, Q-AR9, Q-AR10 → AR10 / AR17 / AR18 / AR14 / AR19 theo kết quả
Wave 3 (GUI):
  AR13 (+ AR19 nếu chọn) → T89 GUI check bởi người dùng
```

- Wave 1 độc lập với nhau: AR11 chạm `Core/Settings`, `App/SettingsWindow`, tests; AR12 chạm `.gitignore`, `Directory.Build.props`, docs; AR15 chạm `Imaging/Caching`. Không file chung → không cần integration PR.
- AR13 chạm `MainWindow.xaml.cs` nặng → **không** chạy song song với bất kỳ task nào sửa file này (AR19 mục `SkippedFiles_Click`).
- Token: Wave 1 dùng `sonnet`/`haiku`; lead chỉ review + build lại + chạy gate (AGENTS.md "Verify claims").

## Gate chung cho mọi task

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release          # 0 warning
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow"
tools/verify-all.ps1
```

Task có ghi "mutation": bỏ (hoặc đảo) đoạn mã được bảo vệ, test tương ứng **phải đỏ**, rồi khôi phục — ghi kết quả vào PR.

## Quy tắc thêm khi task DONE

1. (AR11) Core không còn `File.*`/`Directory.*` ngoài `PhysicalFileSystem` và `AppPaths` — test kiến trúc quét source.
2. (AR12) Build Release với warning = lỗi; `.gitignore` có rule tường minh cho binary native.
3. (AR13) `MainWindow.xaml.cs` chỉ còn lifecycle, wiring, dispatch phím; logic gesture/Fit sống trong controller có unit test.

## Sau khi xong

Nén mỗi task thành một dòng `ID · status · SHA` ở bảng Tasks, chuyển bằng chứng vào `docs/archive/evidence/arch-review-2026-09-26/`, ghi quyết định Q-AR6..10 vào `OPEN-DECISIONS.md` trên `master` (PR docs, AGENTS.md "Decision log"), cập nhật `ACTIVE-TASKS.md` nhóm AR.
