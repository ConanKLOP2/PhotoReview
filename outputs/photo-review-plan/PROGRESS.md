# Tiến độ Photo Review

Cập nhật: 2026-09-14.
Nguồn trạng thái chi tiết: [TASKS.md](TASKS.md).

## Cập nhật runtime gần nhất

- 2026-09-14: hỗ trợ mở folder qua command line; hiển thị ảnh đầu tiên trước khi decode EXIF toàn folder; thêm fallback decode, dimension render/gốc, log tập trung và bắt phím ở `PreviewKeyDown`. Settings đã bỏ shortcut chuyển loại 2 trùng Action JSON. Build và test PASS.
- 2026-09-14: lập kế hoạch Shell Explorer order tại `SHELL-VIEW-ORDER-PLAN.md`; đã review rủi ro COM/UI freeze/fallback/100-file boundary. Chưa triển khai adapter.
- 2026-09-14: triển khai Shell order provider cho folder từ 100 ảnh, có timeout 2 giây, COM cleanup và fallback sort tên; build/test PASS.

## Hiện trạng

- Tài liệu kế hoạch: hoàn tất phiên bản đầu.
- Audit hiện tại: 5 DONE, 19 PARTIAL, 13 TODO, 2 DEFERRED trong 39 task bản đầu.
- Benchmark trên bộ 242 ảnh thật: chưa chạy; đã thêm `tools/benchmark-folder.ps1` để đo baseline đọc storage mà không sửa ảnh.
- Ứng dụng đã có solution/source và release artifacts; chưa coi toàn bộ plan là hoàn tất.
- Gate tích hợp gần nhất: PASS; release đã publish lại từ source hiện tại, SHA256 `525E094AE7BF1C4BCAD4066E7E945E6F98E92517B1AB4C2EB46328F2BAD1ACA9`.

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

1. Chạy `tools/benchmark-folder.ps1` trên bộ 242 ảnh thật và lưu median/P95 theo từng ổ đĩa.
2. Chạy GUI acceptance trên bộ ảnh thật và lưu evidence workflow/resume.
3. Bổ sung benchmark decode WPF và GUI acceptance trên Windows sạch.

Đường dẫn bộ 242 ảnh hiện chưa biết; đây là điều kiện để nghiệm thu/benchmark, không phải lỗi implementation.

## Evidence đã xác nhận

- Debug/Release build: PASS, 0 warning/0 error.
- `PhotoReview.Tests`: PASS persistence, journal/pending journal, keyboard contract và association contract.
- `tools/smoke-test.ps1`: PASS Move/Undo bytes, conflict, session atomic write, Recycle Bin và cleanup.
- `tools/fault-injection-test.ps1`: PASS conflict, prepared/interrupted operation và byte preservation.
- `tools/verify-release.ps1`: PASS release files và executable hash.

## Nhật ký audit

| Ngày | Kết quả |
|---|---|
| 2026-09-13 | Đối chiếu lại source, tests và release artifacts; cập nhật trạng thái trong TASKS.md. Chưa đủ bằng chứng để tuyên bố toàn bộ plan hoàn thành. |

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
| 2026-09-14 | Loại bỏ fallback phím điều hướng/zoom hardcode; runtime chỉ dùng shortcut trong Settings (Escape vẫn là thoát fullscreen/modal) | `891f403`; build Release PASS; PhotoReview.Tests PASS |
| 2026-09-14 | Gom validation shortcut toàn cục và action profile; chặn xung đột phím trước khi lưu Settings | `6c1f40e`; build Release PASS; PhotoReview.Tests PASS |
| 2026-09-14 | Chạy verify-all và publish lại release để loại bỏ evidence artifact stale | `verify-all.ps1` PASS; `verify-release.ps1` PASS; SHA256 `525E094A...` |
| 2026-09-14 | Nối `ShortcutMappings.Compare` vào toggle ComparePanel; giữ click preview là chọn ảnh, không điều hướng | `0c8f335`; build solution PASS; PhotoReview.Tests PASS |
| 2026-09-14 | Tách tùy chọn Compare hash và Compare size để giảm I/O theo workflow; cả hai được lưu trong Settings | `1150f88`, `0807efd`; build solution PASS; PhotoReview.Tests PASS |
| 2026-09-14 | Thêm core retry an toàn cho Move/Copy theo fingerprint, journal Prepared/Committed/Failed; không retry Recycle Bin | `c130ac6`; build solution PASS; PhotoReview.Tests PASS |
| 2026-09-14 | Nối retry vào Recovery UI: chọn operation, xác nhận, chỉ cho Move/Copy và hiển thị kết quả | `8532f0f`; build solution PASS; PhotoReview.Tests PASS |
| 2026-09-14 | Sửa wording/accessibility Recovery và publish release chứa đầy đủ Recovery UI mới nhất | `207bca6`; `verify-release.ps1` PASS; SHA256 `8CE35B7C...` |
| 2026-09-14 | Đổi Name sort sang Windows logical ordering (`StrCmpLogicalW`), giữ PortraitFirst grouping trước bước sort tên | `86e50ee`; build solution PASS; PhotoReview.Tests PASS |

## Cách cập nhật cuối mỗi phiên làm việc

1. Cập nhật task đã làm: trạng thái, ngày, evidence, blocker.
2. Đếm lại DONE/TODO/IN_PROGRESS/BLOCKED, không tính FUT.
3. Ghi kết quả gate; không suy ra gate đạt chỉ vì code đã viết.
4. Thêm nhật ký kết quả thực tế, không chỉ số giờ.
5. Chọn tối đa ba bước tiếp theo theo phụ thuộc.
### 2026-09-14 — Explorer-order audit clarification

- Đã xác nhận PhotoReview hiện không nhận được trạng thái sort column/direction đang hiển thị trong một cửa sổ Explorer chỉ từ đường dẫn file được double-click.
- Fallback hiện tại đã có: `Name` dùng `StrCmpLogicalW` (logical filename như Explorer), `SizeAscending`, `SizeDescending`, và `PortraitFirst` mặc định.
- Phần còn thiếu để đạt parity với Windows Photos là một adapter Windows Shell lấy danh sách theo folder view hiện hành; cần kiểm thử runtime trên Explorer thật trước khi coi là hoàn tất.
- Evidence: commits `86e50ee`, `20637ee`, `b07752a`; contract tests/build PASS.
