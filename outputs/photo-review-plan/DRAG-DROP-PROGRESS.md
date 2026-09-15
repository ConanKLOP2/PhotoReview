# PhotoReview — Tiến độ kéo-thả

## Tổng quan

- Trạng thái: PARTIAL (2 DONE, 8 PARTIAL, 1 TODO theo bảng task đã đối chiếu)
- Cập nhật gần nhất: 2026-09-15
- Tiến độ: 2/11 task có đủ evidence; 8 task cần test hành vi/GUI bổ sung
- Kế hoạch chi tiết: [DRAG-DROP-PLAN.md](DRAG-DROP-PLAN.md)
- Task list: [DRAG-DROP-TASKS.md](DRAG-DROP-TASKS.md)

## Bảng tiến độ

> Đồng bộ với [DRAG-DROP-TASKS.md](DRAG-DROP-TASKS.md). Assertion source-wiring không thay thế manual Explorer acceptance.

| ID | Trạng thái | Bằng chứng | Ghi chú |
|---|---|---|---|
| DD-01 | PARTIAL | `DragDropInputService` contract | Multi-input/invalid matrix chưa đủ |
| DD-02 | DONE | `IsSupportedImage` dùng trong scan | Một nguồn filter extension |
| DD-03 | DONE | `DragDropInputService.Parse` | Parser độc lập, có test |
| DD-04 | PARTIAL | `MainWindow.xaml` + drag handlers | WPF events wired; GUI drop chưa kiểm |
| DD-05 | PARTIAL | `LoadFolderAsync(folder, initialPath)` | Chọn ảnh có contract; overlapping load chưa kiểm |
| DD-06 | PARTIAL | Cancellation pipeline hiện có | Old-load error có thể ghi đè new UI |
| DD-07 | PARTIAL | StatusText + `AppLog` | Warning/log có; feedback GUI chưa kiểm |
| DD-08 | PARTIAL | `PhotoReview.Tests` | Parser cơ bản/wiring PASS; multi-input thiếu |
| DD-09 | TODO | — | Manual Explorer Unicode/large-folder chưa có evidence |
| DD-10 | PARTIAL | Release tests/build PASS | GUI/cache/preload regression chưa đủ |
| DD-11 | PARTIAL | `README.md` | Usage đã có; release notes chưa chứng minh |

## Nhật ký thực hiện

### 2026-09-15

- Đã thêm parser kéo-thả độc lập và test folder/ảnh/invalid input.
- Đã tích hợp `AllowDrop`, `PreviewDragOver`, `Drop` vào cửa sổ chính.
- Đã dùng lại pipeline `LoadFolderAsync`, cache, preload và Explorer ordering.
- Đã build solution thành công và toàn bộ test hiện có PASS.

## Quy ước trạng thái

- `TODO`: chưa bắt đầu.
- `IN_PROGRESS`: đang thực hiện.
- `BLOCKED`: có blocker cụ thể, ghi rõ trong ghi chú.
- `DONE`: đã có bằng chứng kiểm thử/kiểm tra.
