# PhotoReview — Kế hoạch audit và thực thi theo lane

Cập nhật: 2026-09-14. Đây là kế hoạch điều phối cho các phiên làm việc tiếp theo. `TASKS.md` vẫn là backlog nền; file này là checklist audit hiện hành.

## Nguyên tắc điều phối

- Mỗi task có ID, phạm vi, phụ thuộc, tiêu chí hoàn thành và bằng chứng.
- Các lane chỉ sửa file thuộc phạm vi của mình; nếu chạm cùng file phải nối tiếp, không ghi đè.
- Chỉ đánh dấu DONE sau khi có test/build hoặc bằng chứng kiểm tra tương ứng.
- Mỗi phiên kết thúc phải cập nhật trạng thái, commit liên quan, blocker và bước resume tiếp theo.

## Lane và task nhỏ

### Lane A — Build, kiến trúc và code quality

- `AUD-A01` Inventory toàn bộ source, dependency và entry point. DONE khi có sơ đồ module và danh sách file đã đọc.
- `AUD-A02` Build Debug/Release, bật kiểm tra warning và rà soát lỗi compile/XAML. Phụ thuộc A01.
- `AUD-A03` Rà soát async/event ordering, cancellation và stale frame. Phụ thuộc A02.
- `AUD-A04` Tách logic khỏi `MainWindow.xaml.cs` thành services/testable components. Phụ thuộc A03.

### Lane B — Settings và action profiles

- `AUD-B01` Kiểm kê schema config, migration và giá trị mặc định.
- `AUD-B02` Hoàn thiện editor action: add/edit/remove, Move/Copy/Recycle, destination, confirm.
- `AUD-B03` Shortcut recorder, conflict detection, reset/import/export JSON.
- `AUD-B04` Validate destination: không trỏ vào source, kiểm tra quyền ghi, atomic save.

### Lane C — Viewer, compare và UX

- `AUD-C01` Xác minh thứ tự sort: Name và PortraitFirst; test portrait/landscape/square/EXIF orientation.
- `AUD-C02` Compare pair model và navigation; click chọn ảnh, bỏ mouse-next.
- `AUD-C03` Đồng bộ zoom/pan, loading/error state và focus keyboard.
- `AUD-C04` Accessibility, DPI, resize, trạng thái selection và thông báo không chỉ dựa màu.

### Lane D — File safety và duplicate operations

- `AUD-D01` Rà soát Move/Copy/Recycle/Undo và conflict policy.
- `AUD-D02` Hash pipeline: SHA-256, size-first optimization, cancellation và cache hash.
- `AUD-D03` Batch preview/dry-run: danh sách giữ/bỏ, không mutation khi chưa xác nhận.
- `AUD-D04` Batch execution journal, recovery, partial failure và report.

### Lane E — Performance, RAM và I/O

- `AUD-E01` Đo baseline key-to-present, decode, cache hit/miss và disk read.
- `AUD-E02` Kiểm tra RAM budget 16 GB mục tiêu nhưng có giới hạn an toàn, eviction và memory pressure.
- `AUD-E03` Preload scheduler, concurrency, cancellation và fairness cho thao tác hiện tại.
- `AUD-E04` Disk cache versioning, corruption, quota, clear cache và privacy.

### Lane F — Tests và release

- `AUD-F01` Unit tests cho sort, grouping, config migration, shortcut conflicts và hash.
- `AUD-F02` Integration tests cho Move/Copy/Recycle/Undo và batch dry-run.
- `AUD-F03` GUI acceptance bằng keyboard/mouse/compare/resume trên fixture không chứa ảnh người dùng.
- `AUD-F04` Release x64, clean-machine smoke test, installer/association và known issues.

## Thứ tự thực hiện/resume

1. A01 → A02; B01; C01; D01; E01; F01 có thể chạy song song.
2. A03 → A04; B02/B03/B04; C02/C03; D02/D03; E02/E03 thực hiện khi dependency tương ứng hoàn tất.
3. D04, E04, F02/F03 chạy sau khi các contract ổn định.
4. F04 là release gate cuối; không phát hành batch mutation trước khi D04 và F02 đạt.

## Bảng tiến độ hiện tại

| Lane | Đã xác nhận | Đang làm | Tiếp theo | Blocker |
|---|---|---|---|---|
| A | Source inventory; build PASS | A03 | A04 tách service | — |
| B | Config version/migration; action validation/editor/recorder/import-export; destination validation; durable atomic save; runtime shortcut wiring | B04 | Migration failure/backup tests | GUI config failure acceptance còn thiếu |
| C | Name/PortraitFirst, compare cơ bản, EXIF orientation sort | C01 | C02 test pair/GUI | Chưa có fixture EXIF/GUI acceptance |
| D | Move/Copy/Recycle/hash batch; confirm; size-first/hash cache; dry-run; per-file journal/report; pending reconcile; recovery UI | D04 | Fault injection/reconcile edge cases | Fault injection đầy đủ còn thiếu |
| E | LRU/preload/preview cache; 16 GB budget/full-folder threshold; 80% preload pressure guard | E02/E03 | E01 telemetry/benchmark | Chưa có benchmark định lượng/decoded telemetry |
| F | Build/test/smoke/release gate PASS; contract test action/sort/navigation | F01 | F02/F03 GUI acceptance | Cần fixture và test GUI |

## Nhật ký audit

- 2026-09-14 — `AUD-A01/A02`: đọc inventory source/test/tooling; build Debug PASS 0 warning/0 error.
- 2026-09-14 — `AUD-B01/D01`: phát hiện `ReviewAction.Confirm` chưa có hiệu lực và batch chưa hỏi xác nhận; đã sửa và thêm contract test.
- 2026-09-14 — Test cũ về `ClassifyCurrentAsync(2)` đã được cập nhật theo contract action profile, không giữ assertion implementation cũ.
- 2026-09-14 — `AUD-B01/B04`: action profiles được deep-copy khi mở Settings; validation tên/phím/Operation và conflict shortcut đã thêm. `AUD-F01` PASS với build và contract test.
- 2026-09-14 — `AUD-B02`: thêm `ActionProfilesWindow` với add/remove/edit, operation Move/Copy/Recycle/Delete, destination và confirm; build/test PASS.
- 2026-09-14 — `AUD-B03`: shortcut recorder bắt phím trực tiếp; action profile Import/Export JSON; build/test PASS. Commit `b6db27d`.
- 2026-09-14 — `AUD-B04`: Move/Copy action chặn destination rỗng và chính folder nguồn; build/test PASS. Destination relative như `Loai-2` vẫn hợp lệ.
- 2026-09-14 — `AUD-B04`: thêm `ConfigVersion`, migration v1→v2, backup config hỏng và atomic write `WriteThrough`/`Flush(true)`; build/test PASS.
- 2026-09-14 — `AUD-D02`: duplicate scan lọc theo file size trước khi SHA-256 và cache hash theo path/length/last-write; build/test PASS.
- 2026-09-14 — `AUD-D03`: thêm `BatchReviewWindow` hiển thị danh sách file, dung lượng và yêu cầu xác nhận riêng trước Recycle Bin; build/test PASS. Commit `16f7523`.
- 2026-09-14 — `AUD-D04`: batch Recycle ghi journal từng file ở trạng thái Prepared/Committed/Failed và hiển thị báo cáo lỗi; build/test PASS.
- 2026-09-14 — `AUD-D04`: thêm `ReconcilePendingOperations`; pending Recycle không replay mù, source còn ghi Failed, source mất ghi Committed; build/test PASS.
- 2026-09-14 — `AUD-D04`: reconcile thêm Move/Copy; chỉ ghi Committed khi source mất và destination tồn tại đúng size, trường hợp khác ghi Failed không replay; build/test PASS.
- 2026-09-14 — `AUD-D04`: thêm Recovery UI hiển thị pending operations và cảnh báo không replay tự động; build/test PASS.
- 2026-09-14 — `AUD-D04`: Recovery UI đọc cả pending và failed journal entries; build/test PASS.
- 2026-09-14 — `AUD-F04`: `verify-all.ps1 -Configuration Release` PASS sau restore/publish `win-x64`; framework-dependent EXE verified (162,304 bytes, SHA-256 `22D4D8F414BE3B9685F1F9FF042232DA09334E83C180E18F3937ECAD5C8D01F4`).
- 2026-09-14 — Audit finding release: publish `--no-restore` fail `NETSDK1047` vì project thiếu RuntimeIdentifiers và verifier tiếp tục sau lỗi; thêm `RuntimeIdentifiers=win-x64`, thêm guard fail-fast cho publish, publish/verifier mới PASS (162,304 bytes, SHA-256 `C3F1886E40984A96AB85F9F3822DFCE0C7797E36B9A5C2C217D12D772CF6051F`).
- 2026-09-14 — Audit finding P1: Settings có shortcut NextFolder/PreviousFolder/FirstImage/Zoom nhưng runtime hardcode key; đã nối config vào Window_KeyDown và thêm contract test; build/test PASS.
- 2026-09-14 — `AUD-C01`: PortraitFirst sort tính EXIF orientation 5/6/7/8 trước khi phân loại dọc/ngang; build/test PASS. Fixture ảnh EXIF thật vẫn cần bổ sung.
- 2026-09-14 — `AUD-E02`: cache budget nâng lên 16 GB và folder có tổng source bytes dưới 16 GB được preload toàn bộ, vẫn qua cancellation/LRU; build/test PASS. Cần đo decoded footprint và memory-pressure guard trước release.
- 2026-09-14 — `AUD-E03`: preload nền dừng khi GC memory load đạt 80% available memory; thêm contract test; build/test PASS.

## Template cập nhật task

`ID — STATUS — owner — files — evidence — blocker — resume next step`
