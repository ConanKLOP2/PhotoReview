# Kiểm thử và nghiệm thu

Đối chiếu 2026-09-15: Release contract runner PASS sau `5189298`, nhưng nhiều assertion chỉ kiểm tra chuỗi source. Smoke/fault scripts dùng fixture độc lập, chưa fault-inject executor của app. T02/T03, T12/T16/T21/T24/T26 và GUI/native Explorer matrix chưa có evidence PASS; ghi NOT_TESTED trong báo cáo nghiệm thu.

## Tầng kiểm thử

Unit: natural sort, ánh xạ nhãn, cache key, eviction budget, state transition, path planning.
Integration: session/journal, decoder thực, filesystem Move/Recycle Bin/Undo/recovery.
UI/manual: focus, key repeat, DPI, zoom, stale frame, fullscreen.
Performance: theo 04-PERFORMANCE.md.
Không dùng unit test thay cho kiểm tra file thật và crash recovery.

## Ma trận test

| ID | Ca thử | Kết quả bắt buộc |
|---|---|---|
| T01 | Tên 2/10, Unicode, dấu nháy | Đúng thứ tự và đường dẫn |
| T02 | Ảnh A decode chậm, chuyển B | A không ghi đè B |
| T03 | Giữ 1, rồi nhấn 2 nhanh | Không repeat hàng loạt hoặc áp sai ID |
| T04 | Focus ô tìm kiếm | Phím nhập không phân loại |
| T05 | Preview → 100% | Báo trạng thái đúng, hướng xoay đúng |
| T06 | DPI khác, cửa sổ resize | Fit đúng, không decode storm |
| T07 | RAM budget thấp | Evict ổn, current frame không mất |
| T08 | Cache hỏng/nguồn đổi | Rebuild đúng |
| T09 | Đích trong nguồn | Không quét lặp ảnh đã chuyển |
| T10 | Hai folder cùng tên file | Không ghi đè, giữ phân biệt |
| T11 | Same-volume Move/Undo | Đúng đường dẫn và hash |
| T12 | Cross-volume Move | Verify trước bỏ nguồn |
| T13 | Hết chỗ giữa copy | Nguồn còn, lỗi rõ, temp có tracking |
| T14 | File bị khóa/mất quyền | Không báo thành công giả |
| T15 | Source thay đổi khi copy | Không xóa source mới |
| T16 | Crash từng bước file op | Recovery đối chiếu đúng |
| T17 | Undo sau sửa file đích | Conflict, không mất nội dung mới |
| T18 | Resume sau restart | Đúng nhãn, vị trí, pending |
| T19 | Hai instance | Không mutate phiên đồng thời |
| T20 | Ảnh hỏng/siêu lớn | Lỗi cô lập, vẫn duyệt được |
| T21 | Duyệt 30–60 phút | Không tăng RAM liên tục bất thường |
| T22 | Quét hàng nghìn file | UI vẫn phản hồi |
| T23 | Xóa cache/lịch sử | Chỉ tác động đúng dữ liệu ứng dụng |
| T24 | Máy sạch | EXE mở được, không cần môi trường dev |
| T25 | Mất ổ/network/cloud | Trạng thái phù hợp, có retry |
| T26 | Ảnh alpha/EXIF/profile màu | So sánh mẫu chuẩn, giới hạn được ghi |
| T27 | Copy rồi restart/Undo | Nguồn giữ nguyên; chỉ Undo đích hợp lệ |
| T28 | Đóng app khi pending | Nhật ký đủ, không replay mù |

## Chuẩn bị fixture

Dùng folder test độc lập, lưu manifest path/size/hash trước thử.
Tạo fixture nhỏ cho lỗi; giữ bộ ảnh thật riêng cho benchmark.
Fault injection tại các ranh giới ghi journal/copy/rename/delete/commit.
Test cross-volume cần hai volume thực hoặc môi trường mô phỏng được ghi rõ; không coi hai folder cùng ổ là cross-volume.

## Release gate

- T02/T03 và T09–T19/T27/T28 phải đạt trước dùng Move với ảnh thật.
- Hiệu năng có raw data và điều kiện chạy.
- Không có lỗi mở gây mất dữ liệu hoặc phân loại sai ảnh.
- Known issues có mức độ và workaround.
- UX dùng hoàn toàn bàn phím được.
- Portable build chạy trên máy không cài SDK trong OS support đã chốt.
- Chưa test điều gì phải ghi NOT_TESTED, không gộp thành PASS.

## Bằng chứng

Mỗi test: build/commit, OS, fixture, bước chạy, expected/actual, PASS/FAIL/NOT_TESTED, log hoặc ảnh chụp khi hữu ích.
Báo cáo release phải link benchmark, test report và known issues.
