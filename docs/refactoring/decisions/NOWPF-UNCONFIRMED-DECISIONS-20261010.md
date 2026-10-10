---
id: NOWPF-UNCONFIRMED-DECISIONS-20261010
order: 610
summary: |-
  Các quyết định chủ dự án còn mở mà agent ĐÃ làm theo khuyến nghị (người dùng cho phép 2026-10-10, "chưa xác nhận"): NE-3/4/6/7/9, khởi động, B-06, C-04. Người dùng xác nhận hoặc đảo lại từng mục; đảo lại thì ghi cách hoàn tác.
---

# Quyết định đã làm theo khuyến nghị, CHƯA được người dùng xác nhận

Người dùng (2026-10-10): "Các quyết định nào cần tôi quyết định cứ làm theo đề xuất, rồi đánh dấu là tôi chưa xác nhận để tôi xác nhận lại sau".
Mọi mục dưới đây có trạng thái **CHƯA XÁC NHẬN**. Agent làm theo khuyến nghị; mỗi mục ghi cách hoàn tác. Người dùng xác nhận
bằng cách đổi trạng thái thành ĐÃ XÁC NHẬN (hoặc chọn phương án khác).

| Mã | Quyết định đã áp dụng (= khuyến nghị) | Phương án còn lại | Hoàn tác | Trạng thái |
|---|---|---|---|---|
| NE-3 | Thanh cuộn bản Win32: (a) tự vẽ, tương đương bản trước | (b) thanh cuộn ẩn kiểu overlay | Đổi ở WP thanh cuộn, không ảnh hưởng dữ liệu | CHƯA XÁC NHẬN |
| NE-4 | Menu chuột phải: (a) menu native + dark qua uxtheme | (b) tự vẽ | Chỉ lớp menu Shell | CHƯA XÁC NHẬN |
| NE-6 | Single-instance beta: (a) prefix pipe riêng cho bản Win32, hợp nhất ở P6 | (b) dùng chung pipe | Đổi một hằng prefix | CHƯA XÁC NHẬN |
| NE-7 | (a) đóng băng tính năng UI mới trên bản WPF từ đầu đợt 3 | (b) song song hai bản | Mở lại tính năng WPF | CHƯA XÁC NHẬN |
| NE-9 | (a) D2D cubic/linear; G-PIX theo PSNR + xem bằng mắt | (b) thu nhỏ CPU bằng WIC Fant mỗi bước zoom | Đổi bộ lọc trong WP-15 | CHƯA XÁC NHẬN |
| WP04-JPEG | Cache JPEG qua WIC chậm hơn +17-30 % (cầu WPF thêm 2 bản chép khung, ~+4 ms/preview nền): (a) chấp nhận tới WP-06, WP-06 bắt buộc tiêm codec | (b) lối tắt băng-dải, cần mở rộng C-02 đóng băng | Không đổi mặc định (codec WPF mặc định); đổi ở WP-06 | CHƯA XÁC NHẬN |
| STARTUP-1 | Bản .jpg dùng publish R2R 15 MB (người dùng ĐÃ chọn, xác nhận) | 174 MB self-contained + composite | Đổi khoá registry | ĐÃ XÁC NHẬN |
| STARTUP-2 | Hoãn UI nhìn thấy tới sau ảnh đầu (-100 ms) khi làm startup tiếp | giữ nguyên | Revert PR | CHƯA XÁC NHẬN |
| B-06 | Thêm hậu kiểm "nguồn đã biến mất" cho Recycle đơn như bản nhóm (fail + không commit nếu file còn) | giữ nguyên | Revert PR | CHƯA XÁC NHẬN |
| C-04 | Save sau khi Load ConfigVersion mới hơn: sao lưu `config.json.newer-<ts>` trước khi ghi v3 | giữ nguyên (mất trường lạ) | Revert PR | CHƯA XÁC NHẬN |
| WP05-SCALE | Fine-scale dùng WIC Fant (người dùng ĐÃ chọn b, xác nhận) | PixelAreaResampler tự viết | WP-06 | ĐÃ XÁC NHẬN |

## Không làm mà không hỏi (vẫn cần người dùng)
- NE-1/2/5/8 đã chốt trước đó. Chế độ chạy nền/khay, Native AOT (cài MSVC + SDK 4-5 GB), xoá dữ liệu người dùng, bất cứ thay đổi
  nào chạm Thùng rác thật: KHÔNG tự làm.
