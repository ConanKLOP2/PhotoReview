# Quy tắc ưu tiên bắt buộc cho ứng dụng PhotoReview

## Duy trì trạng thái công việc cho AI tiếp theo

- Duy trì `task_on_progress.md` ở thư mục gốc. Đọc khi bắt đầu; cập nhật sau thay đổi đáng kể và trước khi bàn giao.
- Ghi ngắn gọn: mục tiêu, file đã xem/sửa, thay đổi, vướng mắc, việc còn lại, lưu ý tiếp tục và kết quả kiểm tra. Nêu ngày cập nhật; không biến điều chưa xác minh thành sự thật.

## Quy trình bắt buộc trước khi thay đổi

Ứng dụng phải ưu tiên các nguyên tắc sau khi xử lý và review ảnh:

1. **Hạn chế đọc từ đĩa:** Luôn luôn đọc dữ liệu từ đĩa ít nhất có thể. Ưu tiên tái sử dụng dữ liệu đã đọc, cache và các cơ chế đọc tuần tự hiệu quả để tránh I/O dư thừa.
2. **Tận dụng RAM:** Ưu tiên sử dụng nhiều RAM để tối ưu tốc độ. Máy có 32 GB RAM, vì vậy mục tiêu sử dụng tối thiểu là 16 GB khi phù hợp. Nếu tổng dung lượng thư mục dữ liệu nhỏ hơn 16 GB, phải ưu tiên load toàn bộ thư mục lên RAM, miễn là không gây lỗi hoặc ảnh hưởng nghiêm trọng đến hệ thống.
3. **Ưu tiên tốc độ review:** Mọi thiết kế và tối ưu phải ưu tiên tốc độ chuyển ảnh, hiển thị ảnh và thao tác review nhanh.
4. **Ưu tiên chất lượng ảnh:** Khi có thể, bắt buộc load và hiển thị ảnh ở chất lượng tốt nhất có thể, hạn chế giảm chất lượng hoặc dùng ảnh xem trước nếu không cần thiết.

## Quy trình build, publish và push Git bắt buộc

- Sau mỗi thay đổi hoàn thiện, commit, push vào nhánh hợp lý để tôi merge.