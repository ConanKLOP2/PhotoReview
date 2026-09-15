# Plan tối ưu Explorer order — ưu tiên ảnh đang xem

Cập nhật: 2026-09-15

## Mục tiêu

Giảm thời gian từ lúc mở folder đến khi viewer phản hồi, không để việc đọc thứ tự Explorer của toàn bộ folder làm chậm navigation hoặc reindex giữa lúc người dùng đang duyệt.

## Hiện trạng và nguyên nhân

- `LoadFolderAsync` scan và sort fallback trước, đồng thời khởi chạy `ExplorerOrderService`.
- `QueryShell` duyệt từng item bằng `IFolderView2.GetItem(index)` rồi `IShellItem.GetDisplayName`.
- Snapshot chỉ được áp dụng sau khi đã lấy đủ toàn bộ path và validator xác nhận đủ tập file.
- Với folder lớn, hàng trăm COM call chạy tuần tự; kết quả về trễ có thể thay đổi index sau khi người dùng đã Next.

## Nguyên tắc thiết kế mới

1. Ảnh đầu tiên và navigation dùng fallback catalog ngay; không chờ Explorer.
2. Không để snapshot về trễ thay đổi thứ tự sau khi người dùng đã bắt đầu duyệt, trừ khi catalog chưa phát sinh tương tác.
3. Nếu cần Explorer order, lấy theo pha: xác định current item trước, sau đó thu thập các item lân cận, cuối cùng mới hoàn thiện toàn bộ thứ tự.
4. Mọi kết quả nền phải gắn `folderGeneration` và `catalogInteractionGeneration`; kết quả cũ bị bỏ qua.
5. Không đọc metadata/pixel của toàn folder trên đường critical path.
6. Giữ validator chống thiếu/trùng/out-of-folder; không đánh đổi an toàn catalog để lấy tốc độ.

## Thiết kế pipeline đề xuất

### Pha A — first visible

- Scan extension và tạo fallback sort.
- Present ảnh mở vào hoặc ảnh đầu tiên ngay.
- Ghi `LoadFolder scan`, `first visible` và kích thước catalog.

### Pha B — probe Explorer có mục tiêu

- Tìm cửa sổ Explorer khớp folder.
- Đọc số item và sort/group metadata.
- Nếu không có cửa sổ khớp, kết thúc với fallback.
- Nếu có, ưu tiên lấy vị trí của `initialPath`/current path để ánh xạ đúng ảnh đang xem.

### Pha C — progressive order

- Đọc item theo batch nhỏ (mặc định 16 hoặc 32), yield giữa các batch.
- Mỗi batch kiểm tra cancellation và generation.
- Không thay `_files` ngay khi mới có một phần; giữ catalog fallback cho đến khi snapshot hợp lệ hoàn chỉnh.
- Nếu người dùng đã Next/Previous/Move/Delete/Copy, không reindex catalog hiện tại; chỉ lưu snapshot cho lần mở folder sau.

### Pha D — hoàn tất và cache

- Cache order theo canonical folder + fingerprint tập path + sort/group signature.
- Invalidate khi số file/path thay đổi hoặc folder generation đổi.
- Ghi timing từng pha và số COM call.

## Danh sách task

| ID | Task | Trạng thái | Evidence cần có |
|---|---|---|---|
| EO-01 | Thêm metric timing Explorer query và số COM item calls | DONE | `ExplorerOrderService` ghi mốc query/native read, item calls, sort/group và path đầu/cuối |
| EO-02 | Thêm `catalogInteractionGeneration` cho navigation/file action | DONE | `MainWindow` tăng generation khi First/Next/Previous/Skip và trước file action; contract test xác nhận snapshot stale bị bỏ qua |
| EO-03 | Tách Explorer query thành pha probe/current-item | DONE | `TryGetSnapshotProgressiveAsync` nhận progress/cancellation; probe metadata/count trước enumeration |
| EO-04 | Đọc Explorer item theo batch có yield/cancel | DONE | batch mặc định 16, `Thread.Yield()` giữa batch, kiểm tra cancellation từng item; Release build PASS |
| EO-05 | Chặn reindex sau khi người dùng đã tương tác | DONE | `LoadFolderAsync` đối chiếu interaction generation trước khi áp dụng snapshot; log `Explorer native order ignored after catalog interaction`; contract test |
| EO-06 | Cache Explorer order có fingerprint/invalidation | TODO | test cache hit/stale invalidation |
| EO-07 | Bổ sung log path đầu/cuối và native index để debug thứ tự | DONE | log query ghi path đầu/cuối, native index và progress |
| EO-08 | Test sequence Next/Move/Copy/Delete xen kẽ khi Explorer query chạy | DONE | `PhotoReview.Tests` sequence test kiểm tra không skip/double-next và catalog/file state |
| EO-09 | Benchmark folder 100/500/1000 ảnh và P50/P95 | TODO | CSV/JSON benchmark |
| EO-10 | Cập nhật tài liệu cơ chế và task status | TODO | APP-MECHANISMS + TASKS đồng bộ |
| EO-11 | Release test, publish và push | TODO | test PASS, publish path, commit/push |

## Tiêu chí hoàn thành

- First visible không phụ thuộc thời gian full Explorer enumeration.
- Sau lần tương tác đầu tiên, snapshot Explorer về trễ không làm đổi thứ tự/index hiện tại.
- Không còn log `ShowImage failed` do stale path trong sequence test.
- Có số đo trước/sau cho Explorer query và navigation latency.
- Test Release và publish bắt buộc PASS.
