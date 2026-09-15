# Mẫu dùng trong quá trình triển khai

Áp dụng 2026-09-15: mỗi bản report từ template cần ghi commit HEAD/remote, đường dẫn artifact publish bắt buộc, build stamp/version, fixture/OS, kết quả Release tests/build/publish và push. Mỗi case GUI, benchmark hoặc crash chưa chạy phải ghi `NOT_TESTED`; không suy PASS từ script kiểm tra file hiện diện.

## Cập nhật công việc

Ngày:
Task ID:
Build/commit:
HEAD local / origin/master:
Publish artifact path / FileVersion / build stamp:
Đã thay đổi:
Kết quả kiểm tra:
Release test / build / publish / verify / push:
Bằng chứng:
Còn thiếu:
Trạng thái mới:
Bước tiếp theo:

## Báo lỗi

Bug ID / severity:
Task hoặc requirement liên quan:
OS/build/codec:
Fixture hoặc ảnh mẫu:
Các bước tái hiện:
Kỳ vọng:
Thực tế:
Tần suất:
Ảnh hưởng dữ liệu:
Log/bằng chứng:
Cách tạm xử lý:
Kết quả retest:

Severity: S0 mất/sửa dữ liệu ngoài ý muốn; S1 sai ảnh/không dùng được workflow; S2 tính năng lỗi có cách tránh; S3 hiển thị nhỏ.
Không đưa ảnh/tên file riêng tư vào báo cáo chia sẻ khi không cần.

## Benchmark

Ngày / build:
Máy / OS / màn hình / DPI:
Ổ nguồn / cache:
Bộ mẫu / số ảnh / pixel / MB:
Ứng dụng và phiên bản:
Điều kiện cache thực tế:
Thứ tự chạy / số lượt:
Phương pháp đo:
Fixture manifest / hash và source-read counters:
Thời gian chuẩn bị:
First visible / first clear:
Chuyển ảnh median / P95 / max / số mẫu:
Full 100%:
Phản hồi nhãn / lưu bền vững:
RAM peak / steady / cache bytes:
CPU / I/O / disk cache:
Chất lượng hình so sánh:
Raw data:
Kết luận có phạm vi:
Hạn chế:

## Quyết định gate

Gate:
Task bắt buộc:
Bằng chứng:
Tiêu chí đạt:
Tiêu chí chưa đạt:
PASS / FAIL / CONDITIONAL:
NOT_TESTED cases:
Điều kiện còn lại và người quyết định:
Bước tiếp theo:

## Release checklist

- [ ] Version/build xác định
- [ ] Commit HEAD và origin/master cùng revision, working tree sạch
- [ ] Release test/build PASS sau commit
- [ ] Publish đúng `PhotoReview.App/bin/Release/net10.0-windows/publish`
- [ ] Artifact path/FileVersion/build stamp và source provenance được ghi
- [ ] `git push origin master` thành công theo AGENTS.md
- [ ] G0–G4 có bằng chứng
- [ ] Test release và máy sạch đạt
- [ ] Không lỗi dữ liệu/sai ID còn mở
- [ ] Benchmark cùng điều kiện được đính kèm
- [ ] Known issues và NOT_TESTED liệt kê
- [ ] EXE, source, build guide và phím tắt có đủ
- [ ] Nghiệm thu người dùng được ghi nhận
- [ ] TASKS.md và PROGRESS.md đồng bộ

