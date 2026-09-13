# Roadmap

Không ước tính ngày hoàn tất trước khi có bộ mẫu, người thực hiện và kết quả prototype. Các milestone là thứ tự có điều kiện, không phải cam kết lịch.

| Mốc | Nội dung | Gate |
|---|---|---|
| M0 | Khảo sát, định nghĩa bộ mẫu, baseline | G0: đủ dữ liệu và mục tiêu có phạm vi |
| M1 | Prototype viewer + decode + preload | G1: tốc độ/chất lượng đủ tốt, đúng frame |
| M2 | Review nhãn + UX + persistence | G2: phím nhanh đúng ảnh, resume/Undo nhãn |
| M3 | Copy/Move + journal + recovery | G3: an toàn dữ liệu qua test lỗi |
| M4 | Cache hoàn chỉnh + prepare folder | G4: budget ổn, cache đúng, benchmark cập nhật |
| M5 | Đóng gói + nghiệm thu | G5: máy sạch, test release, workflow thật |

## Đường găng

Bộ ảnh/cấu hình → baseline → pipeline prototype → gate tốc độ → review đúng ID → journal/file operations → recovery tests → đóng gói/nghiệm thu.

Có thể song song: UX spec, fixture, schema và skeleton diagnostics sau khi scope được hiểu.
Không chạy song song mutation cùng fixture khi sẽ làm kết quả không xác định.

## Bản đầu hoàn chỉnh

JPG/PNG, folder, fit/100%, phím 1/2/3, skip, ba loại tùy chỉnh, đánh dấu hoặc Move ngay, Copy, Undo, phiên, RAM/disk cache, prepare folder, lỗi từng ảnh và diagnostics.

## Sau bản đầu

V1.1: so hai ảnh, đồng bộ zoom, remap phím, profile folder, xuất danh sách.
V1.2: nhóm tên (1)/(2), hash duplicate, near-duplicate có đánh giá false positive.
Theo nhu cầu: RAW/HEIC/AVIF, controller, tích hợp Explorer.
Chỉ sau bằng chứng nhu cầu: AI chấm nét, HDR chuyên nghiệp, nhiều nền tảng, cloud.

## Khi gate không đạt

G1: profile decode/I/O/render, giảm contention, thử decoder khác nếu cần.
G2: sửa state model và event ordering trước thêm thao tác file.
G3: không phát hành Move; có thể dùng bản đánh dấu nếu G2 đạt.
G4: điều chỉnh cache, không hứa giữ tất cả RAM.
G5: giữ bản thử nghiệm, công bố giới hạn và sửa lỗi chặn.

