---
id: NOWPF-HANDOFF-20261010
order: 402
summary: |-
  Bàn giao NO-WPF (cập nhật 2026-10-14): đợt gần xong (shell Win32 + cửa sổ phụ Win32, CI 6 job), việc còn mở, quyết định chờ người dùng, quy tắc và bẫy vận hành.
---

# Bàn giao NO-WPF (đọc file này đầu tiên khi tiếp tục; cập nhật 2026-10-14, master `6f508390` #518)

Kế hoạch gốc: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md), thẻ gói [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md). Mỗi gói đã merge có
fragment `NOWPF-WPxx-*.md` cạnh file này. Người dùng nói tiếng Việt. Lịch sử theo PR: `git log`, [HISTORY](../HISTORY.md).

## 0. Hiện trạng
- **Đã merge:** WP-01..36 trừ phần nêu dưới. `PhotoReview.exe` = shell Win32 + Direct2D, hai exe trong gói release (R2R; AOT là artifact
  riêng, job CI `publish-aot` còn `continue-on-error`). Settings 7 trang + Recovery + hộp thoại phụ đều là Win32 gốc; cầu WPF đã gỡ khỏi
  shell (#500). CI 6 job test + compile-gate, tường ~200-220 s (#502, #508). Emoji màu sửa (#516), Tab/căn giữa ô nhập (#517).
- **Hồi quy L1 và bài học:** #503 (WS_CLIPSIBLINGS) làm MỌI trang Settings trống; test của nó so hai ảnh đều trống nên xanh. Sửa #511
  (group box xuống đáy Z-order). Mọi test hình ảnh phải kèm khẳng định "không toàn màu nền".
- **D2D cho Settings:** spike xong, GIỮ bản vá (#510; 82-112 giờ-agent > ngưỡng 80).
- **Flaky:** đã sửa tận gốc #512-#514, #518 (trần Join/Wait cố định -> giới hạn chống treo). Còn lại: `AppForwardedOpenTests` A3 chưa tái hiện,
  chưa rõ nguyên nhân (giả thuyết: `ApplicationIdle` bị đói), để nguyên; xem [FLAKY-INTEGRATION-APP](FLAKY-INTEGRATION-APP.md).
- **AOT:** chạy thử cục bộ #506 (smoke 3/3, 4 cửa sổ phụ mở thật, ảnh đầu 451 vs 1028 ms, máy bận). Đề xuất B (AOT mặc định có điều kiện,
  R2R dự phòng) CHƯA XÁC NHẬN; điều kiện: A/B trên máy rảnh, người dùng dùng AOT vài ngày (lưu Settings, RAW, Move/Recycle/Undo,
  Benchmark, BatchReview), smoke AOT trong CI. Xem [NOWPF-AOT-LOCAL-TRIAL](NOWPF-AOT-LOCAL-TRIAL.md).
- **Gate hiệu năng đợt 3:** PR #519 (đang mở khi ghi) đo lại trên máy yên: AOT cửa sổ 112 ms ĐẠT, ảnh đầu 204 ms ĐẠT, Settings lần sau 48 ms ĐẠT;
  R2R cửa sổ 321 ms HỤT, key->frame P95 AOT 4,8 ms HỤT (mục tiêu 3 ms, sàn = 1 khung). Số chính thức: `perf/2026-10-14-gate3-measure.md`
  sau khi #519 merge.

## 1. Việc còn mở
- **Combobox sau Tab:** ngay sau Tab, combobox có focus vẽ nền xanh + viền cam, sau `RedrawWindow` thì khác (khung chấm): hai đường vẽ không
  nhất quán ([FIX-TAB-AND-EDIT-CENTER](NOWPF-WP35-FIX-TAB-AND-EDIT-CENTER.md)); chưa sửa, test loại ô đang focus khỏi phép so.
- **Smoke AOT trong CI** (`aot-window-check.ps1` trên desktop ẩn) chưa có; đổi mặc định AOT chờ người dùng dùng thử.
- **WP-27 phần cần phần cứng thật:** F11 hai màn hình, DPI hai màn hình, title bar tối DWM, menu trên desktop thật build 26300
  ([NOWPF-WP27-VISUAL-CHECK](NOWPF-WP27-VISUAL-CHECK.md)). Cần người dùng nhìn.
- **WP-37 xoá bản WPF** chờ người dùng quyết (NE-7).
- **Kiểm toán test CI (2026-10-10):** đã xử lý các lỗ hổng B-02, B-03, C-01, C-03, C-04, C-05, D-01 (#417-#421 và sau). CHƯA kiểm lại, tra
  `.github/workflows/ci.yml` trước khi làm: CODEOWNERS/review bắt buộc cho `.github/**` và Architecture.Tests; job Native/Slow trên runner
  GitHub (`TEST_FILTER` vẫn loại Native/Slow/Manual; có job `test-integration-slow`); baseline số test theo project; lên lịch `raw-corpus.yml`;
  D-02/D-03 (hook IDialogService, StaTestHost fail khi AppLog có Error); B-01 (Move khác volume + journal Fast chỉ chịu process-crash, ADR 0007);
  B-04..B-09; đột biến phía Imaging (I1-I15); test tự thoát sớm khi thiếu RAW corpus (xanh giả).
- **Chờ người dùng duyệt:** các dòng CHƯA XÁC NHẬN trong [NOWPF-UNCONFIRMED-DECISIONS-20261010](NOWPF-UNCONFIRMED-DECISIONS-20261010.md) (NE-3/4/6/7/9,
  WP20-VISIBLE, WP21-*, WP23-*, WP24-ANCHOR, WP25-*, WP26-COMPARE, WP28-*, WP04-JPEG, B-06, C-04) và các fragment `FLAKY-*`, `NOWPF-PERF-GATE3`.
- Khởi động: bản WPF NW-5 (<= 600 ms R2R) chưa đạt (782 ms); phương án self-contained + R2R composite (174 MB) chưa chọn. Chế độ chạy nền
  (giữ tiến trình, khay): người dùng chọn nhầm rồi huỷ, chưa làm, hỏi lại. Fit width/height lệch 5 DIP: PR riêng trên app WPF.

## 2. Môi trường
- Liên kết .jpg trỏ bản publish R2R `C:\MyProjects\PhotoReview\publish-r2r` (15 MB, git-ignored); publish lại sau mỗi lần merge mã.
- Native AOT cần MSVC x64 + Windows 11 SDK (đã có VS BuildTools + SDK 26100). Ổ C: gần đầy; dọn `work/diag/tune-runs`, và luôn
  `--blame-hang-dump-type none` khi chạy `dotnet test` cục bộ.
- Quy tắc agent: `docs/AGENT-HYGIENE.md` (timeout, scratchpad riêng, `tools/agent-finish-check.ps1`, không giết testhost của agent khác).

## 3. Bẫy vận hành
- Xung đột NGỮ NGHĨA giữa PR: hai PR đúng riêng lẻ vẫn vỡ biên dịch khi gộp. `strict:false` nên CI không bắt trước khi merge: build nhánh
  SAU khi sync master, trước khi để auto-merge.
- OPEN-DECISIONS.md (bảng sinh) và `order:` trùng giữa các fragment xung đột ở mọi PR: dùng `tools/sync-pr-branch.sh <worktree>`
  (lấy bản master, sinh lại, đổi `order` trùng, giữ cả hai phía i18n/PERF-STATUS), rồi `generate/check-open-decisions`.
- Windows Update có thể KHỞI ĐỘNG LẠI máy giữa phiên: cron/lịch của phiên chỉ sống trong phiên, mất sau reboot; đẩy commit sớm, kiểm lại
  tiến trình và lịch sau khi máy lên.
- Script trong `tools/` chỉ ASCII hoặc có BOM UTF-8: powershell.exe 5.1 đọc UTF-8 không BOM theo ANSI và làm hỏng tiếng Việt.
- Test hình ảnh phải có khẳng định không-toàn-nền (xem L1 ở mục 0): so "trước" với "sau" khi cả hai cùng trống vẫn xanh.
- Test chụp BitBlt dùng `SettingsScrollRepaintFixTests.ParkCursor(d)`: con trỏ thật đứng yên trên control đang cuộn sinh WM_MOUSEMOVE
  làm viền hover khác nhau giữa các ảnh.
- GitHub đôi khi giữ head PR cũ sau khi push (CI chạy trên commit cũ, mergeable sai): đẩy một commit rỗng
  (`git commit --allow-empty`) để buộc cập nhật.
- Scratchpad dùng chung giữa agent: file tạm có tiền tố tên gói. Agent từng ghi nhầm file vào checkout chính (file rỗng, file `max`):
  kiểm `git status` của checkout chính định kỳ; `AppSettingsPocoTests.cs` untracked có từ trước phiên, để nguyên.
- Windows: xoá worktree dài cần tiền tố long-path; thư mục tạm có thể bị khoá (Permission denied), git đã gỡ đăng ký.
- Tránh heredoc shell cho file chứa backtick; dùng công cụ ghi file.
- Build Release ở checkout chính sau mỗi `git merge --ff-only origin/master` (AGENTS.local.md).
