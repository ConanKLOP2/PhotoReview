# Quy tắc ưu tiên bắt buộc cho ứng dụng PhotoReview

Ứng dụng phải ưu tiên các nguyên tắc sau khi xử lý và review ảnh:

1. **Hạn chế đọc từ đĩa:** Luôn luôn đọc dữ liệu từ đĩa ít nhất có thể. Ưu tiên tái sử dụng dữ liệu đã đọc, cache và các cơ chế đọc tuần tự hiệu quả để tránh I/O dư thừa.
2. **Tận dụng RAM:** Ưu tiên sử dụng nhiều RAM để tối ưu tốc độ. Máy có 32 GB RAM, vì vậy mục tiêu sử dụng tối thiểu là 16 GB khi phù hợp. Nếu tổng dung lượng thư mục dữ liệu nhỏ hơn 16 GB, phải ưu tiên load toàn bộ thư mục lên RAM, miễn là không gây lỗi hoặc ảnh hưởng nghiêm trọng đến hệ thống.
3. **Ưu tiên tốc độ review:** Mọi thiết kế và tối ưu phải ưu tiên tốc độ chuyển ảnh, hiển thị ảnh và thao tác review nhanh.
4. **Ưu tiên chất lượng ảnh:** Khi có thể, bắt buộc load và hiển thị ảnh ở chất lượng tốt nhất có thể, hạn chế giảm chất lượng hoặc dùng ảnh xem trước nếu không cần thiết.

## Quy trình build, publish và push Git bắt buộc

- Sau mỗi thay đổi hoàn thiện và mỗi commit, luôn chạy test/build Release trước khi bàn giao.
- “Public” trong quy trình này nghĩa là push commit lên remote GitHub `origin` (không chỉ tạo thư mục publish cục bộ).
- Luôn publish bản kiểm tra vào đúng thư mục mặc định:

  `PhotoReview.App/bin/Release/net10.0-windows/publish`

- Lệnh chuẩn:

  `dotnet run --project PhotoReview.Tests -c Release`

  `dotnet publish PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o PhotoReview.App/bin/Release/net10.0-windows/publish`

- Không coi công việc là hoàn tất nếu chưa publish thành công vào thư mục trên. Quy trình này áp dụng trên mọi máy làm việc với project.
- Sau khi commit hoàn tất và test/publish thành công, push branch hiện tại lên remote:

  `git push origin master`

- Không coi công việc là hoàn tất nếu chưa kiểm tra push thành công (trừ khi remote từ chối hoặc thiếu quyền, khi đó phải báo rõ lỗi).
