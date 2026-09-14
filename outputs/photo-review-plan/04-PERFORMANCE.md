# Kế hoạch hiệu năng và benchmark

## Nguyên tắc

Không hứa nhanh hơn Photos trước khi đo. Không suy đoán Photos chỉ dùng preview. So sánh cùng mức nhìn được và full-resolution riêng biệt. Hiển thị thumbnail mờ sớm không được tính là hoàn thành ảnh rõ.

Runtime hiện dùng sort theo Settings và đọc metadata khi folder dưới 100 ảnh; từ 100 ảnh trở lên dùng thứ tự tên kiểu Windows Explorer, không chạy EXIF scan toàn folder trên critical path. Log chẩn đoán nằm tại `%LOCALAPPDATA%\PhotoReview\logs\app.log`.

## Khảo sát bắt buộc

Ghi OS/build, CPU, RAM, GPU/driver, màn hình/DPI, ổ lưu nguồn và cache, dung lượng trống, phiên bản Photos/FastStone, chế độ hiển thị. Không đọc dữ liệu cá nhân ngoài bộ mẫu được đặt trong phạm vi.

Bộ ảnh: số file, định dạng, phân bố MB, pixel, EXIF orientation, profile màu; vị trí local/USB/network/cloud. Bộ 242 ảnh chưa có trong workspace ở thời điểm lập kế hoạch.

## Bộ mẫu

A: JPG/PNG thực tế của người dùng.
B: ảnh nhỏ để làm mốc.
C: JPG độ phân giải cao, progressive nếu có.
D: PNG lớn/alpha.
E: ảnh dọc, panorama, EXIF xoay.
F: hỏng/truncated/kích thước cực lớn.
G: folder hàng nghìn ảnh để đo danh sách.
Ảnh benchmark phải có quyền sử dụng; không tự tải ảnh riêng tư.

## Các kịch bản

1. Mở folder chưa có cache ứng dụng.
2. Mở lại cùng folder có cache.
3. Duyệt sau khi prepare preview hoàn tất.
4. Duyệt tuần tự, đảo chiều, nhảy xa.
5. Nhấn nhanh rồi dừng ở một ảnh.
6. Zoom 100%.
7. Review đồng thời chuyển file.
8. Duyệt lâu rồi mở folder khác, theo dõi RAM.

Luân phiên thứ tự ứng dụng qua nhiều lượt; ghi OS cache có thể ảnh hưởng.
Không gọi một lần chạy là cold nếu chưa kiểm soát được cache hệ điều hành; đặt tên điều kiện đúng thực tế.
Tách thời gian chuẩn bị trước khỏi độ trễ duyệt sau chuẩn bị.

## Mốc đo

folder_request, listing_first_batch, decode_start/end, frame_ready, frame_presented_observed, key_received, label_persisted, operation_committed.
Internal frame-ready không đồng nghĩa pixel đã xuất hiện. Dùng screen recording hoặc phương pháp quan sát hiển thị để đối chiếu Photos/FastStone.

Đo median/P95/max, số mẫu, RAM peak/steady, CPU, I/O, disk cache, thời gian prepare.
Chạy tối thiểu ba lượt mỗi điều kiện; tăng khi biến động cao, ghi ngoại lệ.

## Mục tiêu sơ bộ cần chốt sau khảo sát

| Chỉ số | Mục tiêu |
|---|---|
| Chuyển preview resident | P95 < 100 ms |
| Phản hồi nhãn | P95 < 50 ms; tách khỏi thời điểm lưu bền vững |
| Ảnh rõ đầu tiên local SSD | Khoảng <= 1 s với bộ mẫu đã định nghĩa |
| RAM | Cache tuân budget; tổng app không tăng vô hạn |
| Đúng ảnh | Không stale frame, không nhãn sai |
| Full 100% | Báo riêng, chưa đặt SLA trước đo |

Các mục tiêu không áp dụng mặc nhiên cho ảnh cực lớn, network hoặc cloud chưa hydrate.

## Thử nghiệm tối ưu

- Decode ở kích thước viewport vs full rồi scale.
- Preload 1/3/5/10 ảnh, tính cả byte budget.
- Decode concurrency 1/2/4.
- RAM cache 512 MB/1 GB và cấu hình phù hợp máy.
- Disk cache bật/tắt.
- Codec/backend thay thế chỉ khi profiling chỉ ra decode là nút thắt.
- Thumbnail nền bật/tắt để biết tranh chấp tài nguyên.
- Chuẩn bị folder toàn bộ vs chỉ lân cận.

Mỗi lần chỉ thay ít biến, giữ raw results và cấu hình. Không tối ưu theo cảm giác.

## Cổng G1

PASS: người dùng có thể xem liên tục ở chất lượng phù hợp; mục tiêu đã chốt đạt hoặc có tradeoff được ghi nhận; không lỗi stale ảnh.
FAIL: tiếp tục profiling, chưa đầu tư nhiều vào tính năng phụ.
Báo rõ nhanh hơn/ngang/chậm hơn từng ứng dụng trong từng điều kiện; không lấy một kịch bản để tuyên bố thắng toàn bộ.
