# Plan tối ưu cấu trúc phần mềm (ST01–ST12)

- Ngày: 2026-09-20. Baseline: `master` `5dc5cda`. Task chi tiết: [`STRUCTURE-OPTIMIZE-TASKS.md`](STRUCTURE-OPTIMIZE-TASKS.md).
- Trạng thái: **ST01, ST02, ST03, ST07, ST10 DONE**; ST04–ST06, ST08–ST09 BLOCKED (chờ quyết định Q-ST1, Q-ST2, Q-ST4); ST11 đang xử lý; ST12 điều tra. Build/test: Core 322 tests PASS, App 160 PASS (2 failure không liên quan), 7 architecture rules mới PASS.
- Liên quan: [`OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](OPTIMIZE-CLEAN-PLAN-2026-09-20.md) (OC/WD/IO), [`REFACTOR-PLAN.md`](REFACTOR-PLAN.md) (hướng B + C3), [`../architecture.md`](../architecture.md).
- Nguyên tắc: không đổi hành vi runtime, mặc định decoder, cache, RAM policy hay durability. Mọi task là refactor cấu trúc, trừ ST02 (sửa một điểm I/O dư thừa đã nằm trong F06). Không rewrite framework, không thêm project chỉ để "đẹp".

## 1. Đánh giá hiện trạng

**Đã đạt:** `Core` là `net10.0`, không WPF, không P/Invoke, có `LayerDependencyTests` bảo vệ; `Imaging`, `Platform.Windows`, `Imaging.TurboJpeg` chỉ trỏ xuống `Core`; có composition root, abstraction cho I/O/UI/dialog/scheduler; coordinator/sink đã tách khỏi code-behind.

**Chưa tối ưu (bằng chứng đọc từ source ở baseline):**

| ID | Bằng chứng | Hệ quả |
|---|---|---|
| S1 | `MainViewModel.cs` 809 dòng; constructor 21 tham số, 13 tham số optional; khoảng 16 chỗ `?.`/null-guard trên dependency optional. | Thiếu dependency thì thao tác âm thầm không chạy; production và test dùng hai "cấu hình" khác nhau; khó tách trách nhiệm. |
| S2 | `App.xaml.cs:138-206`: factory `MainViewModel` ~70 dòng, chứa logic `GetTotalSourceBytes` và vòng phụ thuộc VM↔presenter↔sink nối bằng `vm?.`. | Composition root làm việc của domain; khó test, đụng độ owner với các task MainViewModel. |
| S3 | `GetTotalSourceBytes` (App.xaml.cs) vẫn `SetEquals` rồi `GetFileStat` lại toàn folder khi membership đổi. | Nửa sau của F06 chưa xử lý, trong khi OC08 ghi DONE. Vi phạm nguyên tắc "hạn chế đọc đĩa". |
| S4 | `IProgressiveExplorerOrderProvider` là marker rỗng kế thừa `IExplorerOrderProvider` (đã có `TryGetSnapshotProgressiveAsync`). `ExplorerOrderProviderAdapter` chỉ forward. `App.xaml.cs:75-76` đăng ký cả `ExplorerOrderService` lẫn adapter → hai instance; `MainWindowHelpers.cs:140` tạo thêm adapter thứ ba khi không có hook. | Trừu tượng thừa; nhiều instance của dịch vụ COM. |
| S5 | `App/ThumbnailModels.cs` (`ThumbnailCacheOptions`, `ThumbnailResult`) không có tham chiếu nào ngoài chính file. `App/Diagnostics/PhotoReviewPerf.cs` chỉ chứa một `global using` nhưng mang tên class. | Code chết / tên gây hiểu nhầm. |
| S6 | Hạ tầng nằm ở gốc App: `FileHashService` (chỉ cần Core + Imaging), `PhysicalMemory` (wrapper mỏng của `WindowsMemoryProbe`), `PerfCsvListener` + `DiagOptions` (không dùng WPF; `PhotoReviewPerf` ở Core còn `cref` sang `PerfCsvListener`). | Tool/CLI phải tham chiếu App để dùng hạ tầng; Core tham chiếu tên type ở tầng trên trong XML doc. |
| S7 | `PhotoReview.Benchmarking` là `net10.0-windows` + `UseWPF`, nhưng 6 file `PerfAnalyze*` không có `using PhotoReview.*` nào và không dùng `System.Windows`. | Phân tích perf phải kéo WPF; test chậm và cần host Windows không cần thiết. |
| S8 | `Benchmark.Cli` tham chiếu `PhotoReview.App` và **reflection 26 chỗ** vào `MainWindow` (`LoadFolderAsync`, `ShowImageAsync`, `_files`, `_index`, `_fileActionInProgress`, `_metrics`, `_settings`, `ResetFitView`, `SetZoom`, `TryGetCachedPreview`). Các member này còn tồn tại chỉ để CLI dùng (compat). | Đổi tên/xóa là lỗi runtime `NullReference` mà build không bắt được. Chặn OC12/OC16. |
| S9 | Arch tests: đánh số rule thiếu 3 và 5; chưa có rule cho `Imaging.TurboJpeg` và `Benchmark.Cli`. Rule 7 cấm Benchmarking→App nhưng Cli→App không bị cấm. `SourcePresenceTests` (27 chỗ) vẫn đọc source text. Hai lớp `OperationJournalTests` ở hai file trong Core.Tests. | Ranh giới mới không được khóa; test kiểm tra text thay vì hành vi. |
| S10 | `docs/architecture.md` bỏ sót `Benchmarking → Imaging/Platform` và `Benchmark.Cli`. | Tài liệu lệch thực tế. |

**Đính chính so với review miệng:** ba `GlobalStateCollection` (App/Core/Integration tests) **không** gom về `TestSupport`: xUnit chỉ nhận `[CollectionDefinition]` trong cùng assembly với test dùng nó. Giữ nguyên.

**Không nên làm (đã cân nhắc):** tách thêm project cho `Platform.Windows`/`TurboJpeg`; bỏ WPF khỏi `Imaging` (mâu thuẫn hướng C3 đã chọn, lợi ích thấp); đổi hàng loạt `GetService`→`GetRequiredService` (đã có WD03).

## 2. Quyết định cần chốt trước khi làm

| Q | Câu hỏi | Mặc định đề xuất | **QUYẾT ĐỊNH (2026-09-20)** |
|---|---|---|---|
| Q-ST1 | `PerfAnalyze*` chuyển vào project mới `PhotoReview.PerfAnalysis` (`net10.0`) hay vào `Core`? | Project mới: giữ Core gọn, CLI/test tham chiếu trực tiếp. Chỉ vào Core nếu muốn tránh thêm project. | ✓ Project mới `PhotoReview.PerfAnalysis` (ST05 DONE) |
| Q-ST2 | `PerfCsvListener` + `DiagOptions` chuyển về `Core/Diagnostics`? | Có; giữ nguyên tên EventSource `PhotoReview-Perf` và event id (hợp đồng wire). Namespace đổi kéo theo CLI/tests. | ✓ Chuyển về `Core/Diagnostics` (ST04 DONE) |
| Q-ST3 | Chấp nhận Cli→App giữ nguyên, chỉ cấm reflection vào private? | Có. Cắt hẳn tham chiếu cần ST12 (project Presentation), chỉ mở sau khi có số đo lợi ích. | ✓ Chấp nhận, công khai MainWindow members (ST06 DONE) |
| Q-ST4 | Ctrl+Z là Move-only hay Move/Recycle? | Thuộc OC14; ST08 chờ quyết định này. | ✓ Move+Recycle: hoàn tác cả Move lẫn Recycle |

Tất cả quyết định đã chốt. ST04–ST06 DONE. ST08–ST09 sẵn sàng.

## 3. Thứ tự và đụng độ owner

`MainViewModel.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, scheduler và journal chỉ có **một owner tại một thời điểm**. Các task dưới đây đụng cùng file với OC14–OC18, WD02–WD05, IO*; không chạy chồng.

1. **ST00** baseline, khóa số test và rule kiến trúc mới (chỉ thêm test đang pass). ✓ DONE
2. **ST01** dọn code chết/thừa (marker Explorer, `ThumbnailModels`, alias file) — chạm `App.xaml.cs`, `MainWindow*`. ✓ DONE (Commit a6bbc55)
3. **ST02** `SourceSizeTracker` (sửa S3) → **ST03** trích `AppComposition` khỏi `App.xaml.cs`. ✓ DONE ST02 (Commit 775f3b2), ✓ DONE ST03 (Commit 479a1ab)
4. **ST04** đưa hạ tầng xuống dưới App; **ST05** tách `PerfAnalyze*` — hai task này độc lập với `MainViewModel`, ST05 chạy song song được với ST01–ST03 (khác file). ⏸ BLOCKED (chờ Q-ST1, Q-ST2)
5. **ST06** bỏ reflection của CLI vào `MainWindow` (sau ST03, ST04; sau OC15–OC17 nếu chúng đã lên lịch trên `MainWindow.xaml.cs`). ⏸ BLOCKED (phụ thuộc ST04)
6. **ST07** dependency bắt buộc + test builder cho `MainViewModel` (Commit b620999) → (OC14 chờ quyết định) → **ST08** `FileActionController` → **ST09** `DuplicateCleanupController` và `SiblingFolderNavigator`. ✓ DONE ST07; ⏸ BLOCKED ST08–ST09 (chờ OC14 + Q-ST4)
7. **ST10** dọn test/arch rules: ✓ ST10a Consolidate OperationJournalTests (Commit 986b20f), ✓ ST10b Add architecture rules (Commit ef0b601). **ST11** đồng bộ tài liệu (đang xử lý).
8. **ST12** chỉ điều tra (không triển khai): project `Presentation`. Chưa làm

Song song tối đa: ST05 ∥ (ST01→ST02→ST03); ST04 sau ST03 vì cùng đụng `App.xaml.cs`.

## 4. Kiểm thử và bàn giao (áp dụng mọi task)

- Mỗi task: `dotnet build PhotoReview.slnx -c Release` không lỗi mới; targeted tests của vùng chạm; `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` không giảm số test PASS so với ST00 trừ khi task ghi rõ test được thay thế bằng test hành vi tương đương.
- Refactor cơ học tách commit khỏi thay đổi hành vi (ST02 là commit hành vi duy nhất).
- Trước bàn giao: `./tools/verify-all.ps1`, publish `src/PhotoReview.App/bin/Release/net10.0-windows/publish`, `./tools/test-verify-gates.ps1`.
- Không đánh `DONE` từ đọc source: cần build + test pass thật. Không có evidence runtime GUI thì ghi rõ, không suy diễn.
- Nhánh `codex/structure-optimize-*`, không commit thẳng `master`; push và PR theo `AGENTS.md`.
- Rollback: revert từng commit task; không đụng journal, session, cache người dùng.

## 5. Phạm vi chưa làm

Không đổi mặc định `DecoderBackend`/`ScalingQuality`/`UseSourceBytesCache`; không đổi durability journal; không rewrite UI; không tách project vì thẩm mỹ. Tách project `Presentation` (ST12) chỉ là điều tra.
