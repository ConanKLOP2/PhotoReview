# ADR 0004: Tách project `Presentation` khỏi App (ST12 — điều tra)

- Ngày: 2026-09-20. Trạng thái: **đề xuất, chờ người dùng duyệt** (ST12 chỉ điều tra, không sửa source).
- Khuyến nghị: **chưa làm**. Không có consumer nào bỏ được tham chiếu tới `PhotoReview.App`.
- Thay thế bản nháp đầu của ADR này: bản đó lập luận "vòng phụ thuộc" (sai — App → Presentation là một chiều) và có ước lượng thời gian build chưa đo (đã bỏ).

## Câu hỏi (từ STRUCTURE-OPTIMIZE-TASKS ST12)

Nếu ViewModels/Coordinators/Sinks chuyển sang project riêng (`net10.0-windows`, không `Window`/XAML), thì `Benchmark.Cli` và các test project có bỏ được tham chiếu `PhotoReview.App` không, với chi phí nào?

## Số đo (đọc từ source tại `a65be80`, chưa build thử phương án)

**Tập ứng viên:** `App/ViewModels` (4 file, 1037 dòng, 5 type) + `App/Coordinators` (12 file, 1252 dòng, 13 type) = 16 file, 18 type, ~2,3k dòng. Tất cả đã `public`, nên không cần `InternalsVisibleTo` mới cho phần di chuyển.

**Phụ thuộc của tập ứng viên:** không dùng `System.Windows`/`DependencyProperty`/routed event (chỉ có một comment nhắc `System.Windows` trong `MainViewModel.cs`). Chúng dùng `Core` và `Imaging` (`PreviewImageService`, `ThumbnailCache`; `Imaging` là `net10.0-windows`) nên project mới vẫn phải là `net10.0-windows`.

**Không có vòng:** chỉ 6 file ngoài tập dùng chúng — `App.xaml.cs`, `Composition/MainViewModelCompositionRoot.cs`, `Converters/ViewerStretchModeConverter.cs`, `MainWindow.xaml.cs`, `MainWindowHelpers.cs`, `Services/WpfPresentationSink.cs` — và cả 6 ở lại App. `Core`/`Imaging`/`Platform.Windows` không tham chiếu App. Về kỹ thuật, di chuyển khả thi.

**Ai còn cần App sau khi di chuyển:**

| Consumer | Bằng chứng | Bỏ được tham chiếu App? |
|---|---|---|
| `Benchmark.Cli` | `new MainWindow` / `typeof(MainWindow)` ở 3 file (`WpfTestHost`, `LocalUiNextProbe`, `PerfSession`), cộng `PerfDispatcherHooks` (App) | Không — CLI dựng cửa sổ thật |
| `App.Tests` | 7/20 file dùng `MainWindow`/`StaTestHost`/`WpfDialogService`/`ShortcutRouter`… | Không |
| `Integration.Tests` | 6/11 file dùng `MainWindow`/WPF | Không |
| `Architecture.Tests` | tham chiếu App để kiểm rule kiến trúc | Không |

Số project bỏ được tham chiếu `App`: **0**. Thời gian build/test: **chưa đo** (cần thực hiện tách mới đo được; không suy diễn).

## Chi phí nếu làm

Di chuyển 16 file và đổi namespace `PhotoReview.App.ViewModels/Coordinators` (6 file consumer + test); thêm 1 project + `slnx` + reference ở App/tests; `MainViewModelCompositionRoot` (cần `IServiceProvider`) ở lại App.

## Quyết định đề xuất

Chưa tách. Lợi ích được nêu làm lý do (CLI/test không cần App) không đạt vì CLI dựng `MainWindow` thật và mỗi test project (App.Tests 7/20 file, Integration.Tests 6/11 file) còn file cần `MainWindow`/WPF. Chi phí nhỏ nhưng lợi ích đo được bằng 0.

**Điều kiện để mở lại:** có harness ở mức ViewModel không cần `MainWindow` (ví dụ CLI perf-session chạy headless qua `MainViewModel`), hoặc số đo cho thấy build/test App.Tests chậm rõ vì WPF. Khi đó làm task mới (không mở rộng ST12) và đo thời gian build/test trước/sau.

## Phát hiện phụ khi điều tra (thuộc ST06/ST10, đã xử lý riêng)

`Benchmark.Cli` còn 3 chỗ reflection vào type của App sau ST06, trong đó `typeof(App).GetNestedType("PerfDispatcherHooks")` trả về null từ ST03 (class đã chuyển ra `Diagnostics/`), làm `DispatcherLongOp` của CLI bị tắt im lặng. Đã thay bằng API công khai và thêm rule kiến trúc.
