# Tiến độ Photo Review

Cập nhật: 2026-09-13.
Nguồn trạng thái chi tiết: [TASKS.md](TASKS.md).

## Hiện trạng

- Tài liệu kế hoạch: hoàn tất phiên bản đầu.
- Triển khai: 3/39 task DONE (7.7% theo số task, không đại diện khối lượng).
- TODO: 31; IN_PROGRESS: 4; BLOCKED: 0.
- Mở rộng: 8 task DEFERRED, không tính vào bản đầu.
- Benchmark: chưa chạy.
- Ứng dụng: chưa tạo.
- Ảnh gốc: chưa thao tác.

## Milestone

| Mốc | Task | DONE/tổng | Gate | Trạng thái |
|---|---|---|---|---|
| M0 Khảo sát | PR-001–004 | 0/4 | G0 | Chưa bắt đầu |
| M1 Prototype | PR-005–012 | 3/8 DONE, 5 IN_PROGRESS | G1 | Đang triển khai |
| M2 Review | PR-013–020 | 0/8 | G2 | Chưa bắt đầu |
| M3 File an toàn | PR-021–028 | 0/8 | G3 | Chưa bắt đầu |
| M4 Cache hoàn chỉnh | PR-029–034 | 0/6 | G4 | Chưa bắt đầu |
| M5 Release | PR-035–039 | 0/5 | G5 | Chưa bắt đầu |

## Ba bước tiếp theo

1. PR-008: hoàn thiện zoom 100%, pan và EXIF orientation.
2. PR-009/PR-010: tách scheduler và thêm byte budget/eviction cho RAM cache.
3. PR-001–003: ghi cấu hình, bộ ảnh mẫu và baseline Photos/FastStone.

Đường dẫn bộ 242 ảnh hiện chưa biết. Đây là thông tin cần cho benchmark, chưa ghi BLOCKED vì task chưa bắt đầu. Công việc skeleton/spec có thể chuẩn bị khi phạm vi triển khai được yêu cầu.

## Nhật ký

| Ngày | Thay đổi | Bằng chứng |
|---|---|---|
| 2026-09-13 | Tạo bộ kế hoạch và backlog, chưa triển khai | README.md và TASKS.md |
| 2026-09-13 | Tạo solution WPF và prototype folder/viewer/cache/preload; build PASS | PhotoReview.slnx; build 0 warning/0 error |
| 2026-09-13 | Thêm mở file từ Explorer, config folder 1/2, Enter Move loại 2 và Delete Recycle Bin; workflow phím số đã bỏ | App.xaml.cs, AppSettings.cs, association script |
| 2026-09-13 | Thêm zoom, Ctrl+Z cho Move và cập nhật phím tắt; build PASS | MainWindow.xaml/.cs; dotnet build 0 warning/0 error |
| 2026-09-13 | Thêm session store, journal và instance lock; smoke test script được tạo | SessionStore.cs, OperationJournal.cs, InstanceLock.cs |
| 2026-09-13 | Đơn giản hóa workflow theo yêu cầu: mũi tên để nguyên loại 1, Enter Move loại 2, Delete Recycle Bin; thêm test project và smoke tests PASS | MainWindow.xaml/.cs, PhotoReview.Tests, tools/smoke-test.ps1 |

## Cách cập nhật cuối mỗi phiên làm việc

1. Cập nhật task đã làm: trạng thái, ngày, evidence, blocker.
2. Đếm lại DONE/TODO/IN_PROGRESS/BLOCKED, không tính FUT.
3. Ghi kết quả gate; không suy ra gate đạt chỉ vì code đã viết.
4. Thêm nhật ký kết quả thực tế, không chỉ số giờ.
5. Chọn tối đa ba bước tiếp theo theo phụ thuộc.
