# PhotoReview — Audit report hiện hành

Cập nhật: 2026-09-14. Phạm vi: toàn bộ source `PhotoReview.App`, `PhotoReview.Tests`, `tools` và release verification.

## Evidence đã chạy

- Debug build: PASS, 0 warning/0 error.
- Release build solution: PASS, 0 warning/0 error.
- Contract executable tests: PASS.
- File-operation smoke test: PASS.
- Framework-dependent `win-x64` publish và `verify-all.ps1`: PASS.
- Không có ảnh người dùng nào bị dùng làm fixture hoặc bị mutation trong audit.

## Findings theo severity

### P1 — Cache RAM chưa đáp ứng policy hiệu năng

- Evidence: `MainWindow` đặt `MaxCacheBytes = 1 GB`; cache chưa adaptive theo tổng folder và chưa có telemetry RAM.
- Impact: không thực hiện mục tiêu load toàn bộ folder nhỏ hơn 16 GB; chuyển ảnh lớn có thể đọc/decode lại.
- Task: `AUD-E02`, `AUD-E03` — cache budget adaptive, preload full-folder có giới hạn an toàn, memory pressure và đo cache hit.

### P1 — Batch mutation chưa có fault-injection thật

- Evidence: journal/reconcile và dry-run đã có contract test, nhưng chưa mô phỏng crash tại từng ranh giới filesystem/Recycle Bin.
- Impact: chưa chứng minh recovery trên máy thật qua mọi trạng thái lỗi.
- Task: `AUD-D04`, `AUD-F02` — injectable operation executor, fixture fault points và recovery matrix.

### P1 — Shortcut config còn fallback hardcode

- Evidence: runtime đã đọc các field chính, nhưng một số action legacy và mouse/keyboard paths vẫn nằm trực tiếp trong `MainWindow`.
- Impact: thay đổi config có thể không bao phủ toàn bộ interaction.
- Task: `AUD-A04`, `AUD-C03` — command registry duy nhất và test mọi binding.

### P2 — Sort orientation đọc metadata toàn folder đồng bộ theo task

- Evidence: `SortFiles` gọi decoder cho từng file trước khi hiển thị folder.
- Impact: folder nhiều ảnh có thể chậm mở lần đầu và tăng I/O; EXIF orientation chưa được kiểm chứng bằng fixture.
- Task: `AUD-C01`, `AUD-E01` — metadata cache/background scan, đo latency, test portrait/landscape/square/EXIF.

### P2 — Hash cache chưa có giới hạn/eviction riêng

- Evidence: `_hashCache` là dictionary theo phiên, không có quota hoặc clear policy.
- Impact: folder cực lớn có thể giữ metadata không cần thiết; thay đổi file vẫn được kiểm tra stamp nhưng entry cũ không eviction.
- Task: `AUD-D02`, `AUD-E04` — byte/count bound, clear khi đổi folder và invalidation watcher.

### P2 — Recovery UI chỉ xem, chưa có thao tác retry có kiểm soát

- Evidence: `RecoveryWindow` hiển thị pending/failed và không replay.
- Impact: user phải tự xử lý ngoài app; chưa có retry theo fingerprint và destination conflict policy.
- Task: `AUD-D04` — retry riêng Move/Copy/Recycle với confirm, no-overwrite và journal mới.

### P2 — `MainWindow.xaml.cs` còn quá nhiều trách nhiệm

- Evidence: scanner, decode, cache, compare, hash, batch, navigation và file mutation nằm cùng code-behind.
- Impact: khó unit-test và tăng rủi ro thay đổi interaction.
- Task: `AUD-A04` — tách `ImageCatalog`, `ImageLoader`, `DuplicateService`, `ReviewActionExecutor`, `NavigationService`.

### P3 — GUI/accessibility chưa có evidence

- Evidence: chưa có automated UI/keyboard-only acceptance trên fixture; chưa kiểm tra DPI, focus indicator, screen reader.
- Impact: build xanh chưa chứng minh UX public-ready.
- Task: `AUD-C04`, `AUD-F03`.

## Không được đánh dấu hoàn tất nếu thiếu

- Benchmark key-to-present và disk-read count.
- Fixture EXIF/sort/compare/hash có manifest.
- Fault-injection matrix và recovery evidence.
- GUI acceptance trên máy sạch hoặc môi trường Windows tương đương.
