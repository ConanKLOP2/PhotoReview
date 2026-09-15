# PhotoReview — Task kéo-thả

| ID | Task | Kết quả/tiêu chí nghiệm thu | Trạng thái |
|---|---|---|---|
| DD-01 | Chốt contract input | Quy định rõ folder, ảnh đơn, nhiều ảnh, input không hợp lệ | PARTIAL |
| DD-02 | Kiểm tra extension/filter hiện có | Có một nguồn sự thật cho extension ảnh; không duplicate logic | DONE |
| DD-03 | Tạo parser/helper kéo-thả | Input được phân loại thuần, có thể test không cần UI | DONE |
| DD-04 | Thêm WPF drag events | `AllowDrop`, `DragOver`, `Drop`; không ảnh hưởng zoom/compare | PARTIAL |
| DD-05 | Nối parser với `LoadFolderAsync` | Folder mở đúng; ảnh mở parent và chọn đúng path | PARTIAL |
| DD-06 | Xử lý lỗi và cancellation | Access denied/path mất/đang loading không crash, không ghi đè session mới | PARTIAL |
| DD-07 | Thêm logging và UX feedback | Status/tooltip/log thể hiện input hợp lệ, đang mở và lỗi | PARTIAL |
| DD-08 | Viết unit/integration tests | Bao phủ parser, filter, multi-input, invalid path và initial image | PARTIAL |
| DD-09 | Manual test Windows Explorer | Test path Unicode, folder lớn, nhiều ảnh, file không hỗ trợ | TODO |
| DD-10 | Build và regression verification | `dotnet build`, test hiện có và kiểm tra review/cache/preload đều pass | PARTIAL |
| DD-11 | Cập nhật README/release notes | Có hướng dẫn kéo-thả và ghi chú phiên bản | PARTIAL |

Đối chiếu 2026-09-15: registry `ImageFileTypes` và parser đã có. Các test mặc định chỉ bao phủ ca parser cơ bản và wiring; manual Explorer, multi-input, Unicode, cancellation/race và release notes chưa đủ evidence. Vì vậy trạng thái là 2 DONE, 8 PARTIAL, 1 TODO; không dùng tổng 11/11 trước đây làm release gate.

## Chi tiết thực hiện

### DD-01 — Contract

- Xác định thứ tự ưu tiên: folder trước, sau đó file ảnh.
- Quy định input nhiều folder và nhiều parent.
- Quy định không thay đổi session khi toàn bộ input không hợp lệ.
- Chốt message tiếng Việt cho từng lỗi.

### DD-02 — Filter

- Tìm service đang enumerate ảnh.
- Expose predicate/filter dùng chung nếu cần.
- Bảo đảm extension không phân biệt hoa thường.
- Thêm case file không có extension và file ẩn.

### DD-03 — Parser

- Nhận `IReadOnlyList<string>`.
- Trả về loại input, folder, initial image, invalid count và warning.
- Không chạm filesystem ngoài kiểm tra tối thiểu cần thiết.
- Không throw với path malformed; trả kết quả invalid.

### DD-04/DD-05 — UI/use case

- Chỉ set `Effects = Copy` hoặc `None` phù hợp vì đây là thao tác mở.
- Gọi cùng pipeline mở folder hiện tại.
- Bảo đảm initial image được match theo full path, case-insensitive.
- Không tạo pipeline decode riêng.

### DD-06/DD-07 — Robustness

- Kiểm tra token trước/sau scan.
- Log start/success/failure.
- Giữ ảnh/session cũ khi load mới thất bại.
- Không hiển thị exception kỹ thuật dài cho người dùng.

### DD-08/DD-09/DD-10/DD-11 — Verification

- Viết test trước cho parser.
- Chạy build/test và ghi kết quả vào progress.
- Cập nhật README bằng hướng dẫn ngắn.
- Chỉ đánh dấu DONE khi có bằng chứng lệnh/test hoặc manual checklist.
