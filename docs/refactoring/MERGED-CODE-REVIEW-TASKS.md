# Task sửa lỗi sau review code đã merge

- Ngày: **2026-09-18** · [Plan và evidence](MERGED-CODE-REVIEW-PLAN.md).
- Baseline review: `afb2f77` trên `refactor/integration`.
- Branch triển khai: `codex/merged-review-fixes`; đã tích hợp vào `refactor/integration` và được merge vào `master` qua PR #5. Các commit tiếp theo trên `refactor/integration` gồm phần kiểm tra validation và các task T50–T61.
- **Đợt MR01–MR06 đã hoàn tất.** Các hướng dẫn worker và trạng thái TODO bên dưới được giữ như hồ sơ kế hoạch; nhật ký của từng MR đã được cập nhật thành DONE.
- Trạng thái: `TODO`, `IN PROGRESS`, `BLOCKED`, `DONE`. Mỗi worker nhận một task, chỉ sửa Files được giao. Nếu cần thêm file, báo Coordinator để cập nhật phạm vi và xin xác nhận theo AGENTS.

## 0. Phân chia và hợp nhất

| Số | ID | Công việc | Findings | Phụ thuộc | Chạy độc lập | Trạng thái |
|---|---|---|---|---|---|---|
| 1 | MR00 | Review + baseline + tài liệu | F01–F07 | — | — | DONE |
| 2 | MR01 | RAM safety và lifetime preload | F01, F02 | MR00 + duyệt plan | Nhóm A | DONE |
| 3 | MR02 | Gate test/CI không che lỗi | F03 | MR00 + duyệt plan | Nhóm A | DONE |
| 4 | MR03 | ICC WicDirect + quality gate | F04 | MR00 + duyệt plan | Nhóm A | DONE |
| 5 | MR04 | TurboJPEG fallback/metadata | F05, F06 | MR00 + duyệt plan | Nhóm A | DONE |
| 6 | MR05 | Snapshot backend và cache identity | F07 | MR00 + duyệt plan | Nhóm A | DONE |
| 7 | MR06 | Tích hợp/Release/GUI/PR | F01–F07 | MR01–MR05 | Coordinator | DONE (đã merge PR #5; CI remote chưa được ghi lại trong hồ sơ local) |

- Worker dùng branch `codex/mrNN-<slug>` trong worktree riêng. Không sửa file task/handoff, không `git add -A`, không push.
- Mỗi đầu ra: diff trong Files, regression test, lệnh/kết quả, các INV đã kiểm và giới hạn còn lại. Reviewer độc lập đọc diff và tự chạy test; memory/race/native/cache cần reviewer kiểm sâu, không chấp nhận chỉ source-presence.
- Coordinator giữ hai file MR và `task_on_progress.md`; nhận nhật ký worker, hợp nhất MR02 → MR01 → MR03 → MR04 → MR05. Kiểm diff so phạm vi trước mỗi merge. Build/test trên checkout tích hợp chạy tuần tự để tránh khóa output.
- MR03/MR04/MR05 thống nhất sớm: `ActualBackend` biểu thị decoder tạo pixel; yêu cầu decode lấy từ snapshot key, không đọc callback settings lại. Nếu cần đổi interface chung ngoài Files phải cập nhật plan trước.
- Không cần có đủ agent cùng lúc; các lane độc lập có thể chạy nối tiếp. Đợt review MR00 thực hiện tại task hiện tại, không có worker triển khai fix.

## 1. MR00 — DONE — Baseline review

- **Files:** hai file `MERGED-CODE-REVIEW-*.md`, link trong `REFACTOR-TASKS.md`, cập nhật ngắn `task_on_progress.md`.
- **Đầu ra:** 7 findings kèm trigger, evidence và tác động; local probe và kết quả test/publish ghi trong plan.
- **Nhật ký:** 2026-09-18 · review `afb2f77`; 615 passed + 1 skipped; CLI/publish PASS. Không sửa production source/config. Người dùng yêu cầu commit/push tài liệu để chia sẻ ở lượt tiếp theo; branch `codex/merged-code-review-plan`.

## 2. MR01 — DONE — Khôi phục kiểm soát RAM và shutdown preload

- **Owner đề xuất:** Agent A; review độc lập sâu về concurrency. **Phụ thuộc:** MR00 + duyệt plan.
- **Files:**
  - `src/PhotoReview.Imaging/Preload/PreloadScheduler.cs`
  - `src/PhotoReview.App/App.xaml.cs`
  - `src/PhotoReview.App/MainWindow.xaml.cs` (chỉ construction/closing preload)
  - `src/PhotoReview.App/BenchmarkImageExecutor.cs` (chỉ truyền memory probe và teardown preload; giữ override test)
  - `tests/PhotoReview.Imaging.Tests/PreloadSafetyTests.cs` (mới)
  - `tests/PhotoReview.App.Tests/CompositionRootTests.cs`
- **Làm:**
  1. Thêm test inject probe thiếu RAM: không bắt đầu decode mới; đủ RAM: tiếp tục preload nhiều batch; test DI xác nhận probe thật được truyền.
  2. Cả app DI, constructor tương thích và benchmark khi không có override phải truyền memory probe thật; bỏ mặc định giả ở đường production. Giữ override test có chủ đích. Không kéo dependency Platform.Windows vào Imaging.
  3. Thiết kế cancel/dispose idempotent: cancel token kể cả lúc chuyển disposed; theo dõi task chạy; chỉ dispose semaphore khi không còn waiter/worker sử dụng. Quan sát task bị lỗi cả khi scheduler thoát sớm.
  4. Test đóng/dispose khi worker đang giữ slot, đang chờ slot, vừa cancel/restart; không enqueue sau dispose, không lỗi Release sau dispose. Giữ INV-2 và khả năng join preview hữu ích khi chỉ đổi hướng điều hướng.
- **Không làm:** tăng budget RAM, đổi thứ tự preload, full MVVM, refactor MainWindow ngoài construction/closing, sửa lỗi khác tình cờ gặp.
- **Kiểm thử:** focused Imaging `PreloadSafetyTests` + App `CompositionRootTests`, Tests.Unit filter `PreloadSchedulerTests|DiagOverrideTests`; race chạy 10 lần. GUI close-during-preload kiểm tại MR06.
- **Done khi:** RAM guard lấy dữ liệu thật; dispose cancels và không giải phóng primitive đang dùng; test CLI/no Dispatcher vẫn đạt.
- **Rủi ro/rollback:** UI deadlock nếu blocking drain; revert riêng MR01 khi cần, không giữ nửa thay đổi giữa constructor và scheduler.
- **Đầu ra:** patch + lifecycle giải thích ngắn + test evidence. **Nhật ký:** 2026-09-19 · `210523e`, `a1e7f52`, `613af50` · inject `IMemoryProbe` thật, cancel/drain/dispose an toàn, nối scheduler thật vào viewer, tránh UI Dispatcher yield khi shutdown; `PreloadSafetyTests` 4/4, `CompositionRootTests` 6/6, VERIFY Release đạt.

## 3. MR02 — DONE — Bảo đảm lỗi test không bị che

- **Owner đề xuất:** Agent B. **Phụ thuộc:** MR00 + duyệt plan.
- **Files:** `.github/workflows/ci.yml`, `tools/verify-all.ps1`, `tools/test-verify-gates.ps1` (mới).
- **Làm:**
  1. Mỗi lệnh native test phải được kiểm exit code ngay, hoặc đổi thành một invocation tổng có exit code tổng. Không chỉ kiểm lệnh cuối của nhiều suite.
  2. CI phải chạy App.Tests; đồng bộ danh sách sáu test project và filter Manual giữa CI/VERIFY. Giữ upload TRX khi fail.
  3. Viết kiểm tra script bằng stub/fault injection: suite đầu/giữa/cuối fail đều trả nonzero; toàn pass trả zero; test không sửa source thật để gây fail.
  4. Khi chạy kiểm tra script, stub cả phần publish/smoke liên quan để tránh phụ thuộc ảnh hoặc ghi thư mục publish ngoài ý muốn.
- **Không làm:** đổi version SDK/dependency, bỏ analyzer, nới test, thay lịch trigger ngoài nhu cầu chạy gate.
- **Kiểm thử:** `tools/test-verify-gates.ps1`; sau hợp nhất chạy VERIFY thật. CI remote sẽ xác nhận ở PR MR06, không ghi CI xanh từ test local.
- **Done khi:** mọi failure đã inject được bắt; App.Tests xuất hiện trong CI và có kết quả test.
- **Rủi ro/rollback:** khác biệt Windows PowerShell/pwsh; kiểm shell dùng thực tế. Revert riêng gate commit, không thay test để làm xanh.
- **Đầu ra:** patch + ma trận fault injection. **Nhật ký:** 2026-09-19 · `a32fd32`, `599a2e4` · kiểm exit code từng gate, thêm App.Tests vào CI, test gate đầu/giữa/cuối và native stub `cmd /c exit 17`; `tools/test-verify-gates.ps1` PASS.

## 4. MR03 — DONE — ICC của WicDirect và bằng chứng chất lượng

- **Owner đề xuất:** Agent C; reviewer native/image quality. **Phụ thuộc:** MR00 + duyệt plan.
- **Files:**
  - `src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs`
  - `src/PhotoReview.Imaging/Decoding/Wic/WicInterop.cs`
  - `tests/PhotoReview.Imaging.Tests/Quality/DecoderQualityGateTests.cs`
  - `tests/PhotoReview.Imaging.Tests/Quality/WicColorProfileTests.cs` (mới)
  - `tests/PhotoReview.Imaging.Tests/Fixtures/FixtureGenerator.cs`
  - `tests/Fixtures/ColorProfiles/*` (fixture nhỏ, license được phép, không ảnh người dùng)
  - `tests/Fixtures/README.md`, `docs/adr/0001-image-decoder.md`
- **Làm:**
  1. Tạo fixture ICC có tính xác định với pixel khác biệt giữa sRGB và profile rộng; kiểm fixture thực sự chứa profile. Thiếu profile phải báo fail/blocked, không return PASS.
  2. Ưu tiên sửa nhỏ: WicDirect phát hiện ICC chưa hỗ trợ và ném lỗi typed để factory fallback WPF. Chỉ làm native color transform nếu chứng minh được chất lượng trong cùng phạm vi.
  3. Test factory và direct decoder: expected fallback hoặc pixel tương đương WPF, actual backend đúng; full-res/downscale, orientation, alpha nếu định dạng hỗ trợ. Giữ FileShare.ReadWrite|Delete và giải phóng COM.
  4. Sửa assertion ICC của quality gate; cập nhật ADR phân biệt capability có thật với lựa chọn tương lai. Không giữ tuyên bố AdobeRGB khi chỉ có test sRGB/non-null.
- **Không làm:** bật backend mặc định, tự tải ảnh thật/profile không rõ license, thay ngưỡng chất lượng để test xanh.
- **Kiểm thử:** Imaging filter `WicColorProfileTests|DecoderQualityGateTests|WicDirectTests|FactoryTests`; lặp native quality 10 lần. Ghi profile, kích thước, actual backend, PSNR/DeltaE nếu có transform.
- **Done khi:** ảnh ICC đúng màu hoặc fallback có kiểm chứng; không có silent pass do thiếu fixture; ADR khớp code/test.
- **Rủi ro/rollback:** fallback tăng thời gian decode; native COM phải giải phóng đầy đủ. Revert backend change và tuyên bố ADR phụ thuộc cùng nhau.
- **Đầu ra:** patch + fixture provenance + bảng kết quả. **Nhật ký:** 2026-09-19 · `48a52ce` · WicDirect nhận diện ICC và fallback WPF typed; fixture Display P3 cố định kèm SHA/license; quality gate không còn silent pass; focused 55/55, ICC subset lặp 10/10.

## 5. MR04 — DONE — Contract lỗi và metadata decoder

- **Owner đề xuất:** Agent D; reviewer INV-8/12. **Phụ thuộc:** MR00 + duyệt plan.
- **Files:**
  - `src/PhotoReview.Imaging.TurboJpeg/TurboJpegDecoder.cs`
  - `src/PhotoReview.Imaging/Decoding/FallbackImageDecoder.cs` (chỉ nếu cần chỉnh contract typed)
  - `src/PhotoReview.Imaging/Decoding/WpfBitmapImageDecoder.cs` (orientation metadata)
  - `tests/PhotoReview.Imaging.Tests/Decoding/DecoderContractRegressionTests.cs` (mới)
  - `tests/PhotoReview.Tests/DecoderBenchmark.cs` (chỉ nếu test cho thấy cần sửa reporting)
- **Làm:**
  1. Test JPEG header/scan lỗi qua Turbo được thử fallback đúng một lần; dùng spy để kiểm invocation và dùng WPF thật để kiểm kết quả/exception cuối.
  2. Chuẩn hóa native failure thành exception dữ liệu/codec thích hợp. Missing path, cancellation, OOM không fallback; không thêm catch-all InvalidOperationException che lỗi lập trình.
  3. Turbo direct trả `ActualBackend=TurboJpeg`; metadata orientation WPF/Turbo đúng với contract. Wrapper không làm sai backend thực nếu có nhiều tầng fallback.
  4. Chạy `--decoder-bench` trên fixture JPEG nhỏ tự tạo; direct Turbo success phải có backend đúng và FallbackCount=0. Không dùng báo cáo cũ để chứng minh fix.
- **Không làm:** đổi DCT algorithm, đổi native library version, mở rộng hỗ trợ format hoặc benchmark performance tổng thể.
- **Kiểm thử:** Imaging filter `DecoderContractRegressionTests|TurboJpegTests|FactoryTests|OrientationTests`; benchmark smoke sau build CLI, kiểm CSV/JSON.
- **Done khi:** tái hiện F05/F06 trước fix và test sau fix đạt; phân biệt rõ fallback đã được thử với ảnh cuối cùng có decode được hay không.
- **Rủi ro/rollback:** đổi exception làm caller xử lý khác; giữ tests negative-path. Revert MR04 độc lập, hoãn đánh giá số liệu backend phụ thuộc.
- **Đầu ra:** patch + log regression/benchmark. **Nhật ký:** 2026-09-19 · `f22116c` · chuẩn hóa lỗi dữ liệu TurboJPEG, strict stop-on-warning, backend/orientation metadata và fallback đúng một lần; focused 79/79, benchmark smoke Turbo direct không fallback.

## 6. MR05 — DONE — Decode/cache giữ đúng identity

- **Owner đề xuất:** Agent E; reviewer concurrency/INV-1/2/12. **Phụ thuộc:** MR00 + duyệt plan; chạy độc lập bằng fake factory.
- **Files:**
  - `src/PhotoReview.Imaging/Caching/PreviewImageService.cs`
  - `src/PhotoReview.Imaging/ImageCacheKey.cs` (chỉ nếu cần version/key schema)
  - `tests/PhotoReview.Imaging.Tests/Caching/PreviewBackendIdentityTests.cs` (mới)
- **Làm:**
  1. Test chặn decode bằng barrier, đổi current backend sau khi tạo key: decoder phải dùng backend trong key, không đọc callback lại ở worker.
  2. Áp dụng tương tự cho ReadInfo/original dimensions, orientation và request metadata cần snapshot; bảo toàn source fingerprint/epoch.
  3. Định nghĩa fallback cache: không ghi pixel WPF thành pixel native thành công; có thể không persist fallback dưới key native. Cache hit phải có actual metadata đúng. Không dùng requested backend suy ra actual fallback.
  4. Disk cache không có metadata cần thiết phải trở thành miss/version cũ; không xóa toàn bộ cache thật. Test cold decode/RAM hit/disk hit/fallback/đổi backend giữa flight.
- **Không làm:** nguồn byte RAM T65, đổi quota, thêm I/O kiểm source dư thừa hoặc thêm retry file bị Move/Delete.
- **Kiểm thử:** Imaging `PreviewBackendIdentityTests`; Tests.Unit `PreviewImageServiceTests|PreviewImageServiceDiskCacheTests|FileActionConcurrencyTests`; barrier tests chạy 10 lần.
- **Done khi:** không thể nhận `key=Wpf` mà cache lưu pixel WicDirect do callback thay đổi; fallback/disk metadata và INV-1/2/12 được chứng minh.
- **Rủi ro/rollback:** đổi key làm cache miss lần đầu; revert MR05 và giữ cache namespace tách biệt. Không xóa dữ liệu người dùng.
- **Đầu ra:** patch + bảng source/RAM/disk/fallback + race evidence. **Nhật ký:** 2026-09-19 · `a5fe746`, `613af50`, `50c2650` · snapshot backend theo key, cache v3 với metadata backend/orientation, thiếu metadata thành miss, fallback WPF không ghi dưới key native; focused 5/5, barrier lặp 10/10.

## 7. MR06 — DONE — Tích hợp, kiểm chứng và bàn giao

- **Owner:** Coordinator. **Phụ thuộc:** MR01–MR05 DONE sau review.
- **Files:** hai file MR, `docs/refactoring/REFACTOR-TASKS.md`, `task_on_progress.md`, `docs/refactoring/results/merged-code-review-validation.md` (mới), `README.md` chỉ nếu quy trình thực sự đổi.
- **Làm:**
  1. Hợp nhất các patch đã review theo thứ tự mục 0; kiểm diff không có source ngoài Files; ghi SHA và kết quả từng task.
  2. Chạy tuần tự build Release, test solution (Category!=Manual), CLI và `tools/verify-all.ps1` đã sửa. Publish đúng thư mục AGENTS và verify-release. Đọc exit code thật và tổng số test; tách skip có sẵn khỏi pass.
  3. GUI có kiểm soát: mở folder ảnh thử, preload nhiều batch, đổi folder, đóng khi decode đang chờ; kiểm RAM guard bằng injection/probe thay vì cố làm máy hết RAM. Không gửi input OS/clipboard; dùng driver in-process đã có hoặc người dùng thao tác.
  4. Chạy benchmark nhỏ cho backend/fallback và ICC; ghi thời gian/chất lượng theo điều kiện đo, không suy ra thứ hạng toàn bộ từ smoke test.
  5. Cập nhật handoff ngắn; ghi T87 chỉ có thể tiếp tục sau gate sửa lỗi. Sửa ghi chú lỗi Dispatcher.Yield cũ nếu evidence hiện tại cho thấy nó đã được xử lý, không coi nó vẫn tồn tại chỉ từ lịch sử.
  6. Sau xác nhận plan và triển khai: commit có chọn file, push feature branch lên origin, mở PR theo AGENTS (không commit/push master). Chỉ merge master sau CI xanh và người dùng duyệt; Coordinator điều phối việc tích hợp vào refactor/integration.
- **Không làm:** tự merge PR, bắt đầu D07 toàn ma trận hoặc xóa cache thật/reboot; cần chỉ định riêng cho các thao tác đó.
- **Done khi:** gate local đủ, runtime evidence hoặc giới hạn rõ, push/PR xác nhận, CI được đọc thật; mọi F có trạng thái resolved/blocked cụ thể.
- **Rủi ro/rollback:** nếu có conflict vượt phạm vi, BLOCKED và cập nhật plan; revert commit task theo phụ thuộc, không reset workspace.
- **Đầu ra:** [validation report](results/merged-code-review-validation.md) + PR #5 + handoff. **Nhật ký:** 2026-09-19 · hợp nhất tuần tự trên `refactor/integration`; VERIFY Release, CLI, smoke, fault injection, publish và verify-release đạt. Chưa có bằng chứng GUI thủ công, benchmark backend/ICC hoặc CI remote trong hồ sơ local.
