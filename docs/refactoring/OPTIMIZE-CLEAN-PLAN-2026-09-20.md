# Review toàn repo và plan optimize / clean code

- Ngày: 2026-09-20. Baseline: `master`, `ba1317e54702e3af8bd32c0bf5d953b3766f1ba0` (merge PR #9); working tree sạch lúc bắt đầu.
- Trạng thái: review `DONE`; phần lớn correctness tasks đã triển khai. T89/native GUI và đo tối ưu vẫn chờ runtime evidence; không đánh dấu DONE từ source-only audit.
- Phạm vi review: Core + Platform.Windows; Imaging + TurboJpeg; App/UI/coordinators; Benchmarking + CLI/scripts/CI; tests và tài liệu trạng thái liên quan. Ba agent đọc các nhóm đầu, coordinator đối chiếu và review nhóm công cụ. Đây là review theo luồng/rủi ro xuyên repo, không phải bằng chứng mọi nhánh runtime đều đã chạy.
- Nguyên tắc: an toàn dữ liệu/chất lượng trước; giảm I/O và latency; tận dụng RAM có headroom. Không đổi mặc định decoder hoặc bật byte cache 16 GiB chỉ từ suy đoán; không ép dùng đủ RAM khi gây paging/OOM.

## 1. Bằng chứng và giới hạn

`dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual" --nologo --verbosity quiet` exit 0. Tổng 745 PASS: Architecture 7, Core 305, Imaging 203, Integration 59, App 171; 0 fail/skip. Core được chạy riêng lại để lấy summary không bị output analyzer warnings làm mất. Build có analyzer warnings; chưa coi warning style là defect.

Probe CLI thật dùng hai bản sao `tools/diag/samples/perf-sample.csv`, mỗi bản có `session.json` cùng scenario/mode/condition, khác `preloadWorkers=0/8`. `--perf-analyze` trả **2 file, 1 nhóm**, R-CONT=N/A thiếu cặp worker. Artifact tạm: `C:\Users\adminvti\AppData\Local\Temp\PhotoReview-review-perf-72df11ab30e746ef9c34951f84d3f818\summary.md`. Đây là fixture tổng hợp, không phải benchmark tốc độ trên ảnh người dùng.

Probe DuplicateFinder biên dịch source Core hiện tại trong scratch console net10.0: `removeNumbered=False: selected 2/2 [a.jpg,b.jpg]`; `True: selected 2/2 [a (1).jpg,a (2).jpg]`; cả hai `filesStillExist=True`. Chỉ gọi selection, không recycle/delete. Fixture: `C:\Users\adminvti\AppData\Local\Temp\photoreview-duplicate-fixture-61ed036f61174053b42c25bb30616887`. UI vẫn có batch confirmation; không tuyên bố đã xảy ra mất file thực tế.

Chưa chạy mới full verify-all/smoke/fault-injection/publish trong lượt review; chưa nghiệm thu native GUI/Recycle Bin hoặc benchmark ảnh thật. Test PASS không chứng minh các race/GUI bên dưới an toàn. Không chạy thử input ảnh gây native buffer overflow.

## 2. Phát hiện được ưu tiên

Line numbers dưới đây thuộc baseline trên. **S** = xác nhận cấu trúc/luồng từ source; triệu chứng runtime chưa tái hiện. **R** = đã tái hiện bằng probe trong lượt review. **H** = giả thuyết interleaving cần test điều khiển lịch chạy.

| ID | Mức / bằng chứng | Phát hiện, trigger, ảnh hưởng |
|---|---|---|
| F01 | P1 / R | `Core/FileActions/DuplicateFinder.cs:95-98`: group cùng hash toàn tên thường khi removeNumbered=false, hoặc toàn numbered khi true, trả tất cả thành candidate xóa; không bảo đảm survivor. App recycle toàn bộ danh sách sau xác nhận. Cần giữ ít nhất một bản mỗi group. |
| F02 | P1 / S | `Imaging.TurboJpeg/TurboJpegDecoder.cs:104-114`: `stride * scaledH` dùng int unchecked trước truyền pointer native. Ví dụ 32769²×4 = 4,295,229,444 nhưng int wrap thành 262,148: allocation có thể nhỏ hơn native output. Giới hạn trước native; không dựa vào OOM để bảo vệ. WIC sizing cũng cần checked nhưng WIC có tham số buffer length nên không đánh đồng mức rủi ro. |
| F03 | P1 / S | `Imaging/Preload/PreloadScheduler.cs:153-160,178-249,324-332`: drain nằm trong catch; return vì memory pressure và while kết thúc bình thường vì cancel có thể còn worker. Cancel+restart thay task được theo dõi; Dispose chỉ chờ task hiện tại. Cần ownership toàn bộ lifetime, không chỉ sửa thêm một catch. |
| F04 | P1 / S | `App/ViewModels/MainViewModel.cs:551-575` await hash với ConfigureAwait(false), rồi `WpfDialogService.cs:62` tạo Window trực tiếp. Cold hash thực sự await có thể tiếp tục trên MTA, làm batch review lỗi STA/cross-thread. Fake dialogs không phủ được. |
| F05 | P2 / S | `Imaging/Decoding/WpfBitmapImageDecoder.cs:34-37`: catch mọi exception khi preview rồi retry full resolution, gồm missing/access/OOM. Có I/O dư thừa và có thể làm memory pressure nặng hơn; trái hợp đồng không hidden retry khi file action. |
| F06 | P2 / S | `App/Coordinators/FolderLoadCoordinator.cs:97-101` remap bằng First cho từng path = O(n²). `App/App.xaml.cs:159-175` mỗi membership đổi lại stat cả folder để tính total, dù scan đã có CatalogEntry.Length. Đây là cơ hội tối ưu rõ ở folder lớn và Move/Delete liên tiếp; chưa có con số speedup. |
| F07 | P2 / S | `App/ViewModels/MainViewModel.cs:148`, `ImagePresenter.cs:210-218`, `CompareViewModel.cs:187`: toggle compare off nhưng present lại tự bật khi có pair. `MainViewModel.cs:773` OnEmpty không clear presenter.CurrentImage nên có thể giữ ảnh folder trước. |
| F08 | P2 / S + H | `Imaging/Caching/SourceBytesCache.cs:33-42,60`: check generation và Set không atomic với Clear; Evict không invalidate in-flight. `DiskCacheStore.cs:163-171` prune PNG nhưng bỏ `.png.meta` do `PreviewImageService.cs:123` tạo. Race cần barrier repro; orphan metadata thấy trực tiếp từ đường write/prune. |
| F09 | P2 / S | `Core/FileActions/UndoService.cs:149-150`, `OperationJournal.cs:24,75,119-123`: Undo tìm fingerprint trong API chỉ lấy 200 committed move cuối khi journal >=1 MiB; history trong phiên có thể dài hơn. Ngoài lỗi vượt giới hạn còn đọc/parse journal mỗi Undo. |
| F10 | P2 / S | `Benchmarking/BenchmarkImageExecutor.cs:36-43` không áp dụng worker/window/full-folder/reserve/disk-cache của profile vào scheduler/cache tương ứng. CLI không bật/tắt logging theo profile như GUI. `BenchmarkWorkloadRunner.cs:77-100` await decode xong mới action nên không đo race đã quảng bá; delete thực sự gửi fixture vào Recycle Bin dù comment nói permanent delete. |
| F11 | P2 / R | `Benchmarking/PerfAnalyze.cs:68,87,137-141`: grouping thiếu worker count, lấy worker metadata đầu tiên; trộn 0/8 workers thành một group. Sibling comparison còn cho phép khác cold/warm condition, dễ quy nhầm khác biệt cache thành contention. |
| F12 | P2 / S | `Benchmark.Cli/Program.cs:20-47,64`: --benchmark-all gồm cache-recovery/explorer-reindex, nhưng runner chủ động throw NotSupported cho cả hai; catch chỉ in console, không đưa failed phase vào summary. Toàn bộ batch có thể exit 1 mà summary thiếu các profile lỗi. Take(64) không sort cố định cũng làm dataset/whole-folder claim thiếu rõ ràng. |

Các concern cần xác minh trước khi sửa behavior, được bao gồm trong phạm vi test của plan:

- **C01, P1 candidate:** `Platform.Windows/WindowsRecycleBin.cs:50-69` chọn theo path+size, restore trước rồi mới xác minh timestamp. Nhiều phiên bản cùng path/size có thể restore sai bản; cần fixture native biệt lập.
- **C02, P2 candidate:** `Core/FileActions/FileActionService.cs:139,204-218`, `RecoveryRetryService.cs:74-81`: filesystem đã hoàn thành nhưng append Committed lỗi có thể bị báo như operation chưa thành công; append Failed có thể che lỗi gốc. Phải fault-inject và thống nhất outcome với App.
- **C03, H:** `Core/Session/SessionWriter.cs:98-113` lấy batch khỏi pending trước khi acquire write semaphore; batch cũ có thể ghi sau batch mới. Cần deterministic schedule chứng minh, không dùng stress timing làm bằng chứng duy nhất.
- **T89 còn mở:** production initial-mode callback vẫn dùng `(0,0)` (`App.xaml.cs:189`); đã có transaction Fit và pure math tests nhưng thiếu STA/layout + GUI acceptance. Tiếp tục plan T89 hiện hữu, không gọi bug hai-click đã được tái hiện lại trong lượt này.

## 3. Task triển khai chi tiết

Mỗi task chỉ chuyển DONE sau evidence/kiểm thử nêu dưới đây. Nếu concern không tái hiện hoặc contract đã được bảo vệ, ghi kết luận và không patch suy đoán. Prefix file `Core`, `Imaging`, `App`, `Benchmarking`, `Platform.Windows` bên trên tương ứng `src/PhotoReview.*`.

### 1. OC01 — Khóa baseline và regression fixtures — TODO

- Phụ thuộc: người dùng duyệt plan; coordinator thực hiện trước các patch.
- Files: plan này, `task_on_progress.md`, tests tương ứng, fixture helpers; không đổi behavior ở task này.
- Làm: feature branch `codex/optimize-clean-review`; chốt contracts survivor, journal outcome, ownership shutdown, cache invalidation và benchmark semantics. Tạo tests tái hiện F01–F05/F07/F09–F12; barrier tests cho F03/F08/C03; fixture C01/C02 biệt lập. Agent của mỗi gói sở hữu tests trong scope của gói đó.
- Xong khi: mỗi patch sau có failure/test hoặc evidence rõ trên baseline; phân biệt static/runtime, không cần ép mọi concern thành defect. Không thêm source-text assertion thay cho behavior test.
- Rủi ro/rollback: tests có thể động đến Recycle Bin; chỉ fixture temp có manifest, không dùng ảnh thật; giữ/xóa artifact riêng, không reset repo.

### 2. OC02 — Duplicate cleanup và UI thread — DONE

- Phụ thuộc OC01; song song với decoder/preload. Một agent sở hữu `DuplicateFinder`, `MainViewModel.RemoveDuplicatesAsync`, `WpfDialogService`, tests duplicate/dialog.
- Làm: policy survivor deterministic cho all-original/all-numbered/mixed; loại duplicate path; xử lý hash không hợp lệ. Dialog trên UI dispatcher; kiểm tra lại generation và identity trước batch mutation. Không bỏ màn xác nhận hiện có.
- Tests: hash cold asynchronous, STA thật, nhóm 2/3 bản, hash fail, folder đổi khi hash/dialog, file đổi giữa scan và action. Assert >=1 survivor mỗi content group và không recycle file ngoài snapshot đã duyệt.
- Rủi ro: thay candidate list và synchronous dispatcher deadlock; không giữ lock file-action khi chờ dialog. Rollback nguyên gói commit; không đảo thao tác file người dùng tự động.
- Kết quả 2026-09-20: duplicate survivor đã DONE trong commit `00cad65`; targeted 9/9 và full gate 748/748. UI-thread dialog boundary còn TODO do cần dispatcher abstraction và STA test riêng.
- Kết quả bổ sung: commit `abecebc`; MainViewModel dùng IUiScheduler/DispatcherUiScheduler cho dialog sau async hash, targeted MainViewModelAdvanced 8/8 và full gate wave bốn 758/758.

### 3. OC03 — Decoder safety và fallback — DONE

- Phụ thuộc OC01; độc lập OC02/04. Files: TurboJpegDecoder, WicDirectDecoder sizing, WpfBitmapImageDecoder, decoder/quality tests.
- Làm: checked long cho dimensions/stride/buffer, reject trước allocation/native nếu vượt supported budget; narrow WPF fallback theo lỗi codec thật, giữ missing/access/cancel/OOM nguyên nghĩa. Không tự đổi default backend hoặc giảm chất lượng.
- Tests: arithmetic biên không allocate GB, xác nhận native không được gọi khi reject; preview fallback hợp lệ; missing file không retry; real fixtures ICC/EXIF/alpha, requested/actual backend, pixel quality gates.
- Rủi ro: reject ảnh vốn có thể đọc được; thông báo rõ giới hạn và giữ backend WPF mặc định. Rollback commit behavior riêng; không tái bật native path mất guard.
- Kết quả 2026-09-20: commit `6a4ec01`; decoder targeted 108/108 và full gate 748/748. Đã dùng checked long/overflow guard cho TurboJPEG và thu hẹp fallback WPF; quality/native large-image acceptance vẫn cần fixture bổ sung khi triển khai OC11.

### 4. OC04 — Preload lifetime — DONE

- Phụ thuộc OC01; agent riêng. Files: PreloadScheduler, PreloadSafetyTests, adapter/shutdown callsites nếu contract đổi.
- Làm: drain mọi exit; giữ ownership các scheduler generation cũ tới khi worker kết thúc; cancel/restart/dispose không bỏ task và không dispose semaphore trước worker finally. Không block UI dispatcher bằng continuation cần chính dispatcher.
- Tests: memory pressure giữa batch, cancel ở boundary while, restart trước old drain, close/folder switch khi target chậm; barrier xác định, chạy regression race 30 lần sau sửa.
- Xong khi: task completion/disposal đồng nghĩa worker đã settle, không late cache write/Ode/unobserved error; native window close probe không treo.
- Rủi ro: tăng thời gian shutdown; có lifetime async rõ thay vì timeout che lỗi. Rollback riêng commit scheduler, không ghép cache optimization.
- Kết quả 2026-09-20: commit `4a81ba8`; targeted PreloadSafety 5/5 và full gate 748/748. Đã giữ scheduler lifetimes cũ tới khi drain và test cancel/restart trước drain.

### 5. OC05 — Cache invalidation và disk quota — DONE

- Phụ thuộc OC01; có thể song song OC04, không sửa scheduler. Files: SourceBytesCache, PreviewImageService persistence, DiskCacheStore, cache tests.
- Làm: epoch/lock cho check+publish, path invalidation in-flight và case normalization; prune PNG+metadata cùng entry, dọn orphan trong phạm vi cache, tính quota nhất quán.
- Tests: Clear/Evict giữa read và publish bằng barrier; cùng path khác casing; write/prune/clear race; quota gồm metadata và không xóa file ngoài cache directory.
- Xong khi: invalidated work không repopulate; bytes cache không tăng lại sau clear từ work cũ; không orphan sau prune. Giữ behavior mặc định SourceBytesCache off.
- Rủi ro: locking tăng contention; đo cache-hit latency. Rollback độc lập phần RAM/disk; cache là rebuildable, không xóa nguồn ảnh.
- Kết quả 2026-09-20: commit `3db9ae6`; targeted cache tests 10/10 và full gate wave hai 752/752. Evict invalidates in-flight reads; PNG metadata được prune cùng companion và orphan metadata được dọn.

### 6. OC06 — Journal / Undo / Recovery / Session — IN PROGRESS → Undo/journal/session DONE, recovery/native TODO

- Phụ thuộc OC01; một owner Core cho contract file actions. Files: UndoService, OperationJournal, FileActionService/result, RecoveryRetryService, SessionWriter, WindowsRecycleBin và tests; App consumer chỉ hợp nhất sau OC02/07.
- Thứ tự: repro F09/C01–C03; typed history chứa operation ID + fingerprint; tách startup-tail khỏi lookup; phân biệt filesystem outcome và journal durability; ordering single writer/sequence cho session; identity check trước restore.
- Tests: >200 moves và journal >1 MiB, reuse destination, undo mismatch/restart; Prepared/Committed/Failed append fault injection; stale session batch; native restore hai version cùng path/size khác timestamp, destination collision.
- Xong khi: history hiện hành undo được trong giới hạn công bố; không đọc journal mỗi undo đã có history; vị trí file/UI/journal nhất quán khi lỗi; latest session thắng; native restore đúng version.
- Rủi ro: contract rộng, chia 3 commit con Undo, outcome/restore, Session; giữ đọc journal cũ tương thích. Rollback bằng revert code, không rewrite/xóa journal hoặc revert filesystem mutation.
- Kết quả 2026-09-20: Undo history commit `295f04b`, regression >250 moves pass. Fingerprint được giữ trong in-memory history để không phụ thuộc startup tail 200. FileAction durability, recovery retry, SessionWriter ordering và native recycle identity còn TODO.
- Kết quả bổ sung: `481c130` tách filesystem outcome khỏi journal durability khi append Committed lỗi; `42b92e2` bảo đảm SessionWriter bỏ batch cũ; `32156f5` hiển thị rõ retry hoàn tất nhưng journal lỗi và giữ lỗi mutation gốc. FileAction/Session/Recovery tests và full gate 759/759 pass. Native restore identity còn TODO.
- Kết quả wave sáu: `3a5c168` tách `RecycleCandidateSelector`, xác minh path/size/timestamp trước mutation và từ chối ambiguity; candidate tests 2/2, integration/full gate 761/761. Chưa chạy live Recycle Bin fixture nên native acceptance vẫn mở.

### 7. OC07 — Display state và hoàn thành T89 — IN PROGRESS → display subset DONE, T89 TODO

- Phụ thuộc OC01; agent UI riêng, merge tuần tự sau OC02 nếu cùng MainViewModel.
- Files: MainViewModel, ImagePresenter, CompareViewModel, MainWindow, WpfPresentationSink, App composition, App/STA tests; tham chiếu `T89-FIT-LAYOUT-PLAN.md`.
- Làm: tách intent compare khỏi loaded visibility; empty-folder clear presenter/compare; unify Fit button/key/initial-mode qua viewport owner có version. Giữ frame retention của Move/Delete đang load, không áp dụng empty-folder clearing nhầm sang action.
- Tests: compare off/on với pair; single/compare -> empty; stale navigation; Fit một lần so hai lần <=0.5 DIP, offsets <=0.5 DIP; portrait/landscape, DPI, resize, thumbnail->full, wheel->Fit->pan. STA layout test và GUI acceptance đều cần.
- Rủi ro: layout feedback và stale callbacks; bounded convergence/versioning theo T89. Rollback display fixes và T89 thành commit riêng; không đánh DONE từ pure math tests.
- Kết quả 2026-09-20: display subset commit `5edd15d` + test seam `ecdbf2b`; App targeted 11/11, full gate 752/752. Compare toggle off và empty-folder clear đã được sửa. T89 layout/STA/GUI acceptance vẫn TODO.

### 8. OC08 — Folder/catalog I/O và allocations — DONE

- Phụ thuộc OC01; triển khai sau khi UI contract OC07 ổn định hoặc branch riêng chỉ sở hữu FolderLoadCoordinator/Core Catalog. Coordinator merge App composition tránh đụng OC07.
- Files: FolderLoadCoordinator, ReviewCatalog, ImageSortService, GetTotalSourceBytes composition; tests catalog/folder/CountingFileSystem.
- Làm: sort entries trực tiếp hoặc map O(n); tái dùng Length/LastWrite đã scan; total bytes cập nhật theo membership/metadata thay vì re-stat toàn folder. Chỉ thêm index/snapshot cache khi trace cho thấy hot path cần.
- Tests: order parity Natural/size/Explorer, case paths, file mất/đổi; 1k/10k/50k entries; counting stat sau 100 removals. Xong khi không còn remap O(n²) và không N stat sau mỗi removal; report time/alloc trước-sau.
- Rủi ro: stale metadata; vẫn kiểm identity tại decode/file action boundary. Rollback riêng commit performance, giữ tests correctness.
- Kết quả 2026-09-20: commit `a16b327`; sort entries O(n) và tái dùng Length/LastWrite đã scan. ImageSortService/FolderLoadCoordinator targeted tests pass; full gate 755/755.

### 9. OC09 — Benchmark đúng semantics — IN PROGRESS → profile propagation DONE, workload action/report TODO

- Phụ thuộc OC01; agent tooling, song song Core/UI. Files: BenchmarkModels/Profiles/ImageExecutor/WorkloadRunner/Engine, CLI Program, BenchmarkWindow, integration tests; preload options cần phối hợp OC04.
- Làm: từng field profile có effect quan sát được hoặc bỏ/đánh unsupported rõ; cấu hình worker/window/reserve/fullfolder/disk/log nhất quán CLI/GUI. Race workload phải chặn decode ở barrier rồi thực hiện production file-action path. Không quảng bá decode-only là key-to-present/quality proof. Unsupported/failed profile luôn có record; --benchmark-all có capability semantics rõ; manifest dataset sorted, count/cap rõ. Tránh tích lũy fixture vào Recycle Bin ngoài native test riêng.
- Tests: profile A/B thay actual options/counters; CLI/GUI parity; action xảy ra khi decode pending; summary chứa đủ requested profiles và exit code đúng; logging restore; reproducible selection.
- Rủi ro: số cũ không so trực tiếp được với workload mới; ghi schema/semantics version, giữ raw baseline. Rollback gói tooling, không dùng kết quả sai để chọn default.
- Kết quả 2026-09-20: commit `fb5155f`; `Workers`, `MemoryReserveBytes`, `DiskCache` đã truyền vào runtime. Commit `6dee023` bắt đầu file action khi decode đang in-flight. Commit `6ad5c15` thêm deterministic dataset manifest và giữ failed profile trong summary; targeted/full gate wave sáu pass. Chỉ còn benchmark trên ảnh thật và quality/perf interpretation.

### 10. OC10 — Perf analysis grouping — DONE

- Phụ thuộc OC01; độc lập OC09 nếu chỉ sửa PerfAnalyze*. Files: PerfAnalyze, Stats/Report/Rules, PerfAnalyzeTests.
- Làm: key gồm worker và các điều kiện thực nghiệm cần tách (commit/config/fixture khi có); R-CONT chỉ so cùng cache condition và dataset, khác worker. Mixed/unknown metadata phải cảnh báo, không lấy first silently.
- Tests: hai fixture probe 0/8 tạo hai group; cold/warm không thành cặp contention; mixed commits không trộn; thiếu metadata => N/A có lý do.
- Rủi ro: output group/schema đổi; cập nhật consumers/docs, không mutate raw CSV. Rollback analyzer commit; raw có thể phân tích lại.
- Kết quả 2026-09-20: commit `b8b97fb`; PerfAnalyze tests 33/33 và full gate wave sáu 761/761. Group key tách worker count/condition; R-CONT không so sánh khác condition.

### 11. OC11 — Tối ưu decode/RAM theo số đo — TODO

- Phụ thuộc OC03–05, OC08–10 và có fixture ảnh thật. Files: SourceBytesCache/decoder memory adapters, WIC transform, disk persistence policy; tests/benchmark reports.
- Làm theo từng experiment: bỏ ToArray copy khi memory đã array-backed, đo sync-over-Task.Run; đo write-through PNG cache contention; xác minh nhánh WIC GetClosestSize đang không dùng kết quả. Chỉ triển khai DCT transform thật nếu có lợi và quality gate PASS; nếu không, bỏ nhánh no-op và sửa mô tả. Đo decoded/source byte budget cùng nhau trước đề xuất bật byte-cache toàn folder.
- Matrix: Original/Preview, cold-app/warm RAM/warm disk tách riêng; folder <16 GiB nguồn và lớn hơn, high-MP/ICC/EXIF. Ít nhất 3 runs, >=30 samples/workload; P50/P95/P99, reads/bytes, allocations/GC, peak/private/working set, cache hit và quality. Không gọi cold OS nếu chưa kiểm soát OS cache.
- Tiêu chí: cùng fixture/config, quality không giảm; không thêm source reads cho warm navigation; P95 không thoái lui >5% vượt noise giữa runs. Chỉ giữ optimization có lợi lặp lại; folder vừa budget được warm hết khi headroom cho phép, không paging/OOM. Không hứa gain % trước đo.
- Rủi ro: RAM native/decoded khác source bytes, tốc độ phụ thuộc thiết bị. Rollback từng experiment/feature flag; giữ WPF/HighQuality default trừ khi được duyệt riêng.

### 12. OC12 — Clean code có giới hạn — TODO

- Phụ thuộc các behavior fixes trong file bị chạm; không chạy agent cleanup chồng cùng file.
- Files: MainWindow helpers/test hooks, MainViewModel Undo paths, decoder no-op branch, duplicate constants/options/JSON serializers, tooling scripts/docs.
- Làm: migrate reflection/compatibility tests sang public behavior/test seam rồi mới bỏ fields/hooks; gộp restore/persist/notify lặp; shortcut rebuild theo settings change sau audit mutation; bỏ dependency/field/local không dùng thật. Giữ comments race/contracts/native ownership; không mass-format/rename test chỉ để im analyzer.
- Đồng bộ verify-release default path với README/verify-all; CI chạy safety gates và test-verify-gates, xem codex branch trigger; script publish validate output path trước recursive clear. Mọi đổi script có targeted contract/fault tests, không đổi toolchain/package hàng loạt.
- Tests: architecture, tests đã migrate còn phủ behavior; script gate failure propagation; build warnings baseline vs sau để ưu tiên resource/correctness warnings.
- Rủi ro: tests còn dùng legacy hooks thật; rollback commit cleanup cơ học riêng, không lẫn behavior.

- Kết quả 2026-09-20: focused clean pass đã hoàn tất một phần an toàn: `4b6b5ad` Core guards/serializer/invariant culture, `77c4441` Imaging argument guards, `8249fd2` App serializer reuse. Full gate sau clean pass 762/762. Warning còn lại chủ yếu là API ordering/lifecycle/test naming; không đổi public signature hoặc lifecycle khi chưa có task riêng.

### 13. OC13 — Tích hợp, nghiệm thu, publish và bàn giao — TODO

- Phụ thuộc các task đã được chọn triển khai; coordinator thực hiện tuần tự, không nhiều build/publish cạnh tranh output.
- Chạy targeted tests từng gói; full `./tools/verify-all.ps1` Release (xUnit, smoke, fault-injection, publish, verify-release), thêm `./tools/test-verify-gates.ps1`. Sau mỗi commit chạy lại Release gate theo AGENTS.
- Publish đúng `src/PhotoReview.App/bin/Release/net10.0-windows/publish`; command dự phòng: `dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish`.
- GUI/native acceptance OC04/06/07, benchmark report OC11; không đánh pass nếu môi trường thiếu khả năng chạy. Ghi blocker cụ thể, không thay runtime evidence bằng source assertion.
- Cập nhật task_on_progress ngắn, plan/tracker/README liên quan; feature branch, commit chỉ scope được duyệt, push `origin`, mở PR vào `master` và attach PR. Không commit thẳng master.
- Rollback: revert từng commit gói trên feature branch; giữ baseline SHA/benchmark artifacts; không git reset mất việc, không xóa session/journal/cache nguồn người dùng.

## 4. Phân chia multi-agent và hợp nhất

Tối đa 3 workers + coordinator; không cần chạy mọi task cùng lúc.

1. Wave A: agent A OC02, agent B OC03, agent C OC04; coordinator chốt regression/probe và OC10.
2. Wave B: agent A OC06; agent B OC05; agent C OC07 sau merge OC02. Coordinator OC09 sau thống nhất preload options với OC04.
3. Wave C: agent A OC08; agent B OC11 sau benchmark gate; agent C OC12 trên scope không xung đột. Sau cùng coordinator OC13.

Đầu ra mỗi agent: diff giới hạn file đã nhận, ID finding/task, repro trước/sau, lệnh và kết quả test, assumptions/rủi ro; không tự đổi shared interfaces ngoài scope. Coordinator thống nhất contract trước, merge lần lượt, chạy regression liên lớp rồi mới cấp task tiếp. `MainViewModel`, `App.xaml.cs`, scheduler và journal luôn chỉ có một owner tại một thời điểm. Nếu scope mới vượt plan đã duyệt, ghi cập nhật và chờ duyệt phần mới.

## 5. Phạm vi chưa làm

Không rewrite framework UI/COM, đổi decoder mặc định, bật diagnostics thường trực, xóa work artifacts hàng loạt hoặc sửa package vì “mới hơn”. Không lấy lịch sử review 2026-09-18 làm bằng chứng defect hiện tại; chỉ dùng làm danh sách kiểm tra. T89 tiếp tục giữ IN PROGRESS tới khi có GUI/layout evidence.
