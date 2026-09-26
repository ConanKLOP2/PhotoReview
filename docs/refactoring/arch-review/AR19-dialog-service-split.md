# AR19 — Tách `IDialogService` và đưa `SkippedFilesWindow` qua dialog service

**Finding:** F23 · **Quyết định:** Q-AR10 · **Kích thước:** ~0.5 ngày, gộp vào PR AR13 (cùng `MainWindow.xaml.cs`) hoặc PR riêng `refactor/ar19-dialog-split` sau AR13 · **GUI:** không · **Agent:** sonnet

## Hiện trạng

- `src/PhotoReview.Core/Abstractions/IDialogService.cs`: 9 method — 4 prompt chung (`ShowConfirmation`, `ShowMessage`, `ShowError`, `PickFolder`) + 5 mở cửa sổ app (`ShowBatchReview`, `ShowRecovery`, `ShowDiagnostics`, `ShowSettings`, `ShowBenchmark`). Một implementation sản xuất `WpfDialogService` (App) resolve từng cửa sổ qua `IServiceProvider` (adapter có chủ đích). Fake trong tests phải hiện thực cả 9.
- `MainWindow.xaml.cs:700`: `SkippedFiles_Click` → `new SkippedFilesWindow(_viewModel.SkippedEntries) { Owner = this }.ShowDialog()` — cửa sổ duy nhất mở thẳng trong code-behind; các cửa sổ khác qua `IDialogService`. `DialogBoundaryTests` (Architecture) hiện cho phép `*Window.xaml.cs` gọi `MessageBox`/`new *Window` → không bắt.

## Q-AR10 (phần AR19) — phương án

| | (a) Giữ, chỉ sửa `SkippedFilesWindow` | (b) Tách 2 interface |
|---|---|---|
| Việc | Thêm `void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)` vào `IDialogService`; `WpfDialogService` hiện thực; `MainWindow:700` → `_viewModel.ShowSkippedFiles()` | `IUserPrompts` (4 prompt) + `IAppWindows` (6 cửa sổ kể cả SkippedFiles); `IDialogService : IUserPrompts, IAppWindows` giữ tạm để không sửa 20+ chỗ tiêm; Core chỉ dùng `IUserPrompts` (`FileActionService`? kiểm tra: `grep -rn IDialogService src/PhotoReview.Core` — nếu Core không dùng thì `IAppWindows` chuyển sang App) |
| Công | 1 giờ | 0.5 ngày |
| Đổi hành vi | 0 | 0 |
| Ưu | Nhất quán, testable | Fake test nhỏ hơn; Core không biết tên cửa sổ WPF |
| Nhược | Interface vẫn 10 method | Thêm 2 kiểu; giá trị chỉ hiện khi có UI thứ hai (không có kế hoạch) |

**Khuyến nghị: (a)**, làm cùng AR13 để tránh xung đột trên `MainWindow.xaml.cs`. (b) chỉ khi Core thực sự phụ thuộc vào 5 method cửa sổ (kiểm tra bằng grep khi làm; nếu Core không dùng → di chuyển `IAppWindows` sang App là đủ, không cần (b) đầy đủ).

## Thay đổi (a)

1. `IDialogService.ShowSkippedFiles(IReadOnlyList<SkippedEntry>)`; `WpfDialogService`: `new SkippedFilesWindow(entries) { Owner = Application.Current?.MainWindow }.ShowDialog()` (cùng cách lấy Owner như các cửa sổ khác trong file này).
2. `MainViewModel.ShowSkippedFiles()` gọi `_dialogService.ShowSkippedFiles(SkippedEntries)`; `MainWindow:700` gọi VM.
3. Cập nhật fake `IDialogService` trong `tests/PhotoReview.TestSupport*` (thêm method no-op + đếm).
4. `DialogBoundaryTests`: siết rule — `new [A-Z]\w*Window\(` chỉ được phép trong `Services/WpfDialogService.cs` và `App.xaml.cs` (allowlist file text như các test khác). **Mutation:** khôi phục dòng cũ ở `MainWindow` → đỏ.

## Verification / Acceptance

Gate chung; `grep -rn "new SkippedFilesWindow" src` = 1 (trong `WpfDialogService`).
