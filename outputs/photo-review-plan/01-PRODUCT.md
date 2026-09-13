# Đặc tả sản phẩm

## Vấn đề

Người dùng thấy FastStone xem ảnh lớn chậm, Windows Photos nhanh hơn trên máy của mình nhưng thiếu workflow phân loại ba nhóm bằng phím. Cần phần mềm Windows chuyên review: mở folder, xem, nhấn 1/2/3, tự sang ảnh tiếp theo, Undo và tiếp tục phiên sau.

Chưa xác minh cấu hình máy, đường dẫn bộ 242 ảnh, định dạng thực tế hoặc cơ chế nội bộ Windows Photos. Không khẳng định sao chép được nguyên engine Photos hoặc chắc chắn nhanh hơn.

## Phạm vi bản đầu

- Windows x64; phiên bản hệ điều hành hỗ trợ cụ thể chốt sau khảo sát.
- JPG/JPEG và PNG được kiểm thử trước.
- Một folder mỗi phiên; quét đệ quy là tùy chọn, phải loại folder đích.
- Ba loại đổi được tên và đường dẫn đích.
- Chế độ đánh dấu rồi áp dụng mặc định; Move ngay và Copy được hỗ trợ.
- Xem vừa khung, zoom 100%, pan, EXIF orientation.
- RAM cache, disk preview cache, preload có giới hạn.
- Lưu phiên, nhật ký thao tác, Undo và phục hồi.
- Xử lý offline; không gửi ảnh, tên file, metadata ra mạng.
- Không mã hóa lại hoặc sửa metadata ảnh gốc khi phân loại.

## Yêu cầu có mã

| ID | Yêu cầu | Chấp nhận khi |
|---|---|---|
| FR-01 | Chọn/kéo thả folder hoặc ảnh | Mở đúng folder, chọn đúng ảnh nếu đầu vào là ảnh |
| FR-02 | Danh sách ổn định | Natural sort; file đích không tái xuất hiện; thay đổi ngoài app không làm nhảy vị trí âm thầm |
| FR-03 | Viewer | Fit/100%/pan đúng, tên và ảnh luôn đồng bộ |
| FR-04 | Phân loại | 1/2/3 áp dụng đúng ID đang hiển thị, tự sang ảnh chưa xử lý |
| FR-05 | Bỏ qua | Có trạng thái riêng và lọc xem lại |
| FR-06 | Undo | Hoàn tác nhãn hoặc thao tác file hợp lệ; giải thích xung đột |
| FR-07 | Copy/Move | Không ghi đè, có trạng thái từng file và retry có kiểm tra |
| FR-08 | Phiên | Mở lại đúng tiến độ; không tự chạy lại thao tác chưa rõ |
| FR-09 | Cache | Giới hạn được, xóa được, đổi nguồn thì invalidation |
| FR-10 | Chuẩn bị folder | Có tiến độ/hủy; vẫn xem được; không vượt ngân sách cố giữ tất cả |
| FR-11 | Tìm/lọc | Theo tên, loại, chưa phân loại, bỏ qua; đếm đúng |
| FR-12 | Cấu hình | Tên loại, đích, chế độ, RAM/disk cache lưu được |
| NFR-01 | Hiệu năng | Đo theo 04-PERFORMANCE.md và báo cả trường hợp không đạt |
| NFR-02 | Dữ liệu | Vượt gate trong 05-DATA-SAFETY.md |
| NFR-03 | Khả năng dùng | Toàn bộ vòng review dùng bàn phím; focus rõ |
| NFR-04 | Riêng tư | Cache/lịch sử/log có quản lý; mặc định không telemetry |
| NFR-05 | Phục hồi | Crash không khiến app báo sai thành công hoặc tự xóa nguồn |
| NFR-06 | Phân phối | Chạy trên máy sạch trong dải OS hỗ trợ đã chốt |

## Ngữ nghĩa phân loại

Loại 3 không đồng nghĩa xóa. Đánh dấu lại thay nhãn hiện tại và được Undo.
Đổi tên loại chỉ đổi nhãn; đổi folder đích chỉ áp dụng thao tác tương lai, không tự chuyển lại file cũ.
Chế độ Copy vẫn đánh dấu nguồn là đã xử lý trong phiên.
Nếu quét folder con, giữ đường dẫn tương đối trong đích theo mặc định.
Bản đầu chưa có xóa vĩnh viễn, chỉnh sửa ảnh, nhận diện AI, HDR chuyên nghiệp hoặc cloud sync.

## Tiêu chí nghiệm thu trải nghiệm

Người dùng duyệt bộ ảnh mẫu, phân loại bằng một phím, sửa nhầm bằng Undo, đóng/mở lại rồi tiếp tục. Ảnh gốc giữ nguyên nội dung. Chuyển ảnh có cache đạt mục tiêu đã chốt sau prototype; mọi hạn chế codec, màu và tốc độ được nêu trong release notes.

