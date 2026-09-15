# PhotoReview — Audit report hiện hành

Cập nhật nguồn findings: 2026-09-15. Evidence SHA-256 cũ trong các đoạn 2026-09-14 là lịch sử; findings superseded bởi ngày mới nếu mâu thuẫn. Đối chiếu từng function và task xem [FUNCTION-AUDIT-PLAN.md](FUNCTION-AUDIT-PLAN.md).

## Evidence đã chạy

- Debug build: PASS, 0 warning/0 error.
- Warning-as-error build riêng cho App và Tests: PASS, 0 warning/0 error.
- Release build solution: PASS, 0 warning/0 error.
- Contract executable tests: PASS.
- File-operation smoke test: PASS.
- Framework-dependent `win-x64` publish và `verify-all.ps1`: PASS.
- Framework-dependent và self-contained win-x64 phiên bản 1.0.1 đã publish; verifier kiểm tra FileVersion khớp `.csproj`. Release gate PASS: EXE 162,304 bytes, SHA-256 `C80E9B23F638F4FDFD69CC03B8AEE09DF213981B5BD49683EBCA144EAA7344DB`.
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

### P2 — Compare hash/size có thể cấu hình độc lập

- Evidence: `CompareHashEnabled` và `CompareSizeEnabled` được lưu trong Settings; runtime chỉ đọc hash/kích thước khi option bật, contract tests xác nhận (`1150f88`, `0807efd`).
- Impact: workflow review nhanh có thể tắt các phép đọc phụ; mặc định vẫn bật để giữ thông tin kiểm tra duplicate.

### P2 — Recovery retry đã có core safety và UI

- Evidence: `RecoveryRetryService.RetryMoveOrCopy` kiểm tra source fingerprint, từ chối đích đã tồn tại, journal từng transition và không retry Recycle Bin (`c130ac6`); contract tests PASS.
- Evidence bổ sung: Recovery UI có chọn entry, nút Retry Move/Copy, confirm và hiển thị kết quả (`8532f0f`).
- Remaining: cần GUI acceptance thực tế trên Windows để xác nhận focus, dialog và thao tác end-to-end.
- Release evidence: artifact đã publish lại sau UX/accessibility fix, SHA256 `8CE35B7CA26ABB22536B8690E14601A2F5D9471505AA99CA7B077966C8C2EEA7`.

### P1 — Name ordering aligned with Windows Explorer logical sort

- Evidence: `ImageSortService` dùng `StrCmpLogicalW`, fallback natural sort khi API không khả dụng; contract test xác nhận (`86e50ee`).
- Scope: áp dụng cho Name sort và tie-breaker của PortraitFirst; không làm thay đổi ưu tiên dọc/ngang đã cấu hình.

### P2 — Hash cache đã được giới hạn; cần bổ sung quota/telemetry nếu public quy mô lớn

- Evidence: `_hashCache` hiện là `BoundedLruCache` LRU 16 MB, clear khi đổi folder và kiểm tra lại length/last-write trước khi dùng.
- Impact: memory growth không còn vô hạn; quota cấu hình và hit/eviction telemetry vẫn là cải tiến sau nếu public quy mô lớn.
- Task: `AUD-D02`, `AUD-E04` — core eviction/invalidation đã hoàn tất; quota cấu hình/telemetry nâng cao còn mở.

### Ghi chú về các catch có chủ đích

- JSONL journal bỏ qua từng dòng hỏng để không làm mất khả năng đọc các entry hợp lệ còn lại.
- Image sort coi EXIF không đọc được là square/unknown và tiếp tục mở folder.
- Disk preview cache hỏng được xóa và fallback về source; lỗi ghi cache không chặn việc review ảnh.

### P2 — Recovery UI chỉ xem, chưa có thao tác retry có kiểm soát

> Finding này đã được xử lý một phần bởi `RecoveryRetryService` và nút Retry Move/Copy; phần chưa có evidence là GUI/fault acceptance. Không đọc tiêu đề lịch sử như mô tả runtime 2026-09-15.

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
## Explorer sort-state parity (2026-09-14)

PhotoReview now queries the matching Explorer window through `IServiceProvider`/`IShellBrowser` and reads item order, sort columns, and group state from native `IFolderView2`. Direct COM vtable calls avoid the unregistered Shell type-library failure seen with RCW projection. Runtime evidence on `Machi馬吉 - Hot Springs` confirms Name DESC `(37).jpg` through `(1).jpg`. Snapshot validation rejects missing, duplicate, and out-of-folder results; configured sort remains the fallback. Date/Size and grouped-view cases still require the remaining native integration matrix before claiming complete parity.

## Audit continuation (2026-09-15)

- Repository cleanup: added `/work/` to `.gitignore` and removed seven tracked .NET runtime/template cache files. `outputs/copy-cosplaytele.bat` was not present or tracked in the inspected checkout, so no unrelated deletion was performed.
- Extension consistency: added `ImageFileTypes` as the single supported-image registry and routed folder scan, drag-drop, and sibling-folder discovery through it.
- Journal memory behavior: `OperationJournal` now parses JSONL incrementally with `StreamReader` instead of materializing the complete file via `File.ReadAllLines`.
- Recovery correctness: Delete now writes journal type `Recycle`, matching reconciliation; configured Move/Copy actions now write `Prepared`, verify destination size, and write `Committed` or `Failed` entries with source/destination metadata.
- Verification after these changes: Release contract tests PASS, solution/build gates PASS with 0 warnings and 0 errors, required framework-dependent publish PASS at `PhotoReview.App/bin/Release/net10.0-windows/publish`, and release-file verification PASS.

Remaining items are intentionally not marked complete without stronger evidence: GUI acceptance, key-to-present and disk-I/O benchmarks, native Explorer Date/Size/group matrix, and real filesystem fault-injection across crash boundaries.

## Audit implementation 1 (2026-09-15)

- Fixed shared-work lifecycle in FileHashService and ThumbnailCache: a canceled waiter no longer removes work still used by other callers; clear/dispose generations prevent stale cache refill.
- Fixed SessionStore concurrent temp collisions, canonicalized equivalent folder keys, corrected natural ordering for numeric runs over 12 digits, comparer hash consistency, compare pairing scope, and drag-drop ignored counts.
- Added MainWindow scan cancellation input, stale-load catch guard, empty-folder folder/Undo key handling, immediate Skip persistence, and rejection of unknown action operations.
- Added regression checks for compare pairing in the selected folder and long numeric filenames. Release contract runner PASS.
