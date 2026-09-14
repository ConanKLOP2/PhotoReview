# PhotoReview — Audit report hiện hành

Cập nhật: 2026-09-14. Phạm vi: toàn bộ source `PhotoReview.App`, `PhotoReview.Tests`, `tools` và release verification.

## Evidence đã chạy

- Debug build: PASS, 0 warning/0 error.
- Warning-as-error build riêng cho App và Tests: PASS, 0 warning/0 error.
- Release build solution: PASS, 0 warning/0 error.
- Contract executable tests: PASS.
- File-operation smoke test: PASS.
- Framework-dependent `win-x64` publish và `verify-all.ps1`: PASS.
- Sau khi thêm `RuntimeIdentifiers=win-x64`, publish `--no-restore` chạy đúng và verifier kiểm tra artifact mới nhất; sau các thay đổi gần nhất, release gate lại PASS: EXE 162,304 bytes, SHA-256 `953A45AA7ECC40DDE89B5257FF0F02DCDC059689C5E3BAC12C2F02C4381B768C`.
- Không có ảnh người dùng nào bị dùng làm fixture hoặc bị mutation trong audit.

## Findings theo severity

### P1 — Cache RAM chưa đáp ứng policy hiệu năng

- Evidence: đã nâng `MaxCacheBytes` lên 16 GB, preload full-folder khi source folder dưới 16 GB và dừng preload nền ở 80% memory load; chưa có decoded-footprint telemetry.
- Evidence bổ sung: `ReviewMetrics` đã ghi cache hit/miss, source bytes và decode milliseconds trong process; chưa có GUI benchmark tự động.
- Evidence bổ sung: Diagnostics view đã hiển thị metrics trong phiên hiện tại, source-byte counter loại trừ disk-cache hit, số source file reads, và present-latency trung bình ms/ảnh; chưa thay thế benchmark tự động trên fixture chuẩn.
- Impact: policy đã được phản ánh trong code với guard nền, nhưng cần benchmark để xác định decoded footprint khi ảnh giải nén lớn.
- Task: `AUD-E02`, `AUD-E03` — memory-pressure guard, adaptive decoded budget và đo cache hit/RAM thực tế.

### P1 — Batch mutation chưa có fault-injection thật

- Evidence: journal/reconcile và dry-run có contract test, gồm source còn tồn tại và destination sai kích thước đều bị đánh dấu Failed; append journal dùng WriteThrough/Flush(true); chưa mô phỏng crash tại từng ranh giới filesystem/Recycle Bin.
- Impact: chưa chứng minh recovery trên máy thật qua mọi trạng thái lỗi.
- Task: `AUD-D04`, `AUD-F02` — injectable operation executor, fixture fault points và recovery matrix.

### P1 — Shortcut config còn fallback hardcode

- Evidence: runtime đã đọc các field chính và thêm mapping cấu hình cho Skip/Undo/Fullscreen; click ảnh thường không còn handler điều hướng, compare preview giữ handler chọn riêng, action file dùng đúng preview đã chọn và selection được reset khi điều hướng. Một số action legacy và keyboard paths vẫn nằm trực tiếp trong `MainWindow`.
- Impact: thay đổi config có thể không bao phủ toàn bộ interaction.
- Task: `AUD-A04`, `AUD-C03` — command registry duy nhất và test mọi binding.

### P2 — Sort orientation đọc metadata toàn folder đồng bộ theo task

- Evidence: `ImageSortService.Sort` gọi decoder cho từng file trước khi hiển thị folder; logic đã tách khỏi code-behind, có test natural numeric ordering, fallback metadata và JPEG fixture thật với EXIF orientation 6.
- Impact: folder nhiều ảnh có thể chậm mở lần đầu và tăng I/O; EXIF orientation 5/6/7/8 đã được kiểm chứng bằng JPEG fixture.
- Task: `AUD-C01`, `AUD-E01` — metadata cache/background scan, đo latency, test portrait/landscape/square/EXIF.

### P1 — Shortcut runtime đã chuyển sang cấu hình tập trung

- Evidence: navigation, sibling-folder, Home, zoom, Skip, Undo, Fullscreen và action profiles đều kiểm tra `_settings.Shortcuts`; không còn fallback phím điều hướng/zoom hardcode. Contract test và Release build PASS (`891f403`).
- Remaining: command registry duy nhất và kiểm thử mọi binding ở mức UI vẫn thuộc `AUD-A04`, `AUD-C03`.

### P1 — Conflict shortcut giữa global bindings và action profiles đã được chặn

- Evidence: `AppSettings.ValidateShortcuts` kiểm tra key hợp lệ và duplicate trên toàn bộ `ShortcutMappings` + `ReviewAction`; Settings gọi validator trước khi lưu (`6c1f40e`). Contract test và Release build PASS.
- Remaining: recorder hiện chỉ hỗ trợ một phím đơn; chord modifier/profile theo context vẫn là phạm vi mở rộng, chưa coi là đã triển khai.

### P2 — Compare shortcut đã được nối vào runtime

- Evidence: `_settings.Shortcuts.Compare` bật/tắt `ComparePanel`; test contract xác nhận binding (`0c8f335`). Preview click/keyboard vẫn chỉ thay đổi file được chọn để thao tác.
- Remaining: chưa có GUI acceptance đo thao tác thực tế và chưa có đồng bộ zoom/pan giữa hai preview.

### P2 — Hash cache đã được giới hạn; cần bổ sung quota/telemetry nếu public quy mô lớn

- Evidence: `_hashCache` hiện là `BoundedLruCache` LRU 16 MB, clear khi đổi folder và kiểm tra lại length/last-write trước khi dùng.
- Impact: memory growth không còn vô hạn; quota cấu hình và hit/eviction telemetry vẫn là cải tiến sau nếu public quy mô lớn.
- Task: `AUD-D02`, `AUD-E04` — core eviction/invalidation đã hoàn tất; quota cấu hình/telemetry nâng cao còn mở.

### Ghi chú về các catch có chủ đích

- JSONL journal bỏ qua từng dòng hỏng để không làm mất khả năng đọc các entry hợp lệ còn lại.
- Image sort coi EXIF không đọc được là square/unknown và tiếp tục mở folder.
- Disk preview cache hỏng được xóa và fallback về source; lỗi ghi cache không chặn việc review ảnh.

### P2 — Recovery UI chỉ xem, chưa có thao tác retry có kiểm soát

- Evidence: `RecoveryWindow` hiển thị pending/failed và không replay.
- Impact: user phải tự xử lý ngoài app; chưa có retry theo fingerprint và destination conflict policy.
- Task: `AUD-D04` — retry riêng Move/Copy/Recycle với confirm, no-overwrite và journal mới.

### P2 — `MainWindow.xaml.cs` còn quá nhiều trách nhiệm

- Evidence: scanner, decode, cache, compare, hash, batch, navigation và file mutation nằm cùng code-behind.
- Impact: khó unit-test và tăng rủi ro thay đổi interaction.
- Task: `AUD-A04` — tách `ImageCatalog`, `ImageLoader`, `DuplicateService`, `ReviewActionExecutor`, `NavigationService`.

### P3 — GUI/accessibility chưa có evidence

- Evidence: đã có accessible names, focusable compare previews và keyboard selection contract; chưa có automated UI/keyboard-only acceptance trên fixture thực tế, chưa kiểm tra DPI/focus indicator/screen reader.
- Impact: build xanh chưa chứng minh UX public-ready.
- Task: `AUD-C04`, `AUD-F03`.

## Không được đánh dấu hoàn tất nếu thiếu

- Benchmark key-to-present và disk-read count.
- Fixture EXIF/sort/compare/hash có manifest.
- Fault-injection matrix và recovery evidence.
- GUI acceptance trên máy sạch hoặc môi trường Windows tương đương.
