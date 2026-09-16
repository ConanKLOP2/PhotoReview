# Tiến độ

- **Cập nhật:** 2026-09-16 | **Branch:** `refactor/plan-b-c3` (tách từ `docs/refactor-plan` @ `629b8f2`)
- **Mục tiêu:** chốt hướng tái cấu trúc **B + C3** (tách lớp + MVVM, thay decoder sau `IImageDecoder`) và chia thành task nhỏ để model khác thực hiện.
- **Quyết định người dùng:** chọn B + C3 (2026-09-16). Các câu hỏi Q2–Q8 (plan mục 11) chưa có câu trả lời; plan đã ghi mặc định đề xuất.
- **Đã sửa:**
  - `docs/refactoring/REFACTOR-PLAN.md`: so sánh các hướng, ràng buộc K-1…K-3, kiến trúc decoder, cổng chất lượng C3, INV-12, P14 (EXIF orientation, scaling).
  - `docs/refactoring/REFACTOR-TASKS.md`: 82 task nhỏ (T00–T88), hướng dẫn cho model thực thi, alias đường dẫn, mẫu nhật ký, nhánh WC3 chạy song song với W4. Thứ tự phụ thuộc đã kiểm bằng script.
- **Chẩn đoán hiệu năng (bổ sung 2026-09-16):**
  - `docs/refactoring/PERF-DIAGNOSIS-PLAN.md`: mô hình thời gian key→present, giả thuyết H1–H15, kịch bản S1–S10, instrumentation dùng EventSource + biến môi trường, quy tắc quyết định R-IO/R-DEC/R-UI/R-PRE…
  - `PERF-DIAGNOSIS-TASKS.md`: D00–D13, chạy sau T00 và trước T14a.
  - Thêm ràng buộc K-4 (giữ event đo khi refactor). T01 được thay bằng D07 + D12.
- **Không đổi:** source, cấu hình, dữ liệu.
- **Còn lại:** mọi task ở trạng thái `TODO`. Bước tiếp theo là T00 (Coordinator): tag baseline, tạo `refactor/integration` từ branch này, ghi câu trả lời Q2–Q8.
- **Cho AI tiếp theo:** đọc `REFACTOR-TASKS.md` mục 0 trước khi nhận task. Mỗi phiên chỉ làm một task, chỉ sửa file có trong danh sách, không push `master`. Lỗi flaky quota prune nền (189/190) đã giao cho T13b.
