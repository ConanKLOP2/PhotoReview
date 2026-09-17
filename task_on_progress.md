# Tiến độ

- **Cập nhật:** 2026-09-17 (phiên bị dừng sớm để tránh hết token — xem mục "DỪNG GIỮA CHỪNG" ngay dưới đây trước khi làm gì khác) | **Branch làm việc:** `refactor/integration` @ `a4bbfa9` (đã push lên `origin`) · VERIFY đạt, xUnit 256/256
- **Mục tiêu:** tái cấu trúc B + C3 (`docs/refactoring/REFACTOR-PLAN.md`, `REFACTOR-TASKS.md`). Nhánh chẩn đoán hiệu năng WD (`PERF-DIAGNOSIS-PLAN.md`, `PERF-DIAGNOSIS-TASKS.md`) đang **chờ máy 1** (D07+); đã chuyển sang làm tiếp nhánh refactor trên máy 2.
- **Máy 2 (VTI):** không dùng để đo hiệu năng được (không có fixture ảnh thật, RAM 15.58 GB). Đã ghi thông số vào `docs/refactoring/diagnosis/env.md` mục "Máy 2". `verify-all.ps1` chạy tốt trên máy này nên vẫn dùng để làm tiếp refactor track.

## ⚠️ DỪNG GIỮA CHỪNG — đọc trước khi tiếp tục (2026-09-17)

Phiên trước dừng sớm để tránh hết token, **giữa lúc T14b/T14c/T14d đang chạy song song (nhóm ∥C)**. Trạng thái chính xác tại thời điểm dừng:

- **T14a, T14b, T14d:** đều **DONE**, đã merge vào `refactor/integration`, đã review R3 APPROVE, VERIFY đạt (256/256 sau merge T14d), đã push lên `origin`. Nhật ký đầy đủ trong `REFACTOR-TASKS.md`. **Không cần làm gì thêm cho 3 task này.**
- **T14c: CÒN DANG DỞ — đây là việc duy nhất cần làm tiếp.** Worker (Opus 5, agent id `addb7cccfe75b70eb` trong phiên cũ — id này **không dùng lại được** ở phiên mới, chỉ ghi để đối chiếu log nếu cần) đang viết file `PhotoReview.Tests.Unit/MainWindowBehaviorTests.FolderSwitch.cs` (test INV-5) khi phiên bị dừng. File này còn **untracked, chưa commit**, nằm trong worktree `.claude/worktrees/agent-addb7cccfe75b70eb` trên branch `refactor/T14c-inv5` (base `f73fec0`, tức là base **cũ hơn** `refactor/integration` hiện tại — cần rebase/tạo lại branch từ HEAD mới trước khi tiếp tục, vì T14b/T14d đã merge thêm 2 file test khác không xung đột nhưng branch nên bám HEAD mới nhất để VERIFY đúng số lượng test).
  - **Việc cần làm khi bắt đầu phiên mới:**
    1. `git worktree list` để xem worktree `agent-addb7cccfe75b70eb` còn tồn tại không (agent nền gần chắc chắn đã dừng cùng phiên trước, nhưng file trên đĩa thì còn).
    2. Đọc `PhotoReview.Tests.Unit/MainWindowBehaviorTests.FolderSwitch.cs` trong worktree đó, đánh giá đã hoàn chỉnh chưa: có biên dịch được không (`dotnet build`), có test INV-5 thật chưa hay mới là khung sườn.
    3. **Backup trước khi động vào gì:** copy file ra ngoài hoặc `git add` + commit tạm (`git commit -m "WIP T14c snapshot before resume"`) ngay trên `refactor/T14c-inv5`, phòng khi cần đối chiếu.
    4. Nếu file đã gần hoàn chỉnh: có thể giao tiếp cho một worker Opus 5 mới, cung cấp file WIP này làm điểm khởi đầu, yêu cầu hoàn thiện + tự verify + đạt 10/10 theo đúng spec T14c.
    5. Nếu file dang dở/không rõ ràng: an toàn nhất là bỏ, tạo branch `refactor/T14c-inv5` mới từ `refactor/integration` HEAD hiện tại, giao lại từ đầu cho worker Opus 5 mới.
    6. Spec đầy đủ: mục `### T14c` trong `REFACTOR-TASKS.md` (INV-5: "Action hoàn tất sau khi đã đổi folder: không đăng ký undo, không sửa catalog" — `MoveOverride` chờ trong lúc mở folder B, rồi cho Move hoàn tất, assert catalog B không đổi và Ctrl+Z không đụng file), cộng ghi chú bắt buộc dùng `DataRootFixture` (thêm sau T14a). Có thể tham khảo cách các worker T14b/T14d đã brief (đọc `MainWindow.xaml.cs` thật để tìm cơ chế trigger, dùng `UIElement.RaiseEvent` cho phím tắt, không SendInput/SendKeys, không override file ngoài danh sách Files).
    7. Sau khi T14c DONE: merge vào `refactor/integration` (`git merge --no-ff`), chạy `.\tools\verify-all.ps1` (kỳ vọng 256 + số test mới của T14c), ghi nhật ký vào `REFACTOR-TASKS.md` (đổi TODO/IN PROGRESS → DONE), commit, push. Sau đó nhóm ∥C hoàn tất, có thể sang **T20** (tạo `PhotoReview.Core`, phụ thuộc T14b–d).
  - **Dọn dẹp:** sau khi xử lý xong (dù giữ hay bỏ file WIP), dọn worktree cũ: `git worktree remove .claude/worktrees/agent-addb7cccfe75b70eb --force` (chỉ sau khi đã backup/commit nội dung cần giữ).

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

- **Refactor:** T00, T02, T03, T04, T05, T10, T11, T12, T13a, T13b, T14a, T14b, T14d (nhật ký chi tiết trong `REFACTOR-TASKS.md`). T14c còn dang dở — xem mục "DỪNG GIỮA CHỪNG" ở đầu file.
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

1. **Refactor (làm được trên máy 2):** hoàn thiện **T14c** (xem mục "DỪNG GIỮA CHỪNG" ở đầu file — đây là việc ưu tiên số 1 khi resume). Sau đó **T20** (tạo `PhotoReview.Core`, phụ thuộc T14b–d).
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
