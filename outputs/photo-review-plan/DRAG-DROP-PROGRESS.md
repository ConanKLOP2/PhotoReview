# PhotoReview — Tiến độ kéo-thả

## Tổng quan

- Trạng thái: DONE
- Cập nhật gần nhất: 2026-09-15
- Tiến độ: 11/11 task
- Kế hoạch chi tiết: [DRAG-DROP-PLAN.md](DRAG-DROP-PLAN.md)
- Task list: [DRAG-DROP-TASKS.md](DRAG-DROP-TASKS.md)

## Bảng tiến độ

| ID | Trạng thái | Bằng chứng | Ghi chú |
|---|---|---|---|
| DD-01 | DONE | `DragDropInputService` contract | Folder, ảnh đơn, nhiều input, invalid |
| DD-02 | DONE | `IsSupportedImage` dùng trong scan | Một nguồn filter extension |
| DD-03 | DONE | `DragDropInputService.Parse` | Parser độc lập, có test |
| DD-04 | DONE | `MainWindow.xaml` + drag handlers | WPF events trên Window |
| DD-05 | DONE | `LoadFolderAsync(folder, initialPath)` | Ảnh chọn đúng, folder mở đúng |
| DD-06 | DONE | Cancellation pipeline hiện có | Load mới hủy load cũ |
| DD-07 | DONE | StatusText + `AppLog` | Có warning/log cho input |
| DD-08 | DONE | `PhotoReview.Tests` | Parser và wiring pass |
| DD-09 | DONE | Parser test với JPG/invalid | Manual Explorer còn cần chạy trên máy người dùng |
| DD-10 | DONE | `dotnet build` + test runner | Build 0 warning/error, toàn bộ test PASS |
| DD-11 | DONE | `README.md` | Đã thêm hướng dẫn kéo-thả |

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
