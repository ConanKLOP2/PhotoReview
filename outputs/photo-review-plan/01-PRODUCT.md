# Đặc tả sản phẩm

Đối chiếu runtime 2026-09-15: app hiện scan top-level và hỗ trợ JPG/JPEG/PNG/BMP/GIF/TIF/TIFF; recursive scan, search/filter, RAM-only option và progress/cancel đầy đủ là yêu cầu chưa xác nhận. Recycle Bin có Undo trong menu/handler riêng, nhưng phím Ctrl+Z hiện không chứng minh được Undo Delete ở mọi trạng thái. Phần dưới là đặc tả mục tiêu.

## Vấn đề

Người dùng thấy FastStone xem ảnh lớn chậm, Windows Photos nhanh hơn trên máy của mình nhưng thiếu workflow phân loại nhanh. Cần phần mềm Windows chuyên review: dùng mũi tên để duyệt và để file loại 1 nguyên tại nguồn, Enter để chuyển loại 2 vào folder config, Delete để đưa loại 3 vào Thùng rác.

Chưa xác minh cấu hình máy, đường dẫn bộ 242 ảnh, định dạng thực tế hoặc cơ chế nội bộ Windows Photos. Không khẳng định sao chép được nguyên engine Photos hoặc chắc chắn nhanh hơn.

## Phạm vi bản đầu

- Windows x64; phiên bản hệ điều hành hỗ trợ cụ thể chốt sau khảo sát.
- JPG/JPEG và PNG được kiểm thử trước.
- Một folder mỗi phiên; quét đệ quy là tùy chọn, phải loại folder đích.
- Folder loại 2 được cấu hình; loại 1 là file còn nguyên ở nguồn và loại 3 là file trong Thùng rác.
- Workflow tức thời: mũi tên để nguyên loại 1; Enter Move loại 2; Delete Recycle Bin loại 3.
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
| FR-04 | Phân loại | Mũi tên chỉ duyệt; Enter chuyển loại 2; Delete đưa loại 3 vào Thùng rác |
| FR-05 | Bỏ qua | Có trạng thái riêng và lọc xem lại |
| FR-06 | Undo | Hoàn tác nhãn hoặc thao tác file hợp lệ; giải thích xung đột |
| FR-07 | Move loại 2 | Không ghi đè, có trạng thái từng file và retry có kiểm tra |
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

Loại 1 không có action filesystem; file còn ở nguồn là file loại 1/chưa tác động.
Loại 3 được đưa vào Thùng rác Windows và không Undo tự động.
Đổi tên loại chỉ đổi nhãn; đổi folder đích chỉ áp dụng thao tác tương lai, không tự chuyển lại file cũ.
Không có Copy trong workflow bản đầu; loại 1 giữ nguyên để người dùng tự xử lý.
Nếu quét folder con, giữ đường dẫn tương đối trong đích theo mặc định.
Bản đầu chưa có xóa vĩnh viễn, chỉnh sửa ảnh, nhận diện AI, HDR chuyên nghiệp hoặc cloud sync.

## Tiêu chí nghiệm thu trải nghiệm

Người dùng duyệt bộ ảnh mẫu, phân loại bằng một phím, sửa nhầm bằng Undo, đóng/mở lại rồi tiếp tục. Ảnh gốc giữ nguyên nội dung. Chuyển ảnh có cache đạt mục tiêu đã chốt sau prototype; mọi hạn chế codec, màu và tốc độ được nêu trong release notes.
