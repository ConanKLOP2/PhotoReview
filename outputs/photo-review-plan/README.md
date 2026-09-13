# Photo Review — Bộ kế hoạch và theo dõi tiến độ

Ngày lập: 2026-09-13. Ngôn ngữ tài liệu: tiếng Việt.
Trạng thái: solution/prototype M1 đang triển khai; workflow đã chốt là mũi tên để nguyên loại 1, Enter Move loại 2, Delete Recycle Bin.
Phạm vi yêu cầu hiện tại: tạo tài liệu và task, không thay đổi ảnh người dùng.

## Bắt đầu ở đâu

1. Mở [bảng tiến độ](PROGRESS.md) để xem trạng thái thực tế.
2. Mở [backlog](TASKS.md) để nhận task và cập nhật kết quả.
3. Đọc [đặc tả](01-PRODUCT.md), [UX](02-UX.md) và [kiến trúc](03-ARCHITECTURE.md).
4. Thực hiện khảo sát và prototype theo [hiệu năng](04-PERFORMANCE.md).
5. Chỉ cho phép thao tác file thật sau khi đạt [an toàn dữ liệu](05-DATA-SAFETY.md) và [kiểm thử](06-TESTING.md).

## Danh mục

| File | Nội dung |
|---|---|
| 01-PRODUCT.md | Mục tiêu, phạm vi, yêu cầu và tiêu chí nghiệm thu |
| 02-UX.md | Luồng sử dụng, phím tắt, trạng thái giao diện |
| 03-ARCHITECTURE.md | Thành phần, dữ liệu, cache, concurrency |
| 04-PERFORMANCE.md | Bộ mẫu, cách benchmark và các cổng quyết định |
| 05-DATA-SAFETY.md | Copy/Move, Undo, phục hồi và xung đột |
| 06-TESTING.md | Ma trận test, dữ liệu mẫu và release gate |
| 07-ROADMAP.md | Milestone, phạm vi bản đầu và mở rộng |
| 08-DECISIONS-RISKS.md | Quyết định, giả định, câu hỏi, rủi ro |
| TASKS.md | Nguồn sự thật về task và trạng thái |
| PROGRESS.md | Tổng hợp milestone và bước tiếp theo |
| TEMPLATES.md | Mẫu báo cáo công việc, lỗi, benchmark, release |

## Quy tắc theo dõi

- TASKS.md là nguồn sự thật; PROGRESS.md chỉ tổng hợp.
- Trạng thái hợp lệ: TODO, IN_PROGRESS, BLOCKED, DONE, DEFERRED.
- DONE chỉ khi tiêu chí hoàn thành đã đạt và có bằng chứng.
- Chỉ đổi sang IN_PROGRESS khi thực sự bắt đầu làm.
- BLOCKED phải ghi lý do và điều kiện gỡ chặn; phụ thuộc chưa xong không tự động là BLOCKED.
- Mỗi task đang làm cần người phụ trách, ngày cập nhật và link bằng chứng.
- Không tính task DEFERRED vào tiến độ bản đầu.
- Mọi thay đổi phạm vi phải ghi vào nhật ký quyết định.
- Không dùng số giờ đã bỏ ra để suy ra % hoàn thành.
- Các ngưỡng hiệu năng hiện là mục tiêu thử nghiệm, không phải kết quả đã đo.
