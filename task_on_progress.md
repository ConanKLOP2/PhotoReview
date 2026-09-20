# PhotoReview — trạng thái hiện hành

- **Cập nhật:** 2026-09-20, sau khi lập plan cho review UI/clean-code và plan tối ưu cấu trúc (ST).
- **Checkout đã xác minh:** `master`, `ba1317e54702e3af8bd32c0bf5d953b3766f1ba0` (merge PR #9); working tree sạch trước review.
- **Mục tiêu phiên:** review xuyên repo và triển khai ba gói P1 đầu tiên trong plan optimize/clean.
- **Plan:** `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`; correctness và benchmark semantics chính đã triển khai; T89 GUI/STA và native Recycle Bin live acceptance còn chờ evidence.

## Review / validation mới

- Đã xem Core/Platform.Windows, Imaging/TurboJpeg, App/coordinators/UI, Benchmarking/CLI/scripts/CI và tests liên quan bằng 3 agent + coordinator.
- **Release build + xUnit:** `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` exit 0; 745 PASS (7 Architecture + 305 Core + 203 Imaging + 59 Integration + 171 App), 0 fail/skip. Có analyzer warnings.
- **Runtime probes:** DuplicateFinder chọn 2/2 bản cùng hash ở cả group toàn original và toàn numbered; chỉ selection, không recycle/delete. PerfAnalyze gộp 2 run workers 0/8 thành 1 group, R-CONT báo thiếu dữ liệu. Chi tiết/artifact tạm trong plan.
- **Source findings cần regression:** native buffer sizing unchecked; preload exit/restart chưa bảo đảm drain; batch dialog sau ConfigureAwait(false); WPF catch-all retry; folder remap O(n²)/stat lại; display state; cache invalidation/quota; Undo tail-limit; benchmark semantics.
- **Chưa xác minh runtime:** race Session/journal failures, native recycle identity, GUI T89. Không biến static concern thành runtime defect.
- `tools/verify-all.ps1` PASS sau API/lifecycle/culture wave: 7 Architecture + 314 Core + 205 Imaging + 62 Integration + 174 App = 762/762; smoke, fault-injection, publish và verify-release PASS.
- Review UI/clean-code mới đã được ghi thành OC14–OC18 trong `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`; **chưa triển khai**: Undo entry-point unification, pan threshold short-circuit, `_files.Capacity`, pattern cleanup và Fit one-pass.
- Review `WpfDialogService` mới đã được ghi thành WD01–WD06 trong cùng plan; **chưa triển khai**: UI-thread dispatch, DI refactor, MessageBox helper và BenchmarkWindow lifecycle change. WD01 phải audit call graph trước.
- Review I/O durability/performance mới đã được ghi thành IO01–IO07 trong cùng plan; **chưa triển khai**: bỏ `WriteThrough`/`Flush(true)`, async filesystem migration, `IgnoreInaccessible`, hoặc buffer/temp-name optimization. IO01/IO02 phải chốt contract và benchmark trước.
- Review cấu trúc phần mềm mới đã được ghi thành ST00–ST12 trong `docs/refactoring/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md` và `STRUCTURE-OPTIMIZE-TASKS.md`; **chưa triển khai**, chưa build/test lại trong lượt này. Điểm chính: `MainViewModel` 21 tham số/13 optional; `App.xaml.cs` chứa factory VM và `GetTotalSourceBytes` vẫn re-stat toàn folder (nửa sau F06, lệch với OC08 DONE); marker `IProgressiveExplorerOrderProvider` thừa; `ThumbnailModels.cs` chết; hạ tầng (`FileHashService`, `PerfCsvListener`, `PhysicalMemory`) nằm ở App; `PerfAnalyze*` bị buộc vào project WPF; `Benchmark.Cli` reflection 26 chỗ vào `MainWindow`.
- Cần quyết định trước khi làm: Q-ST1 (project PerfAnalysis), Q-ST2 (PerfCsvListener về Core), Q-ST3 (giữ Cli→App), Q-ST4 (semantics Ctrl+Z, thuộc OC14). ST04/ST05/ST08 đang `BLOCKED` vì các câu hỏi này.
- Plan dọn dẹp test ưu tiên đường nóng đã ghi ở `docs/refactoring/TEST-CLEANUP-PLAN-2026-09-20.md` (TC00–TC11); **chưa triển khai, chưa chạy test/đo thời gian**. Phát hiện chính: `InterleavedFileActionSequenceTests`, `BenchmarkScenarioTests` và `FileActionConcurrencyTests.InterleavedSequence…` mô phỏng Next/Move/Delete trên `List<string>` + `File.Move`, không gọi code production; chưa có test chuỗi Next/Move/Delete liên tục hay bất biến đọc đĩa theo chuỗi; ảnh test chỉ 1×1 PNG/text; đường đọc nguồn `new FileStream` không qua `IFileSystem` nên đếm đọc phụ thuộc metrics tự ghi; gate `IsBusy` bỏ im lặng action thứ hai. Cần quyết định Q-T1..Q-T4 (bỏ/xếp hàng phím khi bận, seam đếm đọc, folder ảnh thật qua env, cho phép Recycle Bin thật) — TC06/TC07 đang `BLOCKED`.

## Quyết định và việc còn lại

1. UI dispatcher, file outcome/journal durability, SessionWriter ordering, Recovery retry, native candidate identity, benchmark action/report semantics, API token ordering, disposal lifecycle, culture formatting và focused clean-code warnings đã triển khai; còn T89 layout/GUI acceptance, live Recycle Bin acceptance và đo tối ưu ảnh thật.
2. T89 wheel/anchor/pan và transaction Fit đã merge qua PR #9; `docs/refactoring/T89-FIT-LAYOUT-PLAN.md` vẫn **IN PROGRESS**, còn unify initial-mode viewport, STA layout và GUI Fit → wheel → drag trên ảnh dọc/ngang. Không gọi DONE từ pure math tests.
3. Giữ WPF, DecoderBackend=Wpf, ScalingQuality=HighQuality; SourceBytesCache mặc định off. Chỉ đổi theo số đo/plan được duyệt; không ép RAM gây paging/OOM.
4. T73 còn coverage Recovery retry/ảnh hỏng nếu có fixture; T74 release trước đã DONE theo hồ sơ cũ, không phải release mới trong lượt review.
5. Sau implementation: `./tools/verify-all.ps1`, publish `src/PhotoReview.App/bin/Release/net10.0-windows/publish`, feature branch/push origin/PR master theo AGENTS. Không commit thẳng master.
6. Không giảm `ApplyFitViewAsync` xuống một pass chỉ dựa trên source review; phải có STA/layout trace và GUI acceptance theo OC18/T89.
7. Với `WpfDialogService`, không thêm `Dispatcher.Invoke` hoặc đổi toàn bộ `GetService` thành `GetRequiredService` trước khi hoàn tất WD01 và chốt contract required/optional.
8. Với I/O, không giảm durability journal, không bật `IgnoreInaccessible` và không thêm `IAsyncFileSystem` diện rộng trước IO01/IO02 và evidence tương ứng.

## Tiếp tục

- Đọc AGENTS, plan mới và plan T89; kiểm tra branch/SHA/dirty tree lại trước làm.
- Commit implementation: `4a81ba8` preload, `6a4ec01` decoder, `00cad65` duplicate, `3db9ae6` cache, `5edd15d` display, `b8b97fb` PerfAnalyze grouping, `295f04b` Undo history, `a16b327` folder/catalog, `fb5155f` benchmark profile propagation, `42b92e2` SessionWriter, `481c130` file outcome, `abecebc` UI dispatcher, `32156f5` recovery retry, `6dee023` benchmark action, `6ad5c15` benchmark report/manifest, `3a5c168` recycle candidate identity, `4b6b5ad` Core clean, `77c4441` Imaging clean, `8249fd2` App serializer clean, `e301271` API token ordering, `89019a1` disposal lifecycle, `6fadeb2` culture formatting. Tài liệu plan và file này được cập nhật sau validation.
- Giữ frame khi Move/Delete đang loading; no hidden retry. Không dùng OS SendInput/SendKeys/SetForegroundWindow cho harness; dùng fixture riêng, không đổi config/action người dùng.
- Việc tiếp theo: ST00 baseline → ST01 → ST02 (`MainViewModel`/`App.xaml.cs` chỉ một owner: không chạy chồng OC14/WD03); OC14 chốt semantics + test Undo; sau đó OC15–OC17 clean-code wave; OC18 chờ bằng chứng T89 trước mọi thay đổi Fit.
- Nhóm tiếp theo theo plan: WD01 audit UI-thread/call graph; WD02 chỉ là DRY refactor low-risk; WD03–WD05 chỉ triển khai sau khi contract DI/dispatcher/window shutdown được chứng minh.
- Nhóm I/O tiếp theo: IO01 chốt durability/completeness contract, IO02 benchmark baseline, IO05 reproduce permission; chỉ sau đó mới cân nhắc IO03/IO04/IO06.
