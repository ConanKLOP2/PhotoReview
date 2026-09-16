# Backlog chi tiết và audit trạng thái

Cập nhật nguồn lịch sử: 2026-09-13. Bảng và block chi tiết bên dưới là snapshot PR-xxx cũ, chưa được đồng bộ sau implementation 2026-09-14/15; không dùng chúng làm kết luận gate hiện tại. Trạng thái audit hiện hành và việc cần làm xem [CURRENT-STATE.md](CURRENT-STATE.md) và [FUNCTION-AUDIT-PLAN.md](FUNCTION-AUDIT-PLAN.md). Khi làm tiếp PR-xxx, phải sửa đồng thời bảng và block chi tiết bằng evidence.

Quy ước: DONE = có implementation và evidence đủ; PARTIAL = implementation/evidence chưa đủ; TODO = chưa có; DEFERRED = ngoài MVP. Workflow hiện tại là Left/Right để nguyên file nguồn (loại 1), Enter Move folder 2, Delete Recycle Bin (loại 3), Space skip, Ctrl+Z Undo Move. Không còn yêu cầu phím số 1.

## Đồng bộ trạng thái theo source master — 2026-09-15

Bảng PR-xxx bên dưới là snapshot lịch sử và đã lệch sau các đợt implementation. Theo source hiện tại, các task đã có implementation và contract test tương ứng là **PR-005–010, PR-013–017, PR-019, PR-021–023, PR-025–026, PR-029, PR-031–032** (DONE về implementation). Các gate GUI, benchmark, fault-injection và native matrix vẫn là điều kiện nghiệm thu riêng, chưa tự động đạt.

Các task còn mở: **PR-001–004, PR-011–012, PR-018, PR-020, PR-028, PR-030, PR-033–035, PR-037–039**. **PR-024, PR-027** và backlog `FUT-*` vẫn DEFERRED ngoài phạm vi MVP. Evidence ưu tiên: `CURRENT-STATE.md`, `FUNCTION-AUDIT-PLAN.md`, test Release và publish Release trên `origin/master`.

## Bảng task bản đầu

| ID | Mốc | Task | Trạng thái | Phụ thuộc |
|---|---|---|---|---|
| PR-001 | M0 | Khảo sát môi trường | TODO | — |
| PR-002 | M0 | Lập bộ ảnh mẫu và fixture | TODO | PR-001 |
| PR-003 | M0 | Đo baseline Photos/FastStone | TODO | PR-001, PR-002 |
| PR-004 | M0 | Chốt phạm vi và gate G0 | PARTIAL | PR-003 |
| PR-005 | M1 | Khởi tạo solution và build | DONE | PR-004 |
| PR-006 | M1 | Scanner và natural sort | DONE | PR-005 |
| PR-007 | M1 | Decoder adapter và metadata | PARTIAL | PR-005, PR-002 |
| PR-008 | M1 | Viewer fit và 100% | PARTIAL | PR-007 |
| PR-009 | M1 | Scheduler và chống stale frame | PARTIAL | PR-006, PR-008 |
| PR-010 | M1 | RAM cache prototype | PARTIAL | PR-009 |
| PR-011 | M1 | Instrumentation benchmark | TODO | PR-005 |
| PR-012 | M1 | Benchmark và quyết định G1 | TODO | PR-003, PR-010, PR-011 |
| PR-013 | M2 | Schema phiên và persistence | PARTIAL | PR-012 |
| PR-014 | M2 | Command phân loại đúng ID | DONE | PR-013, PR-009 |
| PR-015 | M2 | Skip, history và Undo nhãn | PARTIAL | PR-014 |
| PR-016 | M2 | Focus và bộ shortcut | PARTIAL | PR-014 |
| PR-017 | M2 | Giao diện review và bộ đếm | PARTIAL | PR-015, PR-016 |
| PR-018 | M2 | Tìm lọc và thumbnail strip | TODO | PR-017 |
| PR-019 | M2 | Cấu hình loại và chế độ | PARTIAL | PR-013, PR-017 |
| PR-020 | M2 | Gate G2 review/resume | TODO | PR-015, PR-016, PR-018, PR-019 |
| PR-021 | M3 | File operation planner | PARTIAL | PR-020 |
| PR-022 | M3 | Journal và state machine | PARTIAL | PR-021 |
| PR-023 | M3 | Same-volume Move | DONE | PR-022 |
| PR-024 | M3 | Copy và cross-volume Move | DEFERRED | PR-022 |
| PR-025 | M3 | Undo file operations | PARTIAL | PR-023, PR-024 |
| PR-026 | M3 | Recovery và khóa phiên | PARTIAL | PR-022, PR-025 |
| PR-027 | M3 | Hàng đợi và tích hợp chế độ | DEFERRED | PR-026, PR-019 |
| PR-028 | M3 | Gate G3 dữ liệu | TODO | PR-027 |
| PR-029 | M4 | Disk cache có version | DONE | PR-012, PR-013 |
| PR-030 | M4 | Prepare cả folder | TODO | PR-029, PR-010 |
| PR-031 | M4 | RAM pressure và I/O tuning | TODO | PR-030, PR-027 |
| PR-032 | M4 | Riêng tư và quản lý cache | TODO | PR-029 |
| PR-033 | M4 | Thay đổi ngoài app và lỗi ổ | PARTIAL | PR-027, PR-029 |
| PR-034 | M4 | Gate G4 hiệu năng tổng thể | TODO | PR-031, PR-032, PR-033, PR-028 |
| PR-035 | M5 | Hoàn thiện UX và accessibility | PARTIAL | PR-034 |
| PR-036 | M5 | Đóng gói self-contained | DONE | PR-035 |
| PR-037 | M5 | Regression và known issues | PARTIAL | PR-036 |
| PR-038 | M5 | Nghiệm thu workflow thật | TODO | PR-037 |
| PR-039 | M5 | Bàn giao và baseline phát hành | PARTIAL | PR-038 |

## Chi tiết thực thi

Cập nhật cả bảng trên và trường trạng thái khi đổi task; đây là cùng một nguồn tài liệu, cần giữ nhất quán. Bằng chứng có thể là file báo cáo, commit, log hoặc ghi nhận nghiệm thu.

### PR-001 — Khảo sát môi trường

- Trạng thái: TODO
- Mốc: M0; ưu tiên: P0
- Phụ thuộc: —
- Phạm vi làm: Ghi OS, CPU/RAM, ổ nguồn/cache, DPI, phiên bản Photos/FastStone; xác định dải OS dự kiến.
- Hoàn thành khi: Có hồ sơ cấu hình và các mục chưa biết được ghi rõ.
- Đối chiếu yêu cầu/test: Q02,Q06
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-002 — Lập bộ ảnh mẫu và fixture

- Trạng thái: TODO
- Mốc: M0; ưu tiên: P0
- Phụ thuộc: PR-001
- Phạm vi làm: Xác định folder người dùng cung cấp; inventory format/pixel/size; tạo bộ test độc lập có manifest hash.
- Hoàn thành khi: Có bộ mẫu thực tế và fixture kiểm thử; không sửa ảnh gốc.
- Đối chiếu yêu cầu/test: Q01,Q07; T01,T20
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-003 — Đo baseline Photos/FastStone

- Trạng thái: TODO
- Mốc: M0; ưu tiên: P0
- Phụ thuộc: PR-001, PR-002
- Phạm vi làm: Chạy các điều kiện trong kế hoạch benchmark, ghi thứ tự chạy và cache; lưu raw data.
- Hoàn thành khi: Có median/P95/max hoặc giới hạn phương pháp đo rõ ràng; không suy diễn cơ chế Photos.
- Đối chiếu yêu cầu/test: NFR-01
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-004 — Chốt phạm vi và gate G0

- Trạng thái: TODO
- Mốc: M0; ưu tiên: P0
- Phụ thuộc: PR-003
- Phạm vi làm: Chốt mục tiêu đo, mặc định loại/chế độ, codec ban đầu và phạm vi thử.
- Hoàn thành khi: Biên bản G0 và decisions cập nhật; lựa chọn chưa chốt giữ nhãn đề xuất.
- Đối chiếu yêu cầu/test: G0
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-005 — Khởi tạo solution và build

- Trạng thái: DONE
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-004
- Phạm vi làm: Tạo WPF app, module boundary, cấu hình build và diagnostics sơ bộ.
- Hoàn thành khi: Build chạy cửa sổ trên môi trường dev, dependency/runtime ghi rõ.
- Đối chiếu yêu cầu/test: D01,D02
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-006 — Scanner và natural sort

- Trạng thái: DONE
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-005
- Phạm vi làm: Liệt kê incremental; Unicode, filter extension, snapshot order, exclude đích, không theo junction mặc định.
- Hoàn thành khi: T01/T09 pass; không chờ metadata cả folder trước xem.
- Đối chiếu yêu cầu/test: FR-01,FR-02
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-007 — Decoder adapter và metadata

- Trạng thái: DONE
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-005, PR-002
- Phạm vi làm: Decode preview/full; đóng stream hợp lý; EXIF orientation; kích thước/format/error.
- Hoàn thành khi: JPG/PNG fixture đúng orientation, lỗi ảnh cô lập; không giữ khóa cản Move.
- Đối chiếu yêu cầu/test: T05,T20,T26
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-008 — Viewer fit và 100%

- Trạng thái: IN_PROGRESS
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-007
- Phạm vi làm: Render fit, zoom/pan, DPI, trạng thái preview/full, tên đồng bộ bitmap.
- Hoàn thành khi: T05/T06 pass; 100% không báo nhầm khi còn preview.
- Đối chiếu yêu cầu/test: FR-03
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-009 — Scheduler và chống stale frame

- Trạng thái: IN_PROGRESS
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-006, PR-008
- Phạm vi làm: Generation token, ưu tiên, giới hạn concurrency/queue, bỏ result cũ.
- Hoàn thành khi: T02 pass qua decode cố tình chậm và đảo chiều liên tục.
- Đối chiếu yêu cầu/test: R02
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-010 — RAM cache prototype

- Trạng thái: IN_PROGRESS
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-009
- Phạm vi làm: Byte budget, pin frame hiện tại, eviction, preload lân cận, release bitmap.
- Hoàn thành khi: T07 pass; telemetry ghi cache bytes và tổng RAM riêng.
- Đối chiếu yêu cầu/test: FR-09
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-011 — Instrumentation benchmark

- Trạng thái: TODO
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-005
- Phạm vi làm: Ghi thời gian key/decode/frame/IO, xuất raw results và cấu hình; phân biệt ready/presented.
- Hoàn thành khi: Đo tái lập được, log không tự gửi ra mạng.
- Đối chiếu yêu cầu/test: NFR-01,NFR-04
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-012 — Benchmark và quyết định G1

- Trạng thái: TODO
- Mốc: M1; ưu tiên: P0
- Phụ thuộc: PR-003, PR-010, PR-011
- Phạm vi làm: So prototype với baseline, thử concurrency/preload, profile nếu fail.
- Hoàn thành khi: Có báo cáo G1 PASS/FAIL và hạn chế; không tuyên bố nhanh hơn nếu thiếu bằng chứng.
- Đối chiếu yêu cầu/test: G1
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-013 — Schema phiên và persistence

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-012
- Phạm vi làm: Session/Image/Category/ReviewAction; migration/version; transaction lưu nhãn.
- Hoàn thành khi: T18 nhãn/vị trí pass; DB lỗi được báo và không mất im lặng.
- Đối chiếu yêu cầu/test: FR-08
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-014 — Command phân loại theo phím tắt

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-013, PR-009
- Phạm vi làm: Mũi tên chỉ duyệt/để nguyên ở nguồn; Enter Move vào folder 2; Delete gửi Recycle Bin; chống key repeat và auto-next sau commit.
- Hoàn thành khi: T03 pass; xử lý lỗi lưu nhãn không báo đã lưu giả.
- Đối chiếu yêu cầu/test: FR-04
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-015 — Skip, history và Undo thao tác file

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-014
- Phạm vi làm: State riêng skip, history navigation, Ctrl+Z cho Move khi fingerprint còn khớp; không Undo Recycle Bin tự động.
- Hoàn thành khi: Bộ đếm đúng; Undo trả nhãn và vị trí theo UX đã định nghĩa.
- Đối chiếu yêu cầu/test: FR-05,FR-06
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-016 — Focus và bộ shortcut

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-014
- Phạm vi làm: Shortcut scoped focus, text input exclusion, numpad, fullscreen/escape.
- Hoàn thành khi: T04 pass; không phân loại khi nhập tên/tìm kiếm.
- Đối chiếu yêu cầu/test: NFR-03
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-017 — Giao diện review và bộ đếm

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-015, PR-016
- Phạm vi làm: Ba nút, trạng thái viewer, loading/error/finished, counters review vs file operations.
- Hoàn thành khi: Tên/frame đúng; trạng thái loading không cho áp lệnh vào ảnh cũ.
- Đối chiếu yêu cầu/test: 02-UX.md
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-018 — Tìm lọc và thumbnail strip

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-017
- Phạm vi làm: Filter tên/loại/chưa xử lý/skip; virtualize strip; tải thumbnail ưu tiên thấp.
- Hoàn thành khi: T22 pass; lọc không làm mất ID hiện tại hoặc sai counter.
- Đối chiếu yêu cầu/test: FR-11
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-019 — Cấu hình folder đích

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-013, PR-017
- Phạm vi làm: Cấu hình folder 2 và validate path; loại 1 không có folder/action riêng, loại 3 luôn là Windows Recycle Bin; Enter/Delete thực hiện ngay.
- Hoàn thành khi: Setting được lưu; đích invalid bị chặn với lý do rõ.
- Đối chiếu yêu cầu/test: FR-12
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-020 — Gate G2 review/resume

- Trạng thái: TODO
- Mốc: M2; ưu tiên: P1
- Phụ thuộc: PR-015, PR-016, PR-018, PR-019
- Phạm vi làm: Chạy workflow bàn phím, thoát/mở lại, rapid input và lỗi persistence.
- Hoàn thành khi: G2 report có test evidence; không lỗi phân loại sai ID.
- Đối chiếu yêu cầu/test: G2
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-021 — File operation planner

- Trạng thái: TODO
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-020
- Phạm vi làm: Resolve scope, nguồn/đích, conflict policy, relative paths, no overwrite và stable operation ID.
- Hoàn thành khi: T09/T10 pass trên fixture; chỉ lập kế hoạch, chưa mutate ảnh thật.
- Đối chiếu yêu cầu/test: FR-07
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-022 — Journal và state machine

- Trạng thái: TODO
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-021
- Phạm vi làm: PREPARED/VERIFIED/COMMITTED/errors; lưu trước/sau mutation; fault injection hooks.
- Hoàn thành khi: Transition hợp lệ và restart đọc được pending; nêu DB/filesystem không atomic chung.
- Đối chiếu yêu cầu/test: NFR-05
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-023 — Same-volume Move

- Trạng thái: TODO
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-022
- Phạm vi làm: Move không overwrite, source validation, journal, error per file.
- Hoàn thành khi: T11/T14 pass bằng fixture; hash không đổi.
- Đối chiếu yêu cầu/test: 05-DATA-SAFETY.md
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-024 — Cross-volume Move (ngoài MVP)

- Trạng thái: DEFERRED
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-022
- Phạm vi làm: Không thuộc workflow MVP; chỉ mở lại nếu cần chuyển giữa volume khác nhau.
- Hoàn thành khi: T12/T13/T15/T27 pass; kiểm thử đúng hai volume hoặc ghi chưa test.
- Đối chiếu yêu cầu/test: R03
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-025 — Undo file operations

- Trạng thái: TODO
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-023, PR-024
- Phạm vi làm: Đối chiếu fingerprint/path, no overwrite, Undo Copy không xóa đích đã sửa.
- Hoàn thành khi: T17/T27 pass; conflict không làm mất bản mới.
- Đối chiếu yêu cầu/test: FR-06
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-026 — Recovery và khóa phiên

- Trạng thái: TODO
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-022, PR-025
- Phạm vi làm: Reconcile source/temp/dest, instance lock, không replay mù, journal repair có bằng chứng.
- Hoàn thành khi: T16/T18/T19 pass tại từng ranh giới fault injection.
- Đối chiếu yêu cầu/test: NFR-05
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-027 — Hàng đợi và tích hợp chế độ (ngoài MVP)

- Trạng thái: DEFERRED
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-026, PR-019
- Phạm vi làm: MVP không có hàng đợi/deferred/copy; Enter và Delete là thao tác tức thời.
- Hoàn thành khi: T28 pass; nhãn và trạng thái chuyển hiển thị riêng.
- Đối chiếu yêu cầu/test: FR-07
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-028 — Gate G3 dữ liệu

- Trạng thái: TODO
- Mốc: M3; ưu tiên: P0
- Phụ thuộc: PR-027
- Phạm vi làm: Chạy full data safety matrix, hash manifest trước/sau, xem lỗi mở.
- Hoàn thành khi: G3 PASS trước Move ảnh thật; FAIL thì giới hạn bản thử ở đánh dấu.
- Đối chiếu yêu cầu/test: G3
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-029 — Disk cache có version

- Trạng thái: DONE
- Mốc: M4; ưu tiên: P1
- Phụ thuộc: PR-012, PR-013
- Phạm vi làm: Key/invalidation, temp-write, corruption recovery, quota/eviction.
- Hoàn thành khi: T08 pass; cache không trỏ nhầm gốc hoặc stale preview.
- Đối chiếu yêu cầu/test: FR-09
- Người phụ trách: Claude (phiên 2026-09-16)
- Bắt đầu / hoàn tất / cập nhật: 2026-09-16 / 2026-09-16 / 2026-09-16
- Bằng chứng: `PreviewImageService` ghi preview đã downscale (targetWidth>0) ra `%LocalAppData%\PhotoReview\cache` qua `DiskCacheStore` dùng chung với `ThumbnailCache` (temp-write atomic, prune LRU theo quota 4GB, corruption recovery giữ nguyên từ nhánh đọc cũ). Original mode (full-res) chủ động không ghi disk để tránh PNG-encode chậm hơn re-decode JPEG. Test: `DiskCacheStoreTests` (5 test, đồng bộ, kiểm soát timestamp) + `PreviewImageServiceDiskCacheTests` (5 test: write→hit, Original không ghi, corruption fallback, quota prune). Commit: `ad75f65`, `9e36d72`, `81c3d59`, `2e23c32` trên `feature/wave3-architecture-testing`.
- Blocker / bước tiếp theo: không còn blocker cho phần đã làm; PR-030 (prepare cả folder)/PR-031 (RAM pressure tuning toàn diện) vẫn là công việc riêng, chưa bắt đầu.
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-030 — Prepare cả folder

- Trạng thái: TODO
- Mốc: M4; ưu tiên: P1
- Phụ thuộc: PR-029, PR-010
- Phạm vi làm: Progress/cancel/resume prepare, giữ RAM theo budget, ưu tiên tương tác.
- Hoàn thành khi: Xem được khi prepare; vượt budget thì evict chứ không OOM.
- Đối chiếu yêu cầu/test: FR-10
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-031 — RAM pressure và I/O tuning

- Trạng thái: TODO
- Mốc: M4; ưu tiên: P1
- Phụ thuộc: PR-030, PR-027
- Phạm vi làm: Đo tranh chấp decode/cache/move, adaptive giảm background, soak.
- Hoàn thành khi: T07/T21 pass; không growth không giới hạn và không đói task hiện tại.
- Đối chiếu yêu cầu/test: R04,R05
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-032 — Riêng tư và quản lý cache

- Trạng thái: TODO
- Mốc: M4; ưu tiên: P1
- Phụ thuộc: PR-029
- Phạm vi làm: Clear cache/lịch sử, RAM-only, log policy, validate cleanup root.
- Hoàn thành khi: T23 pass; chức năng clear không chạm ảnh gốc; chế độ riêng tư được mô tả đúng.
- Đối chiếu yêu cầu/test: NFR-04
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-033 — Thay đổi ngoài app và lỗi ổ

- Trạng thái: TODO
- Mốc: M4; ưu tiên: P1
- Phụ thuộc: PR-027, PR-029
- Phạm vi làm: Watcher/reconcile, source missing/change, disconnect/cloud pending; stable list.
- Hoàn thành khi: T25 pass hoặc ghi NOT_TESTED theo môi trường; không nhảy ảnh âm thầm.
- Đối chiếu yêu cầu/test: FR-02
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-034 — Gate G4 hiệu năng tổng thể

- Trạng thái: TODO
- Mốc: M4; ưu tiên: P1
- Phụ thuộc: PR-031, PR-032, PR-033, PR-028
- Phạm vi làm: Chạy lại benchmark toàn ứng dụng và tổng RAM/cache, kiểm tra chất lượng.
- Hoàn thành khi: Có báo cáo so baseline; không lấy kết quả prototype thay cho app đầy đủ.
- Đối chiếu yêu cầu/test: G4
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-035 — Hoàn thiện UX và accessibility

- Trạng thái: TODO
- Mốc: M5; ưu tiên: P1
- Phụ thuộc: PR-034
- Phạm vi làm: Focus indicators, keyboard-only, labels, contrast, DPI, thông báo gọn.
- Hoàn thành khi: Review hoàn toàn bàn phím; trạng thái không chỉ phân biệt bằng màu.
- Đối chiếu yêu cầu/test: NFR-03
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-036 — Đóng gói self-contained

- Trạng thái: TODO
- Mốc: M5; ưu tiên: P1
- Phụ thuộc: PR-035
- Phạm vi làm: Build release x64, cấu hình dữ liệu ngoài binary, version, portable instructions.
- Hoàn thành khi: EXE chạy không console; T24 pass trong OS support.
- Đối chiếu yêu cầu/test: NFR-06
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-037 — Regression và known issues

- Trạng thái: TODO
- Mốc: M5; ưu tiên: P1
- Phụ thuộc: PR-036
- Phạm vi làm: Chạy ma trận test, severity, giới hạn codec/màu/performance, không đánh PASS cho chưa test.
- Hoàn thành khi: Report đầy đủ, không lỗi mất dữ liệu/phân loại sai còn mở.
- Đối chiếu yêu cầu/test: 06-TESTING.md
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-038 — Nghiệm thu workflow thật

- Trạng thái: TODO
- Mốc: M5; ưu tiên: P1
- Phụ thuộc: PR-037
- Phạm vi làm: Người dùng thử folder thực sau gate an toàn, ghi tốc độ và tính tiện dụng.
- Hoàn thành khi: Có kết quả nghiệm thu hoặc danh sách cần sửa, không tự coi là user đã duyệt.
- Đối chiếu yêu cầu/test: G5
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

### PR-039 — Bàn giao và baseline phát hành

- Trạng thái: TODO
- Mốc: M5; ưu tiên: P1
- Phụ thuộc: PR-038
- Phạm vi làm: EXE/source/build guide/phím tắt/benchmark/test report/known issues; cập nhật progress.
- Hoàn thành khi: Deliverable truy cập được, trạng thái và version đồng bộ.
- Đối chiếu yêu cầu/test: G5
- Người phụ trách: chưa phân công
- Bắt đầu / hoàn tất / cập nhật: — / — / 2026-09-13
- Bằng chứng: chưa có
- Blocker / bước tiếp theo: chưa bắt đầu; thực hiện khi phụ thuộc đạt.

## Backlog mở rộng — không tính vào tiến độ bản đầu

### FUT-009 — Settings UI và shortcut mapping

- Trạng thái: DEFERRED
- Phạm vi dự kiến: Nút Settings nhỏ; cấu hình phím mũi tên và phím action; ánh xạ từng action tới folder đích; Recycle Bin, cache, preload, zoom và resume.
- Hoàn thành khi: restart giữ cấu hình; test conflict shortcut, config hỏng, folder không hợp lệ và migration đều pass.
- Yêu cầu an toàn: hiển thị rõ thao tác tức thời, validate quyền ghi, chặn folder nguồn, có reset mặc định và lưu config nguyên tử.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-001 — So sánh hai ảnh và đồng bộ zoom

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Review vùng chi tiết cùng vị trí; kiểm tra khác kích thước/orientation.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-002 — Nhóm tên (1)/(2) và trùng hash

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Tách giống tên/giống bytes/gần giống; không tự chọn theo MB.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-003 — Near-duplicate và gợi ý nét

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Đánh giá false positives trên bộ mẫu; chỉ gợi ý, không tự xóa.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-004 — Remap phím, profile và export

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Kiểm tra xung đột shortcut, schema export và profile theo folder.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-005 — Codec bổ sung

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Inventory nhu cầu RAW/HEIC/AVIF; kiểm tra license/dependency và máy sạch.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-006 — Tích hợp Explorer và installer

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Menu context opt-in, uninstall sạch, không tự đổi association.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-007 — HDR/màu chuyên nghiệp

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Bộ mẫu và màn hình chuẩn, đặc tả pipeline màu trước cam kết.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

### FUT-008 — Controller và nhiều nền tảng

- Trạng thái: DEFERRED
- Phụ thuộc: G5; nhu cầu người dùng và đặc tả bổ sung.
- Phạm vi dự kiến: Chỉ lên lịch khi có nhu cầu; đánh giá chi phí backend/UI.
- Hoàn thành khi: có tiêu chí riêng được lập trước triển khai, test và bằng chứng tương ứng.
- Người phụ trách / lịch / bằng chứng: chưa có.

## Đợt preload/Next đã xác nhận 2026-09-15

| Task | Trạng thái | Bằng chứng / việc còn lại |
|---|---|---|
| P01 — đo cache/preload | DONE | Log `ShowImage cache-state`, `Preload progress`, `Preload paused for memory`; metrics RAM/inflight/disk/queue/UI assignment. |
| P02 — cache hit không hiện loading | DONE | `ShowImageAsync` dùng bitmap RAM ngay; WPF probe trên ảnh thật đã PASS tại ảnh 2/147. |
| P03 — scheduler liên tục | DONE | Next đổi priority mà giữ decode đang chạy; đổi folder/Explorer reindex làm mới scheduler. |
| P04 — worker/yield | DONE | 8 worker thực; yield sau mỗi 8 job. Benchmark 30 ảnh thật: 2 worker 27.9s, 4 worker 18.9s, 8 worker 15.3s. |
| P05 — byte folder trước preload | DONE | Tổng source bytes được tính trước `ShowImageAsync` đầu tiên. |
| P06 — cache identity/epoch | DONE | Key theo fingerprint/mode/width; epoch ngăn decode cũ refill sau Clear/Move/Delete/đổi folder. |
| P07 — test hành vi | DONE | Test key, priority, action sequence và 30 lượt WPF warm Next trên ảnh thật PASS; không thao tác Move/Delete trực tiếp trên ảnh nguồn. |
| P08 — benchmark local | DONE | Folder ảnh thật mới dưới `C:\Xiuren\[[DONE]`. Sau sửa metadata decode, 30 lượt WPF warm Next: median 11ms, P95 25ms, max 34ms; trước sửa: median 1751ms, P95 4315ms, max 4550ms. Cả 30 lượt không hiện loading hoặc sai index. |
| P09 — release/review | DONE | Test Release và probe WPF 30 lượt PASS; publish đúng thư mục và push `origin/master` theo quy trình bắt buộc. |

Benchmark decoder được chạy bằng `dotnet run --project PhotoReview.Tests -c Release -- --preload-bench <folder> <workers>`; probe WPF read-only bằng `dotnet run --project PhotoReview.Tests -c Release -- --ui-next-probe <folder>`.

## Đợt benchmark nhiều profile trong ứng dụng — 2026-09-15

Mục tiêu: thêm benchmark chạy trong PhotoReview và CLI, so sánh nhiều profile load/navigation/cache/RAM/action race trên ảnh thật local. `Original` chỉ chạy correctness lane, không xếp hạng tốc độ. Action Move/Delete/Copy luôn dùng temp copy; không retry; điều hướng chỉ một lần và bắt đầu trước filesystem operation.

| Task | Trạng thái | Agent/phạm vi | Tiêu chí hoàn thành |
|---|---|---|---|
| BM-01 — Benchmark models | DONE | Agent benchmark_models | Model immutable, validation, serialization; build Release pass |
| BM-02 — Profile registry | DONE | Agent benchmark_models | Nhiều profile, ID ổn định, Original correctness-only |
| BM-03 — Benchmark engine | DONE | Agent benchmark_engine | Phase loop, progress, cancellation, report |
| BM-04 — Tích hợp decoder/cache | DONE | Agent benchmark_engine | Engine contract dùng được với decoder/cache qua delegate |
| BM-05 — Action race workload | TODO | Agent chính/agent audit | Move/Delete/Copy trên temp copy, đo navigation trước action, không retry/double navigation |
| BM-06 — Metrics/ranking | DONE | Agent benchmark_engine | P50/P95/P99 và ranking loại Original |
| BM-07 — Benchmark window | DONE | Agent chính | Nút Benchmark mở cửa sổ chạy read-only profile Fast Sequential |
| BM-08 — Mở log/report | DONE | Agent chính | Có nút mở report gần nhất và mở result folder; JSON export |
| BM-09 — CLI benchmark | DONE | Agent benchmark_tests_cli | list/benchmark/benchmark-all/benchmark-actions và report JSON |
| BM-10 — Unit test engine/profile | DONE | Agent benchmark_tests_cli | Profile registry, ranking và scenario tests đã gọi trong runner |
| BM-11 — Regression lỗi lịch sử | DONE | Agent benchmark_tests_cli | Action race/index/double-navigation/no-retry regression pass |
| BM-12 — Ảnh thật local | DONE | Agent real_benchmark_probe | Folder con 98 ảnh, 773,065,292 bytes; nguồn read-only; reports đã ghi |
| BM-13 — UI probe | DONE | Agent real_benchmark_probe | 30/30 warm navigation, median 12ms, P95 21ms, max 21ms |
| BM-14 — Audit race toàn hệ thống | DONE | Agent audit | Reviewed preload/cache/Explorer/action/logging/cancel; BenchmarkEngine now logs cancellation and phase failures; regression coverage exists |
| BM-15 — Logging chi tiết | DONE | Agent audit | Benchmark start/sample/complete/cancel/failure records include runId, profile, phase, iteration, elapsed and status |
| BM-16 — Diagnostics integration | DONE | Agent chính | Benchmark window hiển thị profile/progress/P50/P95 và đường dẫn report; DiagnosticsWindow hiện hữu giữ metrics viewer |
| BM-17 — Resource matrix | DONE | Agent chính | Có profile workers/RAM/ảnh lớn; real preload benchmark đã có trong LocalImageBenchmark |
| BM-18 — Tài liệu | DONE | Agent audit | Added profile interpretation, debug workflow, result evidence and privacy guidance in `BENCHMARK-PROFILES.md` and `BENCHMARK-RESULTS.md` |
| BM-19 — Merge/audit | DONE | Agent chính | Engine/image executor/CLI/UI merged; Release build and tests pass |
| BM-20 — Release validation | DONE | Agent chính | Release test, publish đúng thư mục và push origin/master đều pass |

Profile bắt buộc: Instant Review, Fast Sequential, Fast Balanced, Fast Aggressive, Preview Light/Balanced/Quality/High Quality, No Preload Baseline, Nearby Only, Full Folder Warm, Large Folder Safe, Huge Image Safe, SSD High Throughput, HDD Conservative, Network Safe, Low Memory, RAM Maximizer, Rapid Key Press, Random Navigation, Action During Decode, Move Race, Delete To Recycle Bin Race, Copy During Decode, Interleaved Actions, Explorer Reindex, Logging On/Off, Recommended Auto và Original Correctness.

Quy tắc phối hợp: agent chỉ sửa phạm vi được giao; agent chính hợp nhất thay đổi và xử lý conflict. Nếu phát sinh phạm vi mới ngoài BM-01..BM-20, cập nhật plan và dừng phần mới để xin xác nhận lại.

