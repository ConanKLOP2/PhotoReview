# Plan giảm token khi agent đọc codebase (DT00–DT10)

- Ngày: 2026-09-20. Baseline: `master` `5dc5cda` + nhánh docs `codex/structure-optimize-plan`.
- Trạng thái: khảo sát `DONE` (đo bằng `wc`/`grep`); **chưa sửa doc/code nào theo plan này**. "Token" ở đây ước lượng thô **byte ÷ 3** (tiếng Việt tốn nhiều token/byte hơn tiếng Anh); chưa đo bằng tokenizer thật, DT00 sẽ chốt cách đo.
- Nguyên tắc: **không mất thông tin** — lịch sử vẫn nằm trong git và `docs/archive/`; chỉ đổi thứ agent *phải đọc mặc định*. Không đổi hành vi code. Comment giữ nguyên nếu nêu bất biến/race/hợp đồng/native ownership.

## 1. Đo được gì (bằng chứng)

| # | Số liệu | Ý nghĩa |
|---|---|---|
| M1 | Docs tracked: **564 KB** (~190k token) — bằng cỡ toàn bộ `src` (621 KB). Test 620 KB, tools 153 KB. | Docs không nhỏ hơn code; đọc hết là không khả thi. |
| M2 | 5 file chiếm ~59% (335 KB): `REFACTOR-TASKS.md` **172 KB** (88 mục, 83 DONE, 1 IN PROGRESS trong bảng trạng thái), `test-parity.md` 56 KB (ma trận migrate CLI→xUnit, đã xong ở T47), `OPTIMIZE-CLEAN-PLAN` 45 KB, `PERF-DIAGNOSIS-TASKS` 32 KB (10 DONE, 2 BLOCKED, 2 TODO), `REFACTOR-PLAN` 30 KB. | Phần lớn là **lịch sử đã hoàn thành** nằm ở thư mục làm việc `docs/refactoring/`. |
| M3 | Khi làm theo hướng dẫn hiện tại (`AGENTS.md` → `task_on_progress.md` → "plan mới + T89"): 1.8 + 8.0 + OC 45 + ST 30 + TC 23 + T89 18 ≈ **126 KB (~42k token) trước khi làm việc**. | Các plan mới của phiên này (ST 30 KB, TC 23 KB) cũng thuộc diện phải nén. |
| M4 | `task_on_progress.md` 8 KB, 41 dòng nhưng có dòng dài 690–890 ký tự; **122 commit** đã chạm file này; chứa danh sách 22 SHA và nhật ký validation tích lũy. | Thiết kế là "trạng thái hiện hành" nhưng đang thành nhật ký. |
| M5 | Không có `docs/INDEX`, không `CLAUDE.md`; agent phải đoán doc nào liên quan. `archive/`, `results/`, `diagnosis/` (~80 KB) nằm lẫn trong cây docs, `Grep`/`Glob` vẫn trả về. Link còn trỏ tới file/khái niệm đã đổi (ví dụ `README` → T66; 4 doc nhắc `PhotoReview.Tests/Program.cs`, `PhotoReview.Tests.Unit`). | Tốn token vì tìm kiếm và đọc nhầm doc cũ. |
| M6 | Comment code: src **88 KB** (~1 370 dòng: 966 `///` + 402 `//`; 14% byte của src), tests 47 KB, tools 10 KB. **65** comment ở src và **32** ở tests/tools còn mã lịch sử (`T14a`, `D05`, `K-2`, `WC3`…). 103 `<summary>` một dòng (cần audit, nhiều cái chỉ diễn lại tên). Không có code bị comment-out. | Comment **không phải** nguồn tốn token chính; lợi ích nhỏ nhưng làm được an toàn và cải thiện chất lượng. |
| M7 | Test lặp helper: `FakeExplorerOrderProvider` ×6, `FakeAppPaths` ×6, `FakeClock` ×5, `TestPresentationSink` ×4, `TestPreloadController` ×4, `FakeRecycleBin` ×4; **7 file** chép tay PNG 1×1 dù `TestImages.PreviewPng` đã có. | Agent đọc test để hiểu hành vi và gặp cùng đoạn boilerplate nhiều lần. |
| M8 | Không có "bản đồ code": muốn biết việc X nằm ở đâu phải đọc `architecture.md` + explore. | Chi phí khám phá lặp lại mỗi phiên. |

**Kết luận:** đòn bẩy lớn nhất là (1) đường vào ngắn và có phân tầng, (2) lưu trữ lịch sử đã hoàn thành ra khỏi vùng làm việc, (3) nén plan/task đang mở; sau đó mới đến bản đồ code, comment và test boilerplate.

## 2. Mô hình phân tầng đọc

| Tầng | Nội dung | Ngân sách | Khi nào đọc |
|---|---|---|---|
| **T0** (luôn đọc) | `AGENTS.md`, `task_on_progress.md`, `docs/INDEX.md` | ≤ 12 KB tổng | Đầu mỗi phiên |
| **T1** (theo việc) | `architecture.md` (+ bản đồ code), `APP-MECHANISMS-VI.md`, **một** plan đang mở, ADR liên quan (đọc phần đầu) | mỗi file ≤ 8–15 KB | Khi task đụng vùng tương ứng |
| **T2** (lưu trữ) | `docs/archive/**` | không giới hạn | **Không đọc** trừ khi được yêu cầu hoặc tra nguồn gốc quyết định |

Mục tiêu sau khi xong: cold start điển hình (T0 + một plan mở) ≲ **25 KB (~8k token)**, giảm ~80% so với M3. Đây là **mục tiêu**, chưa đạt.

## 3. Quyết định cần chốt

| Q | Câu hỏi | Mặc định đề xuất |
|---|---|---|
| Q-D1 | `AGENTS.md` là "quy tắc bắt buộc": cho phép thêm quy tắc "tầng đọc + giới hạn kích thước `task_on_progress.md` + mẫu task gọn"? | Có, thêm ≤ 6 dòng; cần bạn duyệt vì đổi file luật. |
| Q-D2 | Lưu trữ bằng `git mv` sang `docs/archive/` (giữ nội dung) hay xóa hẳn bản đã hoàn thành? | `git mv` + digest ngắn; xóa để sau, git vẫn giữ lịch sử. |
| Q-D3 | Thêm `CLAUDE.md` một dòng nhập `AGENTS.md` (nếu dùng Claude Code) để không phải nhắc đọc thủ công? | Có (nếu bạn dùng Claude Code cho repo này); không tạo bản sao nội dung. |
| Q-D4 | Thêm `.ignore` để `Grep`/`Glob` (ripgrep) bỏ qua `docs/archive/`? | Có, sau khi kiểm chứng công cụ tôn trọng `.ignore`; agent vẫn `Read` tường minh được. |

## 4. Task

Mức: L1 cơ học, L2 vừa. Mọi task doc/comment là **thay đổi không hành vi**; coordinator đọc diff. Nhánh `codex/docs-diet-<ID>`, commit riêng từng task.

### DT00 — Đo baseline và ngân sách — TODO (L1)
- **Làm:** `tools/docs-budget.ps1`: in bảng byte/dòng theo file doc + ước lượng token (byte÷3, ghi rõ là ước lượng) + so với ngân sách tầng (mục 2); chế độ `-Check` exit ≠ 0 khi T0/T1 vượt ngân sách. Chụp bảng baseline vào nhật ký.
- **Xong khi:** script chạy được trên baseline (ghi rõ file nào vượt), có bảng trước/sau để so ở DT10.

### DT01 — Đường vào: `task_on_progress`, `INDEX`, `CLAUDE.md` — TODO (L2) ⛔Q-D1, Q-D3
- **Files:** `task_on_progress.md`, `docs/INDEX.md` (mới), `docs/archive/progress-log-2026-09.md` (mới), `AGENTS.md`, `CLAUDE.md` (nếu Q-D3).
- **Làm:** viết lại `task_on_progress.md` **≤ 5 KB, chỉ trạng thái hiện hành**: mục tiêu, branch/SHA, việc đang mở (ID + trạng thái), blocker, bước kế tiếp, lệnh kiểm tra, 3–5 lưu ý bắt buộc. Chuyển danh sách SHA, nhật ký validation và các đoạn "Review/validation mới" sang `progress-log-2026-09.md` (T2). `INDEX.md` ≤ 2 KB: bảng `doc → khi nào đọc → tầng → kích thước`. `AGENTS.md`: thêm quy tắc đọc theo tầng, giới hạn kích thước, và "nhật ký dài → archive". Không đổi bốn nguyên tắc ưu tiên hiện có.
- **Xong khi:** T0 ≤ 12 KB (`docs-budget -Check`); không mất thông tin (mọi mục cũ còn trong archive log); các link resolve.

### DT02 — Lưu trữ lịch sử đã hoàn thành — TODO (L2) ⛔Q-D2
- **Chuyển bằng `git mv` (giữ history):** `REFACTOR-TASKS.md` (172 KB), `REFACTOR-PLAN.md` (30 KB), `test-parity.md` (56 KB), `PERF-DIAGNOSIS-PLAN.md`+`-TASKS.md` (51 KB; xem ghi chú), `diagnosis/*`, `results/*`, `archive/*` hiện có → `docs/archive/`.
- **Để lại digest thay thế:** `docs/refactoring/REFACTOR-STATUS.md` ≤ 4 KB (hướng B+C3, bảng ID→trạng thái một dòng, INV-1…12 trỏ `architecture.md`, phần còn mở: T89 IN PROGRESS, T73/T74 ghi chú) và `PERF-STATUS.md` ≤ 2 KB (D01/D02 blocked, D07/D12 TODO/BLOCKED, kết luận chính + link báo cáo lưu trữ).
- **Ghi chú:** `PERF-DIAGNOSIS-*` còn 2 BLOCKED + 2 TODO → giữ trạng thái ở `PERF-STATUS.md`; chi tiết task chưa xong giữ nguyên trong archive và được trỏ tới.
- **Sửa link:** README (T66), các doc nhắc `PhotoReview.Tests/Program.cs`/`PhotoReview.Tests.Unit` (thêm ghi chú "layout cũ" ở đầu file archive, không sửa nội dung lịch sử), `INDEX.md`. Script kiểm link tương đối (DT09).
- **Xong khi:** thư mục `docs/refactoring/` chỉ còn plan/status đang mở + T89; không link gãy; `git log --follow` vẫn truy được lịch sử; `docs-budget` báo giảm ≥ 70% dung lượng vùng làm việc.

### DT03 — Nén plan/task đang mở — TODO (L2)
- **Đối tượng:** `OPTIMIZE-CLEAN-PLAN` (45 KB; OC01–OC10 phần lớn DONE kèm đoạn "Kết quả"), `STRUCTURE-OPTIMIZE-{PLAN,TASKS}` (30 KB), `TEST-CLEANUP-PLAN` (23 KB), cùng các mục OC14–18/WD/IO.
- **Làm:** (a) chuyển bằng chứng/phát hiện chi tiết đã xử lý (mục 1–2 của OC) và các đoạn "Kết quả" narrative sang archive, để lại **một dòng/task**: `ID · trạng thái · SHA`. (b) Dùng **mẫu task gọn**: bảng `ID | TT | phụ thuộc | files | xong khi (một câu)`; chi tiết chỉ giữ khi task còn TODO/BLOCKED. (c) Bỏ khối lặp giữa các task ("Không làm", "Rủi ro/rollback" chung) → một mục "quy tắc chung" cho cả plan. (d) Gộp danh sách quyết định chưa chốt (Q-ST*, Q-T*, Q-D*, OC14) vào **một bảng** trong `docs/refactoring/OPEN-DECISIONS.md` ≤ 3 KB.
- **Mục tiêu:** tổng tài liệu kế hoạch đang mở ≤ 40 KB, mỗi file ≤ 15 KB. Không đánh DONE thêm task nào; không đổi nội dung chấp nhận (acceptance) của task còn mở.
- **Xong khi:** đối chiếu danh sách ID trước/sau (không mất task, không mất acceptance của task mở); `docs-budget` đạt.

### DT04 — Docs sống + bản đồ code — TODO (L2)
- **Files:** `docs/architecture.md`, `docs/APP-MECHANISMS-VI.md`, `README.md`, `docs/adr/*`.
- **Làm:** (a) thêm vào `architecture.md` **bản đồ code** ≤ 2 KB: `project → trách nhiệm → file vào chính` (ví dụ Undo → `Core/FileActions/UndoService.cs`), để agent định vị không cần explore; kiểm bằng `git ls-files` khớp thực tế. (b) Loại trùng lặp: setting decoder/cache có ở README, architecture và mechanisms → một nguồn (architecture), README chỉ trỏ. (c) ADR: thêm 2–3 dòng đầu "Status/Decision/Hệ quả" để đọc phần đầu là đủ. (d) Đồng bộ với ST11 (sơ đồ phụ thuộc mới), làm sau hoặc cùng ST11, không hai lần.
- **Xong khi:** mỗi file ≤ ngân sách T1; bản đồ code có mọi project trong `PhotoReview.slnx`; không thông tin bất biến (INV, cơ chế cache) bị mất.

### DT05 — Nén `T89-FIT-LAYOUT-PLAN.md` — TODO (L2)
- **Làm:** 17.7 KB → ≤ 8 KB: giữ trạng thái, acceptance (≤0.5 DIP, GUI Fit→wheel→drag), việc còn lại; chuyển phần suy diễn/toán chi tiết đã hiện thực sang archive kèm link. **Không** đổi hoặc bỏ tiêu chí nghiệm thu; task vẫn IN PROGRESS.
- **Xong khi:** đối chiếu từng tiêu chí trước/sau.

### DT06 — Vệ sinh comment code (chỉ comment) — TODO (L2)
- **Quy tắc giữ:** invariant (`INV-*`), race/thứ tự, hợp đồng interface (Core.Abstractions), native ownership/COM, lý do không hiển nhiên. **Xóa/rút gọn:** (1) mã lịch sử `T14a`/`D05`/`K-2`/`WC3`… (65 src + 32 tests/tools) → thay bằng lý do một dòng hoặc `INV-x`/ADR nếu còn cần; (2) `<summary>` chỉ diễn lại tên (audit 103 dòng đơn, không xóa hàng loạt); (3) comment trùng giữa interface và implementation → `<inheritdoc/>`; (4) trỏ tới thứ không còn (ví dụ `Program.cs RunInterleavedFileActionSequence`, `PhotoReview.Tests`); (5) đoạn kể lịch sử refactor dài → một dòng "vì sao".
- **Không làm:** không dịch ngôn ngữ hàng loạt, không sửa code, không format lại file, không xóa `///` của API công khai trong Core.Abstractions.
- **Cách làm/kiểm chứng:** script liệt kê ứng viên (regex mã lịch sử + summary một dòng); mỗi commit chỉ chạm dòng comment. Kiểm bằng script `tools/verify-comment-only.ps1`: bỏ mọi dòng bắt đầu bằng `//`/`///` và dòng trống ở bản trước/sau, hai kết quả phải **giống hệt** (bắt cả trường hợp lỡ sửa code). Build Release + test không đổi kết quả.
- **Xong khi:** mã lịch sử còn lại là 0 (ngoại trừ ID bất biến `INV-*`); byte comment giảm (ghi số); build/test PASS; verify-comment-only PASS.
- **Đụng owner:** file `MainViewModel.cs`, `App.xaml.cs`, `MainWindow.xaml.cs` chỉ một owner — làm **sau** hoặc **cùng lượt** với ST03/ST07–ST09 để không xung đột.

### DT07 — Giảm boilerplate test — TODO (L2)
- **Làm:** đưa vào `TestSupport` (hoặc dùng project Windows của TC01 khi cần WPF) các helper trùng: `FakeExplorerOrderProvider`, `FakeAppPaths`, `FakeClock`, `TestPresentationSink`, `TestPreloadController`, `FakeRecycleBin`, `FakeDialogService`; thay 7 bản chép PNG 1×1 bằng `TestImages.PreviewPng`. Gộp cùng lượt với TC10 để không đụng test hai lần.
- **Không làm:** không gom `GlobalStateCollection` (xunit cùng assembly); không đổi assertion/hành vi test; không xóa test.
- **Xong khi:** số `[Fact]/[Theory]` không đổi; test PASS như baseline; số dòng test giảm (ghi số).

### DT08 — Giảm nhiễu tìm kiếm — TODO (L1) ⛔Q-D4
- **Làm:** `.ignore` loại `docs/archive/`; kiểm chứng bằng `Grep`/`Glob` của agent rằng tệp archive không xuất hiện nhưng `Read` trực tiếp vẫn được. Xác nhận `bin/obj` đã nằm trong `.gitignore` (đã có) nên không tốn token.
- **Xong khi:** kiểm chứng bằng lệnh tìm thử, ghi kết quả; nếu công cụ không tôn trọng `.ignore` thì bỏ task, chỉ dựa vào `INDEX.md`.

### DT09 — Chốt chặn chống phình lại — TODO (L1)
- **Làm:** thêm vào `tools/verify-all.ps1` bước cảnh báo `docs-budget -Check` (bắt đầu ở mức **cảnh báo**, chuyển lỗi sau khi ổn định), kiểm link tương đối giữa file markdown, và kiểm `INDEX.md` liệt kê mọi file `docs/*.md` không nằm trong archive; cập nhật `tools/test-verify-gates.ps1` theo hợp đồng gate hiện có. `AGENTS.md` (Q-D1) nêu quy tắc: task xong → gấp thành một dòng + SHA, tài liệu mới phải có dòng trong `INDEX.md`.
- **Xong khi:** thêm một doc vượt ngân sách hoặc link gãy tạm thời làm gate báo (kiểm bằng lỗi cố ý, không commit).

### DT10 — Đo lại và bàn giao — ✓ DONE (2026-09-24, PR docs/dt10)
- **Kết quả:** T0 (`AGENTS.md`+`task_on_progress.md`+`docs/INDEX.md`, đo bằng `tools/docs-budget.ps1` trên HEAD) 12044→11128 B (11.8→10.9 KB, ngân sách 12 KB). T1 (5 file `docs/refactoring/*` script tính vào tier này) 23211→10419 B (22.7→10.2 KB, ngân sách 15 KB): `STRUCTURE-OPTIMIZE-STATUS.md` 8926→4187, `TEST-CLEANUP-SUMMARY.md` 4435→1097, `OPEN-DECISIONS.md` 4070→964, `OPTIMIZE-CLEAN-SUMMARY.md` 3299→3172, `T89-FIT-SUMMARY.md` 2481→999 (byte). Phần lịch sử cắt sang `docs/refactoring/archive/*-detail.md` (cut+paste + dòng pointer, không dùng `git mv` vì file gốc vẫn còn phần đang mở). `tools/check-doc-links.ps1` 0 broken sau khi sửa. Cập nhật `docs/ACTIVE-TASKS.md` (dòng DT), không sửa `task_on_progress.md` phần "Now".

## 5. Thứ tự và đụng độ

`DT00 → DT01 → DT02 → DT03 → (DT04 ∥ DT05) → DT08 → DT09 → DT10`; `DT06`, `DT07` độc lập về tài liệu nhưng phải xếp lịch với ST/TC/OC vì cùng chạm `MainViewModel.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, file test VM; `DT04` cùng lượt với ST11.

Rủi ro chính: (1) mất thông tin khi gấp doc → luôn `git mv` + digest + đối chiếu ID/acceptance trước sau; (2) link gãy → script kiểm link ở DT02/DT09; (3) nhiều agent sửa cùng doc → `INDEX.md` và `task_on_progress.md` chỉ một owner tại một thời điểm.

## 6. Kiểm chứng và bàn giao

- Mỗi task doc: `docs-budget -Check`, kiểm link, đối chiếu ID/acceptance. Mỗi task comment/test: build Release + test không đổi kết quả (số PASS baseline ở ST00/TC00), `verify-comment-only` với DT06.
- Nhánh `codex/docs-diet-*`, không commit thẳng `master`; push/PR theo `AGENTS.md`. Rollback bằng revert commit (doc chỉ di chuyển nên không mất dữ liệu).

## 7. Phạm vi chưa làm

Không xóa vĩnh viễn lịch sử, không dịch ngôn ngữ doc/comment hàng loạt, không sinh tài liệu API tự động, không đổi quy tắc ưu tiên hiệu năng/ảnh trong `AGENTS.md`, không đánh giá bằng tokenizer thật (ngoài ước lượng đã nêu).
