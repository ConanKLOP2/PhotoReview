# Review code đã merge — plan sửa lỗi

- Ngày: **2026-09-18**.
- Baseline: `refactor/integration` tại `afb2f773397b3598193de843c5b0442994b0f4ba` (T40 đã merge). `master` local tại `86282cd`; không đồng nhất hai nhánh. Không fetch remote trong đợt review này.
- Trạng thái: **Review DONE; đề xuất sửa lỗi chờ người dùng xác nhận**. Người dùng đã yêu cầu commit/push tài liệu để chia sẻ cho agent/máy khác; chưa cho triển khai source/config.
- Branch chia sẻ: `codex/merged-code-review-plan`, dựa trên baseline tích hợp ở trên. Máy khác chạy `git fetch origin`, rồi `git switch --track origin/codex/merged-code-review-plan` nếu chưa có branch local. Probe/log trong `work/` không được push; bằng chứng tóm tắt và task đầy đủ nằm trong hai file MR.
- Task thực thi: [MERGED-CODE-REVIEW-TASKS.md](MERGED-CODE-REVIEW-TASKS.md).
- Kế hoạch nền: [REFACTOR-PLAN.md](REFACTOR-PLAN.md), [REFACTOR-TASKS.md](REFACTOR-TASKS.md), [PERF-DIAGNOSIS-PLAN.md](PERF-DIAGNOSIS-PLAN.md), [PERF-DIAGNOSIS-TASKS.md](PERF-DIAGNOSIS-TASKS.md).

## 1. Kết luận và phạm vi

**Chưa nên coi bản tích hợp đã ổn để phát hành hoặc bật backend mới mặc định.** Test hiện tại xanh, nhưng còn lỗi safety của preload, độ tin cậy của cổng kiểm tra và hợp đồng decoder/cache. Nên sửa MR01–MR02 trước khi đo D07; hoàn tất MR03–MR06 trước khi thực thi T87/ra quyết định mặc định cuối cùng.

Review tập trung vào code đã hợp nhất: composition root T40, Imaging/preload/cache W3, C3 T80–T86, test và CI. Đã đọc thêm luồng MainWindow, settings, journal/recovery để đối chiếu đường gọi. Đây là review theo rủi ro, không phải chứng nhận mọi dòng code hoặc full GUI acceptance. Không xem các phần W4/W5 còn TODO là lỗi vì chưa triển khai.

App hiện vẫn dùng `PreviewStateContext.CurrentBackend = Wpf`; việc ADR chọn WicDirect chưa đồng nghĩa app đã dùng nó. Các lỗi backend dưới đây tác động trực tiếp khi gọi backend/benchmark và là điều kiện phải xử lý trước T87.

## 2. Findings

### F01 — P1: App bỏ qua kiểm soát áp lực RAM của preload

- Bằng chứng: `src/PhotoReview.Imaging/Preload/PreloadScheduler.cs:60,287–293`; `src/PhotoReview.App/App.xaml.cs:85–93`; `src/PhotoReview.App/MainWindow.xaml.cs:154–158`.
- Cả DI factory và constructor tương thích không truyền probe thật/`hasHeadroom`. Scheduler rơi vào `FakeOrSystemMemoryProbe`, luôn trả `true` và số RAM giả 16 GB. Đăng ký `IMemoryProbe` trong DI không có tác dụng nếu không truyền vào scheduler.
- Khi RAM hệ thống gần đầy, preload vẫn tiếp tục decode/giữ cache; ngưỡng 80% và reserve 2 GB không bảo vệ được hệ thống. Không khẳng định đã quan sát OOM trên máy này.
- Probe runtime bằng assembly Release: `HasHeadroom(maximumLoad=0,reserve=long.MaxValue)=True`.
- Sửa: inject probe thật vào mọi đường production; fake chỉ được dùng khi test chủ động truyền vào. Giữ khả năng preload CLI không có Dispatcher.

### F02 — P1: Dispose không hủy preload và giải phóng semaphore quá sớm

- Bằng chứng: `PreloadScheduler.cs:107–110,278–284`; worker `finally` tại `273–275` vẫn gọi `_preloadSlots.Release()`.
- `Dispose` đặt `_disposed=true`, sau đó gọi `Cancel`; `Cancel` lập tức return. CTS và semaphore bị dispose trong khi scheduler/worker có thể còn hoạt động.
- Trigger: đóng cửa sổ hoặc kết thúc một instance khi đang preload. Có thể phát sinh `ObjectDisposedException`, công việc nền tiếp tục và task lỗi không được quan sát đầy đủ.
- Probe runtime giữ token trước khi dispose: `TokenCancelledAfterDispose=False`.
- Sửa: cancellation phải xảy ra trước khi giải phóng tài nguyên; drain/observe worker rồi mới dispose semaphore. Không chặn UI chờ task cần UI.

### F03 — P1: Cổng test có thể báo xanh dù suite trước thất bại

- Bằng chứng: `tools/verify-all.ps1:33–36,43–50`; `.github/workflows/ci.yml:44–50`.
- Local VERIFY chỉ kiểm `$LASTEXITCODE` sau cả block sáu lệnh `dotnet test`. Một lệnh ở giữa fail, lệnh cuối pass sẽ ghi đè exit code. CI cũng đặt nhiều lệnh native trong một step mà không kiểm mã thoát từng lệnh; không nên phụ thuộc cấu hình shell để suy ra fail-fast.
- CI còn thiếu `tests/PhotoReview.App.Tests/PhotoReview.App.Tests.csproj` (bốn test composition root hiện có).
- Sửa: fail từng project/step hoặc dùng một lệnh test solution trả lỗi tổng; luôn gồm App.Tests và giữ filter Manual phù hợp. Chứng minh bằng fault injection ở suite đầu/giữa/cuối.
- Mức bằng chứng: xác định từ cấu trúc script, chưa tạo một CI run cố ý thất bại trên GitHub.

### F04 — P1: WicDirect thiếu xử lý ICC nhưng quality gate vẫn có thể đạt

- Bằng chứng: `src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs:149–196`; `tests/PhotoReview.Imaging.Tests/Quality/DecoderQualityGateTests.cs:108–141`; `docs/adr/0001-image-decoder.md:84`.
- Pipeline chỉ format-convert sang BGRA rồi tạo BitmapSource; không đọc/apply color context qua color transform, cũng không từ chối ảnh ICC để fallback. Format conversion không thay thế việc chuyển không gian màu.
- Test ICC có thể return thành PASS khi fixture không có ICC; khi có ICC chỉ kiểm WicDirect trả non-null, không so màu. ADR lại khẳng định hỗ trợ AdobeRGB và fidelity màu.
- Ảnh có profile khác sRGB là trường hợp cần sửa/chứng minh trước khi bật WicDirect. Đợt này chưa đo sai lệch pixel trên fixture AdobeRGB/Display P3; không gán số PSNR/DeltaE chưa đo.
- Sửa tối thiểu: phát hiện profile và fallback WPF an toàn; hoặc triển khai color transform đầy đủ. Thêm fixture ICC có tính xác định và assertion pixel; thiếu fixture phải fail/blocked rõ ràng, không silently pass. Hiệu chỉnh ADR bằng bằng chứng mới.

### F05 — P2: Lỗi decode TurboJPEG không đi vào fallback

- Bằng chứng: `src/PhotoReview.Imaging.TurboJpeg/TurboJpegDecoder.cs:60,68,119,193`; `src/PhotoReview.Imaging/Decoding/FallbackImageDecoder.cs:81–94`.
- Native header/decompress lỗi được chuyển thành `InvalidOperationException`, trong khi wrapper không bắt loại này. Trigger: JPEG cắt cụt/codec gặp lỗi; wrapper bỏ qua lần thử WPF theo INV-12.
- Probe với JPEG tự tạo, chỉ giữ 20 byte: `TruncatedTurboException=InvalidOperationException; FallbackCalls=0`.
- Sửa: chuẩn hóa lỗi dữ liệu/native decode thành kiểu thuộc contract fallback. Không bắt rộng mọi exception và không retry missing file, cancellation hoặc OOM. WPF cũng có thể từ chối ảnh lỗi; yêu cầu là thử fallback đúng chính sách, không hứa ảnh hỏng luôn xem được.

### F06 — P2: TurboJPEG báo ActualBackend=Wpf, làm sai telemetry benchmark

- Bằng chứng: `TurboJpegDecoder.cs:141`; `src/PhotoReview.Imaging/Decoding/WpfDecodedImage.cs:21`; `tests/PhotoReview.Tests/DecoderBenchmark.cs:228,277`.
- TurboJPEG tạo `WpfDecodedImage` mà không truyền `actualBackend`; giá trị mặc định là Wpf. Benchmark gọi decoder trực tiếp, nên mọi lần Turbo decode thành công bị đếm thành fallback dù không gọi WPF.
- Probe trên JPEG tự tạo: `DirectTurboActualBackend=Wpf`.
- Sửa: trả metadata backend đúng từ decoder trực tiếp; kiểm orientation metadata của WPF/Turbo cùng contract. Chạy lại một benchmark nhỏ kiểm CSV/JSON. Thời gian cũ không tự động vô giá trị, nhưng các cột backend/fallback phải được coi là chưa tin cậy.

### F07 — P2: Backend của request không được cố định theo cache key

- Bằng chứng: `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:238–254,345–367`.
- Key chụp backend lúc request được tạo, nhưng `DecodeFromSource` lại gọi factory theo `_currentBackend()` khi worker chạy. Backend đổi giữa hai thời điểm sẽ decode B rồi lưu dưới key A.
- Probe với factory có gắn backend, đổi callback sau khi tạo key: `CacheKeyBackend=Wpf; DecodedBackend=WicDirect; StoredUnderOldKey=True`.
- Đây là lỗi service có thể tái hiện; đường settings đổi backend trong GUI chưa được kích hoạt vì T87 chưa làm.
- Sửa: snapshot backend/orientation từ key xuyên suốt decode; quy định rõ actual fallback trong RAM/disk cache theo INV-1/12. Disk hit hiện tạo `WpfDecodedImage` với metadata mặc định (`:225`), nên cần kiểm backend/metadata khi đọc cache, không suy actual từ backend được yêu cầu nếu đã fallback.

## 3. Kiểm tra đã chạy

| Kiểm tra | Kết quả hiện tại |
|---|---|
| `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` | PASS: 615 passed, 1 skipped, 0 failed |
| Chi tiết | Core 199; Imaging 143; Architecture 5 + 1 skip; Integration 8; App.Tests 4; Tests.Unit 256 |
| `dotnet run --project tests/PhotoReview.Tests -c Release --no-build` | PASS toàn bộ CLI contract |
| Publish Release framework-dependent | PASS vào `src/PhotoReview.App/bin/Release/net10.0-windows/publish` |
| `tools/verify-release.ps1 -ReleaseDirectory src/PhotoReview.App/bin/Release/net10.0-windows/publish` | PASS; version 1.0.1.0, đủ artifact |
| Probe độc lập dùng assembly Release | Xác nhận F01, F02, F05, F06, F07 |

Log/probe chỉ có local, nằm trong `work/review-20260918/`; log solution test ở `work/review-20260918-tests.log`. Probe không nằm trong solution và không phải regression test đã merge. Build vẫn có analyzer warnings. Không chạy đầy đủ `verify-all.ps1`, GUI acceptance, D07 hay CI remote trong lượt này. Không tác động ảnh thật/config thật/clipboard hoặc phát input OS.

Một nghi vấn về thread affinity preload đã được kiểm tra bằng probe WPF nhỏ: 19 lượt preload hoàn tất, không báo lỗi. Không đưa nghi vấn đó thành finding xác nhận; GUI dài hạn vẫn cần được kiểm ở MR06.

## 4. Mục tiêu, thứ tự và cách chia việc

1. **MR00 DONE** — baseline review, bằng chứng và hai file plan/task.
2. **MR01 TODO** — sửa preload memory/lifecycle (F01/F02); cùng một owner vì chung scheduler.
3. **MR02 TODO** — sửa VERIFY/CI (F03); độc lập MR01.
4. **MR03 TODO** — bảo toàn màu ICC và sửa quality gate/ADR (F04).
5. **MR04 TODO** — sửa contract TurboJPEG và metadata (F05/F06).
6. **MR05 TODO** — cố định decode identity và cache metadata (F07).
7. **MR06 TODO** — tích hợp, kiểm thử lại, xác nhận GUI và đồng bộ tài liệu.

MR01–MR05 có thể giao agent độc lập sau khi plan được duyệt, với Files/đầu ra riêng trong file task. MR03/MR04/MR05 dùng test file mới khác nhau để tránh conflict. Coordinator hợp nhất theo MR02 → MR01 → MR03 → MR04 → MR05, chạy gate sau mỗi đợt hợp nhất, rồi MR06. Worker không sửa task/handoff và không push.

Không đổi thuật toán preload, target chất lượng, UI, format config hay làm các task W4 khác trong đợt fix. Không bật WicDirect mặc định hoặc tích hợp Turbo vào app trong đợt này; thuộc T87 sau khi đủ bằng chứng.

## 5. Tiêu chí hoàn thành, rủi ro và rollback

- F01–F07 có regression test hành vi: trước fix thất bại, sau fix đạt; race dùng barrier/TCS và chạy lặp 10 lần, không dựa vào sleep mong may mắn.
- Native quality được kiểm trên ảnh tự tạo/fixture được phép chia sẻ; chứng minh actual backend và màu, không chỉ non-null/source-presence.
- Mỗi suite fail phải làm VERIFY/CI fail; sáu project test đều được chạy, không thêm skip/filter để giấu lỗi.
- Test Release, CLI, publish mặc định, release verification và các gate bắt buộc đạt; GUI memory/close-during-preload có bằng chứng riêng. Mọi giới hạn chưa kiểm được phải ghi BLOCKED/partial.
- MR01 có nguy cơ deadlock UI khi drain; dùng lifecycle async hoặc owner giải phóng tài nguyên sau worker, không `.Wait()` trên Dispatcher. MR03 fallback ICC có thể tăng độ trễ nhưng giữ đúng màu. MR05 thay metadata/key disk phải xử lý cache cũ bằng miss/version, không xóa cache thật khi chưa có yêu cầu.
- Mỗi task là commit nhỏ trên feature branch/worktree từ baseline mới nhất. Rollback bằng revert commit của task và commit tích hợp phụ thuộc tương ứng, không reset/clean workspace, không sửa dữ liệu người dùng.
- Người dùng đã cho phép commit/push tài liệu trong lượt bàn giao tiếp theo. PR tài liệu dùng branch chia sẻ ở trên; khi mở vào master cần ghi rõ branch chứa baseline refactor/integration chưa có trên master. Không tự merge. Việc sửa code theo plan vẫn chờ xác nhận riêng.
