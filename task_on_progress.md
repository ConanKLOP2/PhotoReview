# Tiến độ

- **Cập nhật:** 2026-09-17 (phiên bị dừng sớm để tránh hết token — xem mục "DỪNG GIỮA CHỪNG" ngay dưới đây trước khi làm gì khác) | **Branch làm việc:** `refactor/integration` @ `a4bbfa9` (đã push lên `origin`) · VERIFY đạt, xUnit 256/256
- **Mục tiêu:** tái cấu trúc B + C3 (`docs/refactoring/REFACTOR-PLAN.md`, `REFACTOR-TASKS.md`). Nhánh chẩn đoán hiệu năng WD (`PERF-DIAGNOSIS-PLAN.md`, `PERF-DIAGNOSIS-TASKS.md`) đang **chờ máy 1** (D07+); đã chuyển sang làm tiếp nhánh refactor trên máy 2.
- **Máy 2 (VTI):** không dùng để đo hiệu năng được (không có fixture ảnh thật, RAM 15.58 GB). Đã ghi thông số vào `docs/refactoring/diagnosis/env.md` mục "Máy 2". `verify-all.ps1` chạy tốt trên máy này nên vẫn dùng để làm tiếp refactor track.

## ⚠️ DỪNG GIỮA CHỪNG — đọc trước khi tiếp tục (2026-09-17)

Phiên trước dừng sớm để tránh hết token, ngay sau khi nhóm ∥C (T14b/T14c/T14d) **vừa làm xong hết phần worker**, nhưng **T14c chưa được review R3 và chưa merge**.

- **T14a, T14b, T14d:** DONE, đã merge vào `refactor/integration`, đã review R3 APPROVE, VERIFY đạt, đã push. Không cần làm gì thêm.
- **T14c: worker đã xong (DONE), CHƯA REVIEW, CHƯA MERGE — đây là việc duy nhất còn lại của nhóm ∥C.**
  - Branch: `refactor/T14c-inv5`, commit `a2b696b` (base `f73fec0` = tổ tiên của `refactor/integration` hiện tại, không có xung đột với các merge T14b/T14d vì mỗi task chỉ thêm 1 file test riêng).
  - Worktree: `.claude/worktrees/agent-addb7cccfe75b70eb` (nếu còn tồn tại; kiểm bằng `git worktree list` — nếu mất, branch `refactor/T14c-inv5` vẫn còn trong object database của repo chính, dùng `git worktree add` hoặc `git checkout` bình thường để xem lại).
  - File thêm: `PhotoReview.Tests.Unit/MainWindowBehaviorTests.FolderSwitch.cs` (mới, 278 dòng, 2 test: INV-5 chính + 1 test đối chứng để chống pass rỗng). Không sửa file nào khác.
  - Worker tự báo: VERIFY đạt xUnit 253/253 (baseline 251 + 2), test INV-5 đạt 10/10 lần chạy riêng, attribution commit đã đúng chuẩn (`Co-Authored-By: Claude Sonnet 5`) — đã tự kiểm tra lại, khớp.
  - Nhật ký đầy đủ (sẵn sàng dán vào `REFACTOR-TASKS.md` mục `### T14c`) nằm trong báo cáo cuối của worker (agent id cũ `addb7cccfe75b70eb`, không dùng lại được ở phiên mới — chỉ còn ý nghĩa tham chiếu log).
  - **Việc cần làm khi bắt đầu phiên mới (theo đúng quy trình đã áp dụng cho T14a/T14b/T14d):**
    1. Giao một Opus 5 **review độc lập, không có context worker** (R3), checkout branch `refactor/T14c-inv5` @ `a2b696b`, diff với `refactor/integration`, chạy lại checklist 7 mục trong `REFACTOR-TASKS.md` mục 0 (tự chạy VERIFY + tự chạy lại test INV-5 10 lần, không tin lời worker). Các điểm cần review đặc biệt chú ý (worker tự nêu, cần xác minh độc lập):
       - `MoveOverride` gọi `File.Move` thật (không no-op) để action đi đúng nhánh thành công — có thật vậy không?
       - Test dùng reflection đọc `_files`/`_index`/`_moveHistory`/`_lastUndoAction` — đọc thôi hay có ghi đè gì khác không?
       - Test tự dựng `ReviewAction` riêng (đích `"Loai-2"` trong thư mục tạm) thay vì dùng `_settings.Actions`/`ClassifyCurrentAsync`, để tránh đụng `config.json` thật của máy (giống lưu ý đã có ở T14b/T14d) — xác nhận đúng.
       - Có 2 test thay vì 1 (thêm test đối chứng không đổi folder, để chứng minh assertion không pass rỗng) — chấp nhận được không, hay cần tách/xoá bớt?
       - Không dùng OS-level input simulation, không đụng clipboard.
    2. Nếu APPROVE: merge vào `refactor/integration` (`git merge --no-ff refactor/T14c-inv5`), chạy `.\tools\verify-all.ps1` (kỳ vọng 256/256, vì baseline sau T14d đã là 254... thực ra baseline hiện tại của `refactor/integration` là 256 sau T14d, cộng 2 test T14c → kỳ vọng 258), cập nhật nhật ký + đổi TODO/IN PROGRESS → DONE trong `REFACTOR-TASKS.md`, commit, push.
    3. Nếu CHANGES: giao lại đúng worker cũ (không có, vì agent cũ đã mất) hoặc một worker Opus 5 mới sửa theo danh sách CHANGES, rồi review lại (tối đa 2 vòng theo quy trình).
    4. Sau khi T14c merge xong: nhóm ∥C hoàn tất, có thể sang **T20** (tạo `PhotoReview.Core`, phụ thuộc T14b–d).
  - **Dọn dẹp sau khi merge:** `git worktree remove .claude/worktrees/agent-addb7cccfe75b70eb --force` và `git branch -d refactor/T14c-inv5` (chỉ sau khi đã merge xong).

## Chuyển sang máy khác (làm theo thứ tự)

1. **Lấy code**
   ```powershell
   git clone https://github.com/ConanKLOP2/PhotoReview.git
   cd PhotoReview
   git switch refactor/integration
   ```
2. **SDK:** cài .NET SDK 10.0.401 hoặc mới hơn (`global.json` dùng `rollForward=latestFeature`).
3. **Kiểm tra máy mới:**
   ```powershell
   .\tools\verify-all.ps1
   ```
   Kỳ vọng xUnit 250/250 và dòng `PASS: all PhotoReview verification gates`.
4. **Fixture ảnh:** copy `tools/diag/fixtures.example.json` thành `work/diag/fixtures.local.json`, rồi sửa đường dẫn F1/F1b/F2/F3/F4 theo máy mới.
   - Ghi file bằng `ConvertTo-Json` hoặc editor; mỗi backslash phải viết thành `\\`.
   - Máy cũ dùng một thư mục ảnh 12,9 GB, gồm 1.842 ảnh (1.820 JPEG, 22 PNG).
   - Máy cũ không có F5, F6, F7.
   - **Không commit tên hay đường dẫn thư mục ảnh.**
5. **Công cụ đo** (hỏi người dùng trước khi tải hoặc cài):
   - `dotnet tool install -g dotnet-counters`
   - `dotnet tool install -g dotnet-trace`
   - Process Monitor: tải `https://download.sysinternals.com/files/ProcessMonitor.zip`, giải nén vào `work/tools/procmon/`, kiểm chữ ký Microsoft bằng `Get-AuthenticodeSignature`.
   - `wpr` có sẵn trong Windows.
6. **Ghi lại môi trường:** chạy lại phần "thông tin máy" của D00 và thêm một mục **"Máy 2"** vào `docs/refactoring/diagnosis/env.md`. Không ghi đè số liệu của máy cũ: số liệu D05/D11 hiện có được đo trên máy cũ (i7-9750H, 32 GB, NVMe), nên **không trộn số liệu giữa hai máy** khi so sánh.
7. **Kiểm tra nhanh công cụ đo:**
   ```powershell
   .\tools\diag\run-matrix.ps1 -Scenarios s3-next-burst -Modes Preview -Conditions warm -Repeat 1 -FixtureAlias F1 -OutRoot work/diag/runs-smoke
   dotnet run --project PhotoReview.Tests -c Release -- --perf-analyze <thư mục batch vừa tạo>
   ```

## Đã xong

- **Refactor:** T00, T02, T03, T04, T05, T10, T11, T12, T13a, T13b, T14a, T14b, T14d (nhật ký chi tiết trong `REFACTOR-TASKS.md`). T14c: worker đã xong nhưng **chưa review, chưa merge** — xem mục "DỪNG GIỮA CHỪNG" ở đầu file.
- **Chẩn đoán:** D00, D03, D04, D05, D06, D10, D11 (nhật ký trong `PERF-DIAGNOSIS-TASKS.md`).
- **Công cụ đo có trong repo:**
  - Event `PhotoReview-Perf` + CSV (`PHOTOREVIEW_PERF_TRACE`).
  - Các biến `PHOTOREVIEW_DIAG_PREREAD`, `PHOTOREVIEW_DIAG_PRELOAD_WORKERS`, `PHOTOREVIEW_DIAG_DISABLE_DISKCACHE`.
  - `--io-decode-split`.
  - `--perf-session` (driver trong process; xem `docs/refactoring/diagnosis/perf-session.md`).
  - `tools/diag/run-matrix.ps1`.
  - `--perf-analyze` + `tools/diag/rules.json`.
  - `tools/diag/parse-applog.ps1`, `tools/diag/procmon-summary.ps1`.

## Số liệu sơ bộ (máy cũ)

- `docs/refactoring/diagnosis/io-decode-split.md`:
  - Decode chiếm 98–100% thời gian so với đọc file (file đã nằm trong cache OS).
  - JPEG 48 MP decode khoảng 1,3 s dù hạ width.
  - Disk cache PNG lỗ với ảnh decode rẻ, lãi 2,7–12× với ảnh decode đắt.
- Chạy thử S3 warm trên F1: P50 5,2 ms, hit 99,5%. Trong 10% lần chậm nhất, `t_render` chiếm khoảng 92% (chưa kết luận; có thể do cửa sổ test không ở foreground).

## Việc tiếp theo

1. **Refactor (làm được trên máy 2):** review + merge **T14c** (worker đã DONE, chỉ còn thiếu review R3 + merge — xem mục "DỪNG GIỮA CHỪNG" ở đầu file, đây là việc ưu tiên số 1 khi resume). Sau đó **T20** (tạo `PhotoReview.Core`, phụ thuộc T14b–d).
2. **D07 (ma trận đo, chỉ chạy trên máy 1): CHỜ NGƯỜI DÙNG quyết định** trước khi chạy:
   - Phạm vi: rút gọn khoảng 1 giờ, đầy đủ vài giờ, hoặc hoãn.
   - Có cho xóa cache preview/thumbnail của app để đo cold-diskcache không.
   - Process Monitor (cần bấm UAC) cho phần đo lại D02.
   - Có reboot để đo cold-OS không.
3. **D01/D02 (BLOCKED, chỉ máy 1):** đo lại trong D07 bằng `--perf-session` (và Procmon cho D02). Không dùng `SendInput`.
4. **Sau đó (máy 1):** D08 (WPR/PresentMon; WPA và PresentMon chưa cài), D09 (dotnet-counters), rồi D12 (báo cáo và quyết định), D13 (cập nhật plan).

## Lưu ý quan trọng

- **Quy tắc 4 (`PERF-DIAGNOSIS-TASKS.md`):** không bao giờ gửi input ở mức OS (`SendInput`, `SendKeys`, `SetForegroundWindow`, …) và không đụng clipboard. Ngày 2026-09-16, phím mô phỏng của D01 đã rơi vào cửa sổ Claude Code.
- **Config thật của người dùng** có action Enter = Move vào một thư mục ảnh thật. `--perf-session` thay toàn bộ action trong bộ nhớ và chỉ chạy action trên bản sao tạm. Không ghi `config.json` thật.
- **Lỗi đã biết, chưa sửa:** `PreloadScheduler` gọi `Dispatcher.Yield()` khi không có Dispatcher, nên preload dừng sau lô đầu (benchmark CLI và BenchmarkWindow). Đã chuyển cho T32; D12 không dùng số liệu preload benchmark cũ.
- **Quy trình:**
  - Task giao theo bảng model/review (mục 0 của `REFACTOR-TASKS.md`).
  - Worker không sửa file task, không `git add -A`, không push.
  - Coordinator merge vào `refactor/integration`, chạy `tools/verify-all.ps1`, rồi push.
  - Chỉ vào `master` qua PR (AGENTS.md, T05).
- **Bài học review:** Haiku từng bịa tên method (T10), sửa ngoài phạm vi (T12), gắn Trait làm test bị loại khỏi CI (T13a). Coordinator luôn phải đọc diff. Task tra cứu tên phải có script kiểm.
- **Giới hạn sử dụng:** agent từng bị dừng vì HTTP 429 (giới hạn chi tiêu). Nếu reviewer R3 không chạy được thì Coordinator tự review và ghi vào nhật ký.
- **Thư mục chỉ có trên máy cũ** (không cần mang theo): `work/diag/io-split`, `work/diag/runs-smoke` (bản tóm tắt đã có trong `docs/`), `work/diag/config.backup.json` (config đã được khôi phục).
