# Photo Review — Bộ kế hoạch và theo dõi tiến độ

Trạng thái hiện hành tại `c87d14a`: app WPF 1.0.1 đã có viewer, action profiles, Compare, cache, native Explorer query, drag-drop và Recovery UI; đợt audit function đầu tiên đã triển khai một phần. [CURRENT-STATE.md](CURRENT-STATE.md) là snapshot runtime; [FUNCTION-AUDIT-PLAN.md](FUNCTION-AUDIT-PLAN.md) là plan tối ưu từng function; [APP-MECHANISMS-VI.md](../APP-MECHANISMS-VI.md) là mô tả cơ chế và checklist preview.

Ngày lập: 2026-09-13. Ngôn ngữ tài liệu: tiếng Việt.
Snapshot ban đầu 2026-09-13 là prototype M1. Hiện source đã có thêm Compare/action/cache/native Explorer; phạm vi audit này cập nhật tài liệu và plan, không thao tác ảnh người dùng.

## Bắt đầu ở đâu

1. Mở [trạng thái hiện hành](CURRENT-STATE.md) và [plan function](FUNCTION-AUDIT-PLAN.md).
2. Mở [bảng tiến độ lịch sử](PROGRESS.md) và [backlog PR](TASKS.md) khi cần đối chiếu milestone cũ.
3. Đọc [đặc tả](01-PRODUCT.md), [UX](02-UX.md) và [kiến trúc](03-ARCHITECTURE.md).
4. Thực hiện khảo sát và prototype theo [hiệu năng](04-PERFORMANCE.md).
5. Chỉ cho phép thao tác file thật sau khi đạt [an toàn dữ liệu](05-DATA-SAFETY.md) và [kiểm thử](06-TESTING.md).

## Danh mục

| File | Nội dung |
|---|---|
| 01-PRODUCT.md | Mục tiêu, phạm vi, yêu cầu và tiêu chí nghiệm thu |
| 02-UX.md | Luồng sử dụng, phím tắt, trạng thái giao diện |
| 03-ARCHITECTURE.md | Thành phần, dữ liệu, cache, concurrency |
| IFOLDERVIEW2-IMPLEMENTATION-PLAN.md | Kế hoạch native Explorer order, COM, version và test |
| 04-PERFORMANCE.md | Bộ mẫu, cách benchmark và các cổng quyết định |
| 05-DATA-SAFETY.md | Copy/Move, Undo, phục hồi và xung đột |
| 06-TESTING.md | Ma trận test, dữ liệu mẫu và release gate |
| 07-ROADMAP.md | Milestone, phạm vi bản đầu và mở rộng |
| 08-DECISIONS-RISKS.md | Quyết định, giả định, câu hỏi, rủi ro |
| CURRENT-STATE.md | Snapshot runtime/evidence/gate hiện hành |
| FUNCTION-AUDIT-PLAN.md | Audit function và thứ tự tối ưu FA-00..14 |
| TASKS.md | Backlog PR-xxx lịch sử, cần đồng bộ bảng và block khi triển khai tiếp |
| PROGRESS.md | Nhật ký/milestone lịch sử và bước tiếp theo |
| TEMPLATES.md | Mẫu báo cáo công việc, lỗi, benchmark, release |
| AUDIT-PLAN.md | Kế hoạch audit theo lane, task nhỏ, phụ thuộc và resume |
| AUDIT-REPORT.md | Findings theo severity, evidence và điều kiện không được bỏ qua |
| SHELL-VIEW-ORDER-PLAN.md | Thiết kế Shell Explorer order lịch sử |
| DRAG-DROP-PLAN.md / DRAG-DROP-TASKS.md / DRAG-DROP-PROGRESS.md | Thiết kế và trạng thái kéo-thả |
| ../APP-MECHANISMS-VI.md | Cơ chế runtime hiện hành và checklist preview Windows |

## Quy tắc theo dõi

- CURRENT-STATE.md và FUNCTION-AUDIT-PLAN.md là nguồn audit hiện hành; TASKS.md là backlog PR lịch sử, PROGRESS.md là nhật ký.
- Trạng thái hợp lệ: TODO, PARTIAL, IN_PROGRESS, BLOCKED, DONE, DEFERRED.
- DONE chỉ khi tiêu chí hoàn thành đã đạt và có bằng chứng.
- Chỉ đổi sang IN_PROGRESS khi thực sự bắt đầu làm.
- BLOCKED phải ghi lý do và điều kiện gỡ chặn; phụ thuộc chưa xong không tự động là BLOCKED.
- Mỗi task đang làm cần người phụ trách, ngày cập nhật và link bằng chứng.
- Không tính task DEFERRED vào tiến độ bản đầu.
- Mọi thay đổi phạm vi phải ghi vào nhật ký quyết định.
- Không dùng số giờ đã bỏ ra để suy ra % hoàn thành.
- Các ngưỡng hiệu năng hiện là mục tiêu thử nghiệm, không phải kết quả đã đo.
