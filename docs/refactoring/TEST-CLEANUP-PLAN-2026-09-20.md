# Plan dọn dẹp test — ưu tiên đọc đĩa, Next ảnh, Move/Delete liên tục (TC00–TC11)

- Ngày: 2026-09-20. Baseline: `master` `5dc5cda` (+ nhánh docs `codex/structure-optimize-plan`).
- Trạng thái: khảo sát `DONE` (đọc source test + production); **chưa sửa test hay source nào**; chưa chạy test/đo thời gian trong lượt này. Mọi con số "số test", "số lần Delay" là đếm bằng grep, không phải kết quả chạy.
- Liên quan: [`STRUCTURE-OPTIMIZE-TASKS.md`](STRUCTURE-OPTIMIZE-TASKS.md) (ST10 nay trỏ về plan này), [`test-parity.md`](test-parity.md), `OPTIMIZE-CLEAN-PLAN-2026-09-20.md` (OC11 acceptance, OC06 native).
- Mục tiêu: có một bộ test **chạy production code thật** bảo vệ đường nóng của người dùng — đọc đĩa tối thiểu, Next liên tục, Move/Delete liên tục trên ảnh có kích thước/định dạng thật — trước khi dọn phần test còn lại.

## 1. Phát hiện từ khảo sát (bằng chứng)

| ID | Bằng chứng | Hệ quả |
|---|---|---|
| G1 | `Core.Tests/Services/InterleavedFileActionSequenceTests.cs`: kịch bản Next→Move→Delete chạy trong **constructor** trên `List<string>` + `File.Move/Delete` + `Math.Min`; `[Fact]` chỉ `Assert.True(bool)`. Không gọi `ReviewCatalog`, `FileActionService` hay `MainViewModel`. `BenchmarkScenarioTests.cs` và `FileActionConcurrencyTests.InterleavedSequenceLeavesDeterministicState` lặp lại đúng kịch bản đó lần hai và ba, cũng chỉ trên file/local index. Comment còn trỏ `Program.cs` `RunInterleavedFileActionSequence` (đã lỗi thời). | Ba nhóm test này **không thể fail** vì regression production; tạo cảm giác an toàn giả cho đúng vùng quan trọng nhất. |
| G2 | Test VM chỉ có thao tác đơn: `MainViewModelFileActionTests` (9 test, mỗi test một action). `RunActionAsync_ConcurrentAction_IsRejectedByGate` chỉ assert `IsBusy == false` cuối cùng; không assert action thứ hai không đụng file/catalog. Production: `ExecuteFileActionCoreAsync` `if (_fileActionService.IsBusy) return;` — action thứ hai **bị bỏ im lặng**. Không có test nào cho chuỗi Next×N, Move×N, Delete×N liên tiếp. | Chưa có bằng chứng "nhấn nhanh liên tục" cho kết quả đúng; hành vi bỏ phím khi bận chưa được chốt là chủ ý hay không (Q-T1). |
| G3 | Ảnh test: `TestImages.PreviewPng` 1×1; `MainViewModelFileActionTests` dùng PNG 1×1 hoặc byte text; `FixtureGenerator` (JPEG 4000×3000, 8000×6000, EXIF, ICC) chỉ nằm trong `Imaging.Tests` và dùng WPF, nên App/Integration/Core.Tests không dùng lại được. | Decode vài µs, không có cửa sổ race "decode đang chạy khi file bị Move/Delete", áp lực RAM/preload không thực. |
| G4 | Bất biến đọc đĩa chỉ kiểm cho **một** decode (`PreviewDecodeRecordsExactlyOneSourceRead`, `DiskCacheHitAvoidsSourceRead`...). Không test nào cho chuỗi: warm Next = 0 lần đọc nguồn; duyệt hết folder mỗi file đọc đúng một lần; Move/Delete không đọc/stat lại các ảnh còn lại; hash đọc một lần. Tiêu chí OC11 "không thêm source reads cho warm navigation" chưa có test tự động. | Đúng nguyên tắc số 1 của `AGENTS.md` (hạn chế đọc đĩa) nhưng không có gate. |
| G5 | Nhiều đường đọc nguồn **không đi qua `IFileSystem`**: `PreviewImageService.cs:381,419` (`new FileStream`), `SourceBytesCache.cs:51`, `FileHashService.cs:51`, `PreviewImageService.cs:235` (disk cache). `CountingFileSystem` chỉ đếm stat/open trong `IFileSystem`; số đọc nguồn dựa vào việc từng nơi tự gọi `ReviewMetrics.RecordSourceRead/RecordSourceOpen`. | Một đường đọc mới quên ghi metric sẽ **vô hình** với mọi test đọc đĩa; đếm bằng metrics chưa chứng minh đủ. |
| G6 | Delete trong mọi test VM dùng `FakeRecycleBin`; test native duy nhất là `WindowsRecycleBinCandidateTests` (selector). Live Recycle Bin acceptance vẫn TODO (OC06). | Hành vi thật của Shell (độ trễ, nhiều bản cùng tên, restore đúng bản) chưa được kiểm ở tốc độ Delete liên tục. |
| G7 | `FileActionConcurrencyTests` assert `watch.Elapsed < 1s` trên `File.Move`; khoảng 50 dòng `Task.Delay`/`WaitAsync`/`TimeSpan.From*` rải ở test (nhiều nhất `PreloadSafetyTests` 10, `PreviewImageServiceTests` 6, `MainWindowBehaviorTests.*` 11, `StaTestHost` 3). | Rủi ro flake theo tải máy; test kiểm race dựa thời gian thay vì barrier. |
| G8 | `FixtureTests.RealWorldPhotosManualTest` thoát sớm (`return`) khi thiếu `PHOTOREVIEW_FIXTURE_DIR` → báo PASS mà không kiểm gì; xunit là 2.9.3 (không có `Assert.Skip`). Ảnh thật chưa bao giờ đi qua kịch bản Move/Delete/Next. | PASS giả; không có đường an toàn để chạy trên ảnh thật (phải chạy trên **bản sao**). |
| G9 | Trait chỉ dùng thưa (`Integration` 6, `Manual` 1, `Quality` 1, `Unit` 1, `Architecture` 1) → không chọn được "bộ đường nóng" để chạy riêng/lặp. Trùng lặp/lạc chỗ: hai `OperationJournalTests`, `SourcePresenceTests` (text), `PerfAnalyzeTests` ở Integration (ST05). | Không chạy nhanh được bộ quan trọng; dọn dẹp chưa có ưu tiên. |

## 2. Nguyên tắc

1. Test đường nóng phải chạy **production code thật**: `MainViewModel` + `FileActionService` + `OperationJournal` + `PhysicalFileSystem` + `PreviewImageService` + decoder thật trên **JPEG sinh ra có kích thước thật**. Chỉ giả những gì không thể giả an toàn: Recycle Bin (mặc định), dialog, dispatcher.
2. Kiểm bằng **trạng thái và bộ đếm**, không bằng thời gian tường. Race dùng barrier (`TaskCompletionSource`), không `Task.Delay`. Thời gian chỉ dùng làm timeout bảo vệ, nêu rõ thông điệp lỗi.
3. Test dùng **mô hình kỳ vọng độc lập** (shadow model: danh sách catalog + tập file mong đợi) so với trạng thái thật sau từng bước, seed cố định và log seed khi fail.
4. Ảnh thật của người dùng: **không commit, không ghi tên folder**, chỉ qua biến môi trường `PHOTOREVIEW_FIXTURE_DIR`, luôn **copy sang thư mục tạm** trước khi Move/Delete; không đụng bản gốc. Recycle Bin thật chỉ với file tạm sinh ra, test tự khôi phục (Undo) để không để rác; không xóa vĩnh viễn từ Recycle Bin bằng code.
5. Không xóa test cũ trước khi có test production-path tương đương; mỗi checkpoint cũ ánh xạ sang một test mới (bảng ở TC08).
6. Không giảm số PASS ngoài phần được thay thế có ghi ánh xạ; không nới assertion, không đánh `Skip` để qua.

## 3. Quyết định cần chốt

| Q | Câu hỏi | Mặc định đề xuất |
|---|---|---|
| Q-T1 | Khi người dùng bấm Move/Delete liên tục lúc action trước còn chạy: **bỏ** phím (hành vi hiện tại, `IsBusy` → return) hay **xếp hàng** (queue có thứ tự) hay bỏ nhưng báo trạng thái? | Chưa đổi production. TC05 ghim hành vi hiện tại bằng test mô tả (số action bị bỏ, không file nào bị đụng sai) và có thêm số liệu; nếu bạn chọn queue/feedback thì mở task hành vi riêng (thuộc OC14/INV-4). |
| Q-T2 | Cho phép một cổng đếm đọc nguồn trong production (ví dụ `ISourceReader`/hook ghi metric cho mọi `FileStream` đọc nguồn) để G5 không còn điểm mù? | Có, seam nhỏ, không đổi hành vi; làm ở TC02. Nếu không: chỉ đếm được các đường đã tự ghi metric. |
| Q-T3 | Folder ảnh thật cho TC07 (chỉ đường dẫn qua biến môi trường, không lưu vào repo) và quy mô (mặc định 30–200 ảnh)? | Do bạn cung cấp lúc chạy; task `BLOCKED` tới khi có. |
| Q-T4 | Cho phép test `Native` chạy Recycle Bin thật trên máy này (chỉ file tạm, tự Undo)? | Chỉ chạy khi bạn xác nhận, ngoài gate mặc định. |

## 4. Task (theo thứ tự ưu tiên)

Mức: L1 cơ học, L2 vừa, L3 đụng invariant. Coordinator đọc mọi diff test (không chấp nhận test bị nới hoặc xóa để qua). Trạng thái ban đầu `TODO` trừ ghi chú.

### Nhóm P0 — bộ an toàn đường nóng

#### TC00 — Baseline, kiểm kê, trait — TODO (L1)
- **Làm:** chạy full test `--logger trx` ghi thời gian từng test; chạy riêng các test liên quan G2/G4/G7 lặp **30 lần** (`--filter`) để có tỉ lệ flake nền; lập bảng kiểm kê mọi test: `giữ / viết lại / gộp / xóa` + lý do (ưu tiên vùng đường nóng). Thêm trait chuẩn: `HotPath` (chạy mặc định, nhanh), `Stress` (lặp/seed nhiều, ngoài gate mặc định), `Native` (Recycle Bin/Shell thật), `Manual` (ảnh thật), `Slow`.
- **Files:** `docs/refactoring/test-parity.md` hoặc file kiểm kê mới, `task_on_progress.md`, trait trên test hiện có (chỉ thêm attribute).
- **Xong khi:** có số PASS/thời gian baseline, danh sách test chậm nhất, tỉ lệ flake nền, bảng kiểm kê; `--filter "Category=HotPath"` chạy được.

#### TC01 — Fixture ảnh thật dùng chung — TODO (L2)
- **Bối cảnh:** G3. `FixtureGenerator` dùng WPF nên không vào được `TestSupport` (`net10.0`, dùng chung Core.Tests).
- **Làm:** tạo `tests/PhotoReview.TestSupport.Windows` (`net10.0-windows`, WPF) chuyển/dùng lại `FixtureGenerator` (Imaging.Tests trỏ vào đây); thêm `PhotoFolderBuilder`: sinh folder N ảnh JPEG gồm 4000×3000 (~12 MP), một số 6000×4000/8000×6000, có ảnh EXIF orientation, PNG, một ảnh hỏng (truncated) và một file lạ. Mỗi ảnh khác byte (khác fingerprint) nhưng chi phí sinh thấp: mã hóa một số ít biến thể rồi nhân bản có thay đổi bảo đảm decode hợp lệ (xác minh bằng decode thật). Có cache theo lượt chạy để không sinh lại mỗi test.
- **Không làm:** không commit ảnh; không thêm ảnh thật vào repo.
- **Xong khi:** builder tạo folder 50 ảnh ≲ vài giây (đo và ghi số); Core.Tests không bị kéo phụ thuộc WPF; test Imaging hiện có vẫn PASS.

#### TC02 — Cổng đo đọc đĩa (`ReadBudgetProbe`) — TODO (L3)
- **Bối cảnh:** G5, ⛔Q-T2.
- **Làm:** (a) helper test chụp `ReviewMetrics.Snapshot()` + `CountingFileSystem` trước/sau một kịch bản và cho các phép assert: `SourceReads`, `SourceBytesRead`, `SourceOpenCount`, `TopSourceOpens` theo path, `StatCount`, `PreloadHits`, `DiskCacheHits`. (b) Kiểm phủ ghi metric: liệt kê mọi nơi mở nguồn (danh sách trong G5) và thêm test **đối chứng độc lập**: đọc bằng `FileSystemWatcher`/đếm tại `PhysicalFileSystem` bọc `CountingFileSystem` cho các đường đi qua `IFileSystem`, và với `FileStream` trực tiếp thì thêm seam Q-T2 hoặc ghi rõ đường nào chưa đo được. (c) Test kiểm: nếu một đường đọc không ghi metric thì probe phải báo (fail có chủ đích trên một reader giả).
- **Files:** `tests/*` helper; production chỉ khi Q-T2 = có (seam nhỏ trong `Imaging/Caching`, `FileHashService`), commit riêng.
- **Xong khi:** probe dùng được ở App.Tests/Imaging.Tests; danh sách đường đọc có đánh dấu "đo được / không đo được" và lý do; không đổi hành vi production.

#### TC03 — Test bất biến đọc đĩa theo chuỗi — TODO (L3)
- **Phụ thuộc:** TC01, TC02. Category `HotPath`.
- **Test (ảnh JPEG thật, folder ≥ 20 ảnh, RAM cache/preload bật theo cấu hình mặc định):**
  1. Duyệt tuần tự hết folder (có preload): mỗi file nguồn được mở **đúng một lần** (`SourceOpenCount` theo path = 1); `SourceBytesRead` ≤ tổng kích thước file (+ dung sai ghi rõ).
  2. Warm Next/Previous trong cửa sổ cache: delta `SourceReads = 0`, `StatCount = 0`.
  3. Quay lại ảnh đã xem trong ngân sách RAM: `SourceReads = 0`.
  4. Move/Delete ảnh hiện tại: delta đọc của **các ảnh còn lại = 0**; `StatCount` tăng ≤ hằng số nhỏ độc lập số ảnh (khóa hồi quy F06 cùng ST02); ảnh vừa Move/Delete không bị đọc lại sau khi bị bỏ khỏi catalog.
  5. Hash (`FileHashService`) đọc một lần mỗi fingerprint; duplicate scan không đọc lại ảnh đã hash.
  6. Preload không đọc file đã có trong cache; preload bị hủy khi đổi folder không đọc thêm sau khi drain.
  7. `UseSourceBytesCache=false` (mặc định): không có bản sao byte thừa (đo qua RAM/`SourceBytesCache` counters).
- **Xong khi:** mỗi test có phép assert số cụ thể; chạy 30 lần liên tiếp không lỗi; phủ tiêu chí OC11 "không thêm source reads cho warm navigation".

#### TC04 — Next liên tục (key-repeat) — TODO (L3)
- **Phụ thuộc:** TC01. Category `HotPath` (+ biến thể `Stress`).
- **Test:** qua `MainViewModel` + `ImagePresenter` + `PreviewImageService` + decoder thật: gọi `NextAsync` K lần **không await** từng lần (mô phỏng giữ phím) rồi await tất cả; xen kẽ Next/Previous; giữ phím ở đầu/cuối folder (kẹp biên); Next khi decode ảnh trước còn chạy (barrier trên decoder giả cho một biến thể, decoder thật cho biến thể khác).
- **Assert:** ảnh cuối trình diễn = `Catalog.Current`; không trình diễn ảnh cũ sau ảnh mới (INV-1, token bị thay thế); số lần present ≤ K; không exception chưa quan sát; tác vụ bị hủy không đầu độc scheduler/cache; số lần đọc nguồn bị chặn trên (≤ K + kích thước cửa sổ preload, ghi rõ công thức); không rò handle (file vẫn Move/Delete được ngay sau đó).
- **Xong khi:** 30 lần liên tiếp ổn định; test fail được khi cố ý phá token `ImagePresenter` (kiểm mutation thủ công, ghi trong nhật ký).

#### TC05 — Move/Delete liên tục qua production path — TODO (L3, đụng INV-3/4/5/6)
- **Phụ thuộc:** TC01, TC02; ⛔Q-T1 chỉ ảnh hưởng ca (b). Không chạy chồng OC14/ST07–ST09 trên cùng file test VM; thống nhất thứ tự trước.
- **Bộ test** (`MainViewModel` + `FileActionService` + `OperationJournal` + `PhysicalFileSystem` thật; Recycle Bin giả có độ trễ cấu hình được bằng barrier):
  - a. **Move×N tuần tự** (await từng cái) trên N ảnh thật: mỗi ảnh chuyển đúng một lần, không sót/không trùng, thứ tự Next đúng (INV-3), journal `Prepared→Committed` đủ N, lịch sử Undo N mục.
  - b. **Bấm liên tục không await**: ghim hành vi hiện tại của gate — đếm action bị bỏ, khẳng định không file nào bị đụng sai, không double-process, catalog nhất quán; biến thể có độ trễ Recycle để phản ánh Delete thật chậm hơn Move. Kết quả đo (số phím bị bỏ) ghi vào nhật ký làm dữ liệu cho Q-T1.
  - c. **Chuỗi ngẫu nhiên có seed** Next/Move/Delete/Undo/Prev có mô hình kỳ vọng: sau **mỗi bước** so sánh `catalog.Paths`, tập file trên đĩa (nguồn/đích/Recycle giả) và journal với mô hình. Seed cố định trong gate, nhiều seed trong `Stress`; in seed khi fail. Đây là bản thay thế thật cho ba test G1.
  - d. **Delete ở cuối/đầu folder** chọn đúng slot còn lại; folder rỗng sau chuỗi Delete → presenter/compare được clear (F07).
  - e. **Action khi decode ảnh hiện tại đang chạy** trên JPEG thật (decoder giữ handle share `ReadWrite|Delete`, INV-8): Move/Delete hoàn thành mà **không chờ decode** (barrier, không đo giây).
  - f. **Chèn lỗi** tại action thứ k (move lỗi/journal lỗi): ảnh khôi phục đúng vị trí, các action sau vẫn chạy, trạng thái không lệch.
  - g. **Đổi folder giữa chuỗi** (INV-5): action cũ không sửa catalog/undo mới.
- **Xong khi:** mỗi ca chạy 30 lần ổn định; kiểm mutation thủ công (bỏ advance-trước-I/O, bỏ restore khi lỗi) làm test fail; không dùng `Task.Delay`/thời gian tường trong assert.

### Nhóm P1 — kiểm chứng thật và ảnh thật

#### TC06 — Recycle Bin thật (Native) — BLOCKED (Q-T4) (L3)
- **Làm:** chỉ với file tạm có manifest: Delete liên tục M ảnh (M≈20) bằng `WindowsRecycleBin` thật; xác nhận từng file rời khỏi thư mục nguồn và có bản trong Recycle Bin; Undo khôi phục đúng bản (kể cả nhiều file cùng tên/kích thước khác timestamp — C01); đo P50/P95/max độ trễ mỗi Delete và **ghi vào báo cáo** (không assert giây). Test tự khôi phục để Recycle Bin không còn rác; nếu fail, manifest liệt kê mục còn lại để dọn tay; không xóa vĩnh viễn bằng code.
- **Category:** `Native`, ngoài gate mặc định; có lệnh chạy riêng ghi trong tài liệu test. Đóng blocker native của OC06/OC13.

#### TC07 — Bộ chạy ảnh thật (Manual) — BLOCKED (Q-T3) (L2)
- **Làm:** thay `RealWorldPhotosManualTest` (thoát sớm = PASS giả) bằng `[FactRequiresEnv("PHOTOREVIEW_FIXTURE_DIR")]` tự đặt `Skip` có lý do rõ khi thiếu (xunit 2.9.3 không có `Assert.Skip`). Khi có biến môi trường: copy N ảnh sang thư mục tạm, chạy lại kịch bản TC03–TC05 tham số hóa theo folder, xuất báo cáo (số đọc, byte, P50/P95 present, số phím bị bỏ) vào thư mục tạm; **không** ghi đường dẫn/tên ảnh vào repo hay log commit.
- **Xong khi:** thiếu env → test hiện `Skipped` (không PASS); có env → báo cáo sinh ra, bản gốc không bị đổi (kiểm băng hash tập nguồn trước/sau).

### Nhóm P2 — dọn dẹp

#### TC08 — Thay thế test tautological (G1) — TODO (L2)
- **Phụ thuộc:** TC05 DONE. Ánh xạ trước khi xóa:

| Test cũ | Thay bằng |
|---|---|
| `InterleavedFileActionSequenceTests.SequenceNextThenMoveKeepsNextImage` | TC05a + TC05c |
| `…SequenceNextThenDeleteKeepsNextImage` | TC05a/c (biến thể Delete) |
| `…SequenceDeleteAtEndSelectsPriorSurvivingSlot` | TC05d |
| `BenchmarkScenarioTests` (Move/Delete/Copy checkpoint + đếm điều hướng) | TC05a/c; Copy-preserves-source → test `FileActionService` copy (kiểm nếu chưa có) |
| `FileActionConcurrencyTests.MoveSucceedsWhileReadIsOpen` / `DeleteSucceedsWhileReadIsOpen` | TC05e, cộng một test hợp đồng mở qua `IFileSystem.OpenReadShared` kiểm cờ share (không dùng `FileStream` thô, bỏ `< 1s`) |
| `FileActionConcurrencyTests.CopyPreservesSourceAndBytesWhileReadIsOpen`, `InterleavedSequenceLeavesDeterministicState` | TC05c / test copy production |
- **Xong khi:** mỗi test cũ có đích ánh xạ đã PASS; xóa file cũ trong commit riêng; `test-parity.md` cập nhật.

#### TC09 — Audit flake/thời gian — TODO (L2)
- **Làm:** phân loại ~50 dòng `Task.Delay`/`WaitAsync`/`TimeSpan` và assert thời gian tường: thay bằng barrier/`TaskCompletionSource`, `IUiScheduler`/clock giả; giữ timeout chỉ làm chốt chặn có thông báo. Ưu tiên theo đường nóng: `PreloadSafetyTests`, `PreviewImageServiceTests`, `MainWindowBehaviorTests.*`, `MainViewModel*Tests`, `StaTestHost`, rồi phần còn lại.
- **Xong khi:** mỗi file đã sửa chạy 30 lần liên tiếp không lỗi; danh sách còn lại có lý do giữ.

#### TC10 — Cấu trúc test (gộp ST10) — TODO (L2)
- **Làm:** gộp hai `OperationJournalTests` (đối chiếu danh sách `[Fact]` trước/sau); chuyển `PerfAnalyzeTests` theo ST05; thay dần `SourcePresenceTests`/`ProjectSources` bằng test hành vi (nhóm chưa có thay thế giữ và ghi lý do); chuẩn hóa tên/thư mục theo `Unit|Integration|HotPath|Stress|Native|Manual`; `tests/README.md` mô tả cách chạy từng nhóm. **Không** gom `GlobalStateCollection` (xunit yêu cầu cùng assembly).
- **Xong khi:** số `[Fact]/[Theory]` không giảm ngoài phần TC08 có ánh xạ; README có lệnh cho từng category.

#### TC11 — Tích hợp gate — TODO (L1)
- **Làm:** `tools/verify-all.ps1` thêm bước `HotPath` (mặc định lặp nhỏ, ví dụ 3 lần) và tùy chọn `-Stress`, `-Native`, `-Manual`; `.github/workflows/ci.yml` chạy `HotPath`; cập nhật `tools/test-verify-gates.ps1` để bước mới lỗi thì gate lỗi (đúng hợp đồng gate hiện có).
- **Xong khi:** gate mới fail khi một test HotPath fail (kiểm bằng lỗi cố ý tạm thời, không commit); tài liệu lệnh khớp script.

## 5. Thứ tự và đụng độ

`TC00 → TC01 → TC02 → (TC03 ∥ TC04) → TC05 → TC08`; `TC06`, `TC07` sau khi TC05 ổn và có quyết định Q-T3/Q-T4; `TC09` chạy dần theo từng file, không chồng với task khác trên cùng file; `TC10`, `TC11` cuối.

- **Đụng owner:** `MainViewModel*Tests`, test Undo/file action đang thuộc phạm vi OC14 và ST07–ST09; TC05 viết mới trong file/lớp riêng (`HotPath/*`) để không đè lên các task đó, và phải rebase khi ST07 đổi constructor (dùng builder của ST07 khi có; trước đó dùng `CreateViewModel` hiện có).
- **Với ST02:** TC03(4) là test hồi quy cho việc bỏ stat lại toàn folder; nếu chạy trước ST02 nó phải **fail có chủ đích** trên baseline (ghi lại làm bằng chứng), rồi PASS sau ST02.
- **Với ST10:** ST10 trong tasks nay là con trỏ tới TC10.

## 6. Kiểm chứng và bàn giao

- Mỗi task: build Release, targeted tests, **30 lần lặp** cho test đường nóng, sau đó `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Stress"` không giảm PASS ngoài phần thay thế có ánh xạ.
- Trước bàn giao: `./tools/verify-all.ps1` và `./tools/test-verify-gates.ps1`; báo cáo TC06/TC07 chỉ khi môi trường chạy được, nếu không ghi blocker cụ thể, không suy diễn.
- Nhánh `codex/test-cleanup-*`; commit riêng cho: fixture, probe, từng nhóm test, xóa test cũ. Không commit thẳng `master`. Rollback bằng revert commit; test không sửa journal/session/cache của người dùng.

## 7. Phạm vi chưa làm

Không đổi hành vi production (kể cả gate `IsBusy`) trừ seam đo ở Q-T2 nếu được duyệt; không đánh giá tốc độ tuyệt đối (P95) như tiêu chí PASS/FAIL trong gate (chỉ báo cáo); không đưa ảnh thật hay đường dẫn cá nhân vào repo; không viết lại toàn bộ `SourcePresenceTests` một lần.
