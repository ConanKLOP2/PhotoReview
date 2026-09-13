# Tiến độ Photo Review

Cập nhật: 2026-09-13.
Nguồn trạng thái chi tiết: [TASKS.md](TASKS.md).

## Hiện trạng

- Tài liệu kế hoạch: hoàn tất phiên bản đầu.
- Audit hiện tại: 5 DONE, 14 PARTIAL, 16 TODO, 2 DEFERRED trong 39 task bản đầu.
- Benchmark trên bộ 242 ảnh: chưa chạy; ảnh gốc chưa thao tác.
- Ứng dụng đã có solution/source và release artifacts; chưa coi toàn bộ plan là hoàn tất.

## Milestone

| Mốc | Task | DONE/tổng | Gate | Trạng thái |
|---|---|---|---|---|
| M0 Khảo sát | PR-001–004 | PARTIAL | G0 | Chưa có hồ sơ môi trường, bộ ảnh và baseline |
| M1 Prototype | PR-005–012 | PARTIAL | G1 | Viewer/scanner/cache có; EXIF, scheduler đầy đủ và benchmark thiếu |
| M2 Review | PR-013–020 | PARTIAL | G2 | Workflow cơ bản có; Settings UI, filter, counter và gate GUI thiếu |
| M3 File an toàn | PR-021–028 | PARTIAL | G3 | Move/journal/Undo guard có; recovery UI/fault injection thiếu; PR-024/027 deferred |
| M4 Cache hoàn chỉnh | PR-029–034 | PARTIAL | G4 | Cache cơ bản có; LRU/quota/telemetry/benchmark thiếu |
| M5 Release | PR-035–039 | PARTIAL | G5 | Build/test/package có; GUI acceptance, regression report và installer thiếu |

## Ba bước tiếp theo

1. Chạy GUI acceptance trên bộ 242 ảnh thật và lưu evidence workflow/resume.
2. Hoàn thiện Settings UI cho shortcut mapping, folder đích, validation và config.
3. Bổ sung EXIF test, LRU/quota cache, recovery UI và benchmark report.

Đường dẫn bộ 242 ảnh hiện chưa biết; đây là điều kiện để nghiệm thu/benchmark, không phải lỗi implementation.

## Nhật ký

| Ngày | Thay đổi | Bằng chứng |
|---|---|---|
| 2026-09-13 | Tạo bộ kế hoạch và backlog, chưa triển khai | README.md và TASKS.md |
| 2026-09-13 | Tạo solution WPF và prototype folder/viewer/cache/preload; build PASS | PhotoReview.slnx; build 0 warning/0 error |
| 2026-09-13 | Thêm mở file từ Explorer, config folder 1/2, Enter Move loại 2 và Delete Recycle Bin; workflow phím số đã bỏ | App.xaml.cs, AppSettings.cs, association script |
| 2026-09-13 | Thêm zoom, Ctrl+Z cho Move và cập nhật phím tắt; build PASS | MainWindow.xaml/.cs; dotnet build 0 warning/0 error |
| 2026-09-13 | Thêm session store, journal và instance lock; smoke test script được tạo | SessionStore.cs, OperationJournal.cs, InstanceLock.cs |
| 2026-09-13 | Đơn giản hóa workflow theo yêu cầu: mũi tên để nguyên loại 1, Enter Move loại 2, Delete Recycle Bin; thêm test project và smoke tests PASS | MainWindow.xaml/.cs, PhotoReview.Tests, tools/smoke-test.ps1 |
| 2026-09-13 | Audit lại trạng thái theo implementation/evidence; hạ các task chưa đủ test từ DONE xuống PARTIAL và ghi rõ gap | TASKS.md; build/test/smoke/release evidence |

## Cách cập nhật cuối mỗi phiên làm việc

1. Cập nhật task đã làm: trạng thái, ngày, evidence, blocker.
2. Đếm lại DONE/TODO/IN_PROGRESS/BLOCKED, không tính FUT.
3. Ghi kết quả gate; không suy ra gate đạt chỉ vì code đã viết.
4. Thêm nhật ký kết quả thực tế, không chỉ số giờ.
5. Chọn tối đa ba bước tiếp theo theo phụ thuộc.
