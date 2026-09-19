# Quy tắc ưu tiên bắt buộc cho ứng dụng PhotoReview

## Duy trì trạng thái công việc cho AI tiếp theo

- Duy trì `task_on_progress.md` ở thư mục gốc. Đọc khi bắt đầu; cập nhật sau thay đổi đáng kể và trước khi bàn giao.
- Ghi ngắn gọn: mục tiêu, file đã xem/sửa, thay đổi, vướng mắc, việc còn lại, lưu ý tiếp tục và kết quả kiểm tra. Nêu ngày cập nhật; không biến điều chưa xác minh thành sự thật.

## Quy trình bắt buộc trước khi thay đổi

- Trước mọi thay đổi mã nguồn, cấu hình, giao diện hoặc cấu trúc dữ liệu, phải lập một plan chi tiết để người dùng xác nhận.
- Plan phải nêu rõ: mục tiêu, nguyên nhân/phạm vi, các file hoặc khu vực dự kiến thay đổi, thứ tự thực hiện, tiêu chí hoàn thành, cách kiểm thử, rủi ro và phương án rollback.
- Plan phải có danh sách task đánh số. Mỗi task cần ghi trạng thái `TODO`, `IN PROGRESS`, `BLOCKED` hoặc `DONE` trong quá trình thực hiện.
- Phải đánh dấu task nào có thể chạy độc lập để chia cho multi-agent, nêu rõ đầu ra của từng agent và cách hợp nhất kết quả.
- Không được bắt đầu chỉnh sửa hoặc commit trước khi người dùng xác nhận plan. Các bước đọc code, đọc log, phân tích hiện trạng và chuẩn bị plan được phép thực hiện trước khi xác nhận.
- Nếu phát hiện phạm vi thay đổi khác với plan đã xác nhận, phải cập nhật plan và chờ người dùng xác nhận lại trước khi tiếp tục phần thay đổi mới.

Ứng dụng phải ưu tiên các nguyên tắc sau khi xử lý và review ảnh:

1. **Hạn chế đọc từ đĩa:** Luôn luôn đọc dữ liệu từ đĩa ít nhất có thể. Ưu tiên tái sử dụng dữ liệu đã đọc, cache và các cơ chế đọc tuần tự hiệu quả để tránh I/O dư thừa.
2. **Tận dụng RAM:** Ưu tiên sử dụng nhiều RAM để tối ưu tốc độ. Máy có 32 GB RAM, vì vậy mục tiêu sử dụng tối thiểu là 16 GB khi phù hợp. Nếu tổng dung lượng thư mục dữ liệu nhỏ hơn 16 GB, phải ưu tiên load toàn bộ thư mục lên RAM, miễn là không gây lỗi hoặc ảnh hưởng nghiêm trọng đến hệ thống.
3. **Ưu tiên tốc độ review:** Mọi thiết kế và tối ưu phải ưu tiên tốc độ chuyển ảnh, hiển thị ảnh và thao tác review nhanh.
4. **Ưu tiên chất lượng ảnh:** Khi có thể, bắt buộc load và hiển thị ảnh ở chất lượng tốt nhất có thể, hạn chế giảm chất lượng hoặc dùng ảnh xem trước nếu không cần thiết.

## Quy trình build, publish và push Git bắt buộc

- Sau mỗi thay đổi hoàn thiện và mỗi commit, luôn chạy test/build Release trước khi bàn giao.
- “Public” trong quy trình này nghĩa là push branch lên remote GitHub `origin` và mở Pull Request vào `master`.
- Luôn publish bản kiểm tra vào đúng thư mục mặc định:

  `src/PhotoReview.App/bin/Release/net10.0-windows/publish`

- Lệnh chuẩn:

  `dotnet run --project tools/PhotoReview.Benchmark.Cli -c Release`

  `dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish`

- Không coi công việc là hoàn tất nếu chưa publish thành công vào thư mục trên. Quy trình này áp dụng trên mọi máy làm việc với project.
- Quy trình bắt buộc sau test/publish thành công:
  1. Làm việc trên feature branch (không commit thẳng `master`).
  2. Push branch hiện tại lên remote: `git push origin <branch-name>`.
  3. Mở Pull Request vào `master` trên GitHub.
  4. Chỉ merge khi CI (`.github/workflows/ci.yml`) chạy xanh và người dùng duyệt.
- Lệnh và đường dẫn mặc định luôn đồng bộ với `README.md`.
- Không coi công việc là hoàn tất nếu chưa kiểm tra push branch thành công (trừ khi remote từ chối hoặc thiếu quyền, khi đó phải báo rõ lỗi).

## Đợt tái cấu trúc đang chạy

Agent nhận task phải đọc trước:
- **Kế hoạch tái cấu trúc:** `docs/refactoring/REFACTOR-PLAN.md`, `docs/refactoring/REFACTOR-TASKS.md`.
- **Chẩn đoán hiệu năng:** `docs/refactoring/PERF-DIAGNOSIS-PLAN.md`, `docs/refactoring/PERF-DIAGNOSIS-TASKS.md`.

Mỗi phiên: chọn **một** task từ danh sách (xem mục 0 của `REFACTOR-TASKS.md`), sửa **chỉ** file trong danh sách **Files** của task, không push `master`. Coordinator sẽ merge vào `refactor/integration` và kiểm tra toàn bộ.
