---
id: NOWPF-FAULTINJ-JOURNAL
order: 1040
summary: |-
  Bước 1 "chứng minh bằng số" (2026-10-12): ma trận điểm lỗi x thao tác cho Move/Copy/Recycle/Undo/Retry/Dismiss/journal. Ma trận 15 thao tác x 8 loại lỗi: 36 ô đã có từ PR #349, 48 ô LẤP ở PR này, 5 ô còn THIẾU (biến thể cùng mã); phần lấp gồm UnauthorizedAccess, hủy giữa chừng, retry bị ngắt sau crash thật, hai lỗi liên tiếp qua restart, Dismiss, xoá vĩnh viễn không Thùng rác, thư mục đích rỗng, reconcile bị ngắt cho Copy nhóm/Recycle/Undo). Không đổi mã sản phẩm, không tìm thấy lỗi dữ liệu thật. Đột biến: 29 chạy, 24 bị bắt ngay, 1 chỉ bị bộ rộng bắt, 2 tương đương, 2 lỗ hổng test thật đã lấp (retry nhóm Superseded, retry ghi Failed khi Prepared lỗi). Còn mở: 3 quan sát hành vi cần chủ dự án quyết (thư mục đích rỗng sau rollback/crash/undo, mục Delete Failed cũ sau khi Retry Move xong, B-01 cross-volume + journal Fast).
---

# NOWPF-FAULTINJ-JOURNAL - ma trận fault-injection cho Move/Copy/Delete/Undo/journal (2026-10-12)

Không đổi hành vi sản phẩm: chỉ test và seam của test (`tests/PhotoReview.Core.Tests`). Hạ tầng đã có từ #349
(`FaultInjectionFileSystem`, `FaultRig`, `FaultInvariants`, `FaultInjectionMatrix`); xem [TESTING](../../TESTING.md) mục
"Fault-injection". Thiết kế điểm lỗi: mỗi lời gọi GHI của thao tác (mkdir, append journal, move, copy, delete, recycle,
restore, delete-permanent) được đánh số; một `FaultPlan(At, Kind)` chọn lời gọi và kiểu lỗi; crash = mọi lời gọi sau đó
(cả đọc) ném `ProcessCrashedException`, "restart" = journal/service mới trên cùng đĩa giả.

## Điểm lỗi cố định được ánh xạ thế nào

| Điểm yêu cầu | Lời gọi đánh số tương ứng | Ghi chú |
|---|---|---|
| Trước khi ghi Begin (Prepared) | `mkdir` thư mục đích, `mkdir` thư mục journal | `CrashBefore`/`FailOnce`/`FailUnauthorized` |
| Sau Begin, trước thao tác FS | `append` Prepared, kiểu `CrashAfter` hoặc `CrashBefore` của lời gọi kế | tương đương trạng thái đĩa |
| Giữa copy và xoá nguồn (khác ổ) | `move` với `CrashMoveCopiedNotDeleted` | chỉ Move |
| Sau thao tác FS, trước `MarkMutationCompleted` | `move`/`copy`/`recycle` với `CrashAfter` | |
| Sau `MarkMutationCompleted`, trước Commit | không có lời gọi riêng (cờ trong bộ nhớ) = `CrashBefore` của `append` Commit | trạng thái đĩa y hệt |
| Đang ghi Commit (nửa dòng) | `append` với `CrashPartial` / `FailPartial` (TornStream) | tail rách, dòng sau phải xuống dòng mới |
| Restart ở mỗi điểm | `Restart()` + `ReconcilePendingOperations()` rồi `VerifyAsync` | luôn chạy; thêm reconcile bị ngắt, retry bị ngắt |

Invariant kiểm sau mỗi lần (`FaultInvariants`, `FaultInjectionMatrix.VerifyAsync`): không mất file (nội dung duy nhất cho từng ảnh),
không ghi đè, mọi phán quyết journal đúng với đĩa, không còn Prepared sau reconcile, reconcile idempotent, mọi tệp lạ đều có
dòng journal, Retry mọi mục Recovery an toàn, Move một ảnh khác cùng tên không ghi đè. Thêm trong PR này: xoá vĩnh viễn
(`FaultBin.VolumeHasBin=false`) là mất mát DUY NHẤT được phép và chỉ trong phạm vi hành động; thư mục đích có sẵn không bao giờ bị xoá.

## Ma trận (cột = loại lỗi, hàng = thao tác)

Ký hiệu: `có` = đã có trước PR này (#349/CrashMatrixTests); `LẤP` = thêm ở PR này; `n/a` = không áp dụng (lý do bên dưới);
`THIẾU` = còn mở.
Cột: C1 crash trước/sau mỗi lời gọi (gồm khác-ổ), C2 rách/nửa chừng (journal, copy cụt), C3 IOException tạm thời,
C4 UnauthorizedAccess tạm thời, C5 hủy (CancellationToken) sau mỗi lời gọi, C6 reconcile khởi động bị ngắt,
C7 retry Recovery bị ngắt sau crash THẬT, C8 restart rồi làm lại (hai lỗi liên tiếp).

| Thao tác | C1 | C2 | C3 | C4 | C5 | C6 | C7 | C8 |
|---|---|---|---|---|---|---|---|---|
| Move đơn | có | có | có | LẤP | LẤP | có | LẤP | LẤP |
| Move nhóm (JPEG+RAW+XMP) | có | có | có | LẤP | LẤP | có | LẤP | LẤP |
| Copy đơn | có | có | có | LẤP | LẤP | có | LẤP | LẤP |
| Copy nhóm | có | có | có | LẤP | LẤP | LẤP | LẤP | THIẾU (cùng mã với Move nhóm) |
| Recycle đơn (bin giả) | có | n/a (không có bước nửa chừng) | có | LẤP | LẤP | LẤP | n/a (Retry chỉ Move/Copy) | LẤP |
| Recycle nhóm | có | n/a | có | LẤP | LẤP | có | LẤP | LẤP (sau Move nhóm) |
| Xoá vĩnh viễn đơn (không bin, opt-in) | LẤP | n/a | LẤP | LẤP | LẤP | THIẾU (nhóm đã có, cùng mã) | n/a | THIẾU |
| Xoá vĩnh viễn nhóm | LẤP | n/a | LẤP | LẤP | LẤP | LẤP | n/a | THIẾU |
| Từ chối xoá vĩnh viễn khi chưa opt-in | LẤP (không có lời gọi ghi nào) | | | | | | | |
| Undo Move đơn | có | có | có | LẤP | n/a (Undo không nhận token) | LẤP | LẤP | n/a (Undo chỉ trong phiên) |
| Undo Move nhóm | có | có | có | LẤP | n/a | LẤP | LẤP | n/a |
| Undo Recycle đơn (restore từ bin) | có | n/a | có | LẤP | n/a | n/a (khôi phục không ghi journal, đã biết) | n/a | n/a |
| Undo Recycle nhóm | có | n/a | có | LẤP | n/a | LẤP | n/a (Retry chỉ Move/Copy) | n/a |
| Retry Recovery Move/Copy/nhóm (Failed tổng hợp) | có | có | có | LẤP | LẤP | n/a | n/a | n/a |
| Dismiss mục Recovery | LẤP | LẤP (dòng rách) | LẤP | LẤP | n/a | n/a | n/a | n/a |
| Nén journal (`TryCompact`) | có (`JournalCompactionFaultInjectionTests`) | có | có | THIẾU (chỉ IOException) | n/a | n/a | n/a | n/a |

Đếm (ô áp dụng, không tính n/a và hàng "từ chối"): 36 ô có sẵn, 48 ô LẤP, 5 ô THIẾU (Copy nhóm C8, xoá vĩnh viễn đơn C6 và C8,
xoá vĩnh viễn nhóm C8, nén journal C4: đều là biến thể của mã đã phủ ở hàng liền kề, ghi để khỏi bỏ sót). `FaultInjection*` +
`CrashMatrix`: 75 test = hàng nghìn lần bơm lỗi; `FileActionFaultInjectionGapTests` (45 test) lặp 100 lần liên tiếp không lần nào đỏ.

Cột C5/C7/C8 là "ma trận bậc hai": `FaultInjectionSecondOrder.AssertRetryInterruptedAsync` (crash thật ở mỗi lời gọi x kiểu crash,
restart + reconcile, rồi Retry mọi mục Failed bị ngắt ở mỗi lời gọi của chính nó x 6 kiểu lỗi, restart + kiểm invariant) và
`AssertDoubleFaultAsync` (crash, restart, người dùng làm lại cùng/khác hành động và hành động đó bị lỗi ở mỗi lời gọi x mọi kiểu).

## Cách thêm một điểm lỗi / kịch bản mới

1. Điểm lỗi mới trong IO: nếu là lời gọi ghi của `IFileSystem`/bin, nó đã tự được đánh số (`Mutate`). Với một dịch vụ ngoài (như bin
   giả), gọi `fs.Mutate("tên", effect, partial?)` trong fake; thêm tên đó vào `FaultInjectionFileSystem.Applies` nếu cần quy tắc riêng.
2. Kiểu lỗi mới: thêm một giá trị `FaultKind` và một `case` trong `FaultInjectionFileSystem.Mutate`; nó tự vào mọi ma trận mặc
   định (`FaultInjectionMatrix.Kinds`, trừ `CancelAfter` vốn phải chọn tường minh qua `kinds:`).
3. Kịch bản mới: một lời gọi `FaultInjectionMatrix.AssertAllFaultPointsAsync(name, create, act, extraCheck?, kinds?)`;
   `create` dựng rig + phần lành của kịch bản, `act` là phần được đánh số. Bậc hai: `FaultInjectionSecondOrder`.
4. Quy tắc: không `Thread.Sleep`/`Task.Delay`, không phụ thuộc thứ tự luồng (mọi thứ đánh số theo lời gọi, tuần tự), không đụng
   Thùng rác thật (luôn `FaultBin`).
5. Một ô `THIẾU` phải được ghi vào bảng trên; một vi phạm invariant báo kèm đúng điểm bơm (`kind at call #n/total = mutation`).

## Đột biến (quy tắc hai bước, gói an toàn dữ liệu dùng bộ rộng cho mọi mẫu sống sót)

29 đột biến hợp lệ (1 không biên dịch đã thay), trong `FileActionService` (dọn thư mục rỗng, rolledBack/Dismissed, bù Copy/Move,
kiểm "nguồn vẫn còn", hủy trong vòng member, VerifyGroupMove), `JournalTransaction` (Commit/Fail/Dismiss, live marker),
`UndoService` (chống ghi đè, R18, Register, nhóm), `RecoveryRetryService` (đổi nguồn, Superseded), `OperationJournal`
(Dismiss `current == entry`, đuôi rách, latest-wins, Reconcile).

| Kết quả | Số | Chi tiết |
|---|---|---|
| Bị bắt ở bước 1 (namespace FileActions) | 24 | F01-F04, F09-F13b, F15, J01, J04, U01-U03, U05, U06, R01, R03, R05b, O02, O03, O05, O06 |
| Chỉ bị bắt ở bước 2 (bộ rộng) | 1 | O01 (Dismiss bỏ `current == entry`): `Dismiss_StaleSnapshotRacedByAConcurrentRetry_...` |
| Tương đương | 2 | F05 (`DestinationExists:false` đã bao hàm bởi `Conflict`), F17 (`copiedStat != null` kéo theo `DestinationCreated`) |
| LỖ HỔNG THẬT, đã lấp | 2 | R05: nhánh Superseded của retry NHÓM (`RecoveryRetryRaceTests` "P02 (group)"); J02: retry vẫn ghi Failed khi Prepared lỗi (`JournalTransactionTests.Fail_RetryWhosePreparedAppendThrew_StillJournalsTheFailure`) |

Cả R05 và J02 được chạy lại sau khi thêm test: bị bắt. F17 từng bị bắt một lần bởi một test thời gian ở xa
("Flush does not wait unboundedly...") ở bộ rộng; chạy lại thì sống sót: coi là tương đương, không tính là bị bắt.

## Quan sát cần chủ dự án quyết (KHÔNG tự đổi; ghi CHƯA XÁC NHẬN)

1. **Thư mục đích rỗng còn lại.** Mã chỉ dọn thư mục do lời gọi tạo khi lỗi xảy ra TRƯỚC dòng Prepared (có test khoá). Sau
   rollback đầy đủ (Prepared -> Dismissed), sau crash, và sau Undo Move, thư mục đích rỗng (do lần chạy tạo) ở lại. Không mất dữ
   liệu. Đề xuất: giữ nguyên (một thư mục rỗng vô hại, và dọn sau Prepared cần chứng minh "do ta tạo" qua restart);
   nếu muốn dọn, chỉ cho nhánh rollback đầy đủ trong cùng tiến trình.
2. **Mục Delete Failed cũ sau khi Retry Move nhóm hoàn tất.** Capture bị Move nhóm dở, người dùng Delete cả nhóm (lỗi), rồi Retry Move
   xong: mục Delete còn là Failed dù ảnh nằm ở thư mục đích. Không mất dữ liệu; Retry của Delete bị từ chối ("chỉ Move/Copy"), Dismiss
   xử lý. Invariant của test đếm đích của Move khác là "một chỗ".
3. **B-01 (cross-volume + journal Fast, handoff 4B)**: vẫn là cam kết process-crash, không power-loss (ADR 0007). Ma trận chứng minh
   phần process-crash (`CrashMoveCopiedNotDeleted`, `CrashPartial`); power-loss cần quyết định sản phẩm.

Lệch/giới hạn: mô hình đĩa là bộ nhớ (không thứ tự ghi/flush của OS thật); `CrashAfter` của `append` không sinh trạng thái mới
(bằng `CrashBefore` của lời gọi kế). Không dùng Thùng rác thật; `Category=Native` vẫn ngoài CI.
