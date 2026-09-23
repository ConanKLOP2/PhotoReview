# ADR 0005: Quy tắc thread affinity cho tầng App (không `ConfigureAwait(false)` trong App)

- **Trạng thái:** Chấp nhận (Accepted), 2026-09-23 (Q-AR2). Implemented by AR04 (PR #37) — đã bỏ 47 `ConfigureAwait(false)` trong `src/PhotoReview.App`, thêm `AppThreadAffinityTests`, guard Debug trên `ReviewCatalog` và metric `CrossThreadPresentCount`; perf gate (so với baseline AR02e) và nghiệm thu GUI trên máy thật còn chờ.
- **Ngày:** 2026-09-23
- **Thực hiện bởi:** AR04 (`docs/refactoring/arch-review/AR04-ui-thread-affinity.md`)
- **Thay thế phạm vi:** phần "UI-thread audit" của WD01

## Bối cảnh

`ReviewCatalog` được ghi rõ là không thread-safe, chỉ dùng trên UI thread (`ReviewCatalog.cs:11`). Tuy vậy tầng App (ViewModel, Coordinator) có 47 `ConfigureAwait(false)`. Sau các continuation đó, code tiếp tục sửa catalog trên thread pool, ví dụ `FolderLoadCoordinator` gọi `_catalog.Reset(entries)` sau `await Task.Run(...).ConfigureAwait(false)` trong khi handler phím trên UI thread vẫn đọc catalog. `ImagePresenter.RemoveMissingCatalogItemAsync` và `MainViewModel.NotifyNavigationStateChanged` cũng chạy ngoài UI thread. Để bù, `WpfPresentationSink` phải `Dispatcher.Invoke` đồng bộ cho mọi lần cập nhật, chặn một thread pool mỗi lần.

`task_on_progress.md` đã ghi nhận vấn đề nhưng gác lại vì gắn với WD01, và WD01 lại bị chặn bởi OC14 (Undo), dù thread affinity không phụ thuộc ngữ nghĩa Undo.

## Quyết định

1. **Tầng App không dùng `ConfigureAwait(false)`.** Mọi continuation quay về `SynchronizationContext` của WPF. Trạng thái UI (`ReviewCatalog`, `ViewerState`, `CompareViewModel`, `MainViewModel`) chỉ bị sửa trên UI thread.
2. **Core, Imaging, Platform.Windows luôn dùng `ConfigureAwait(false)`** và đặt công việc CPU/I-O nặng trong `Task.Run` hoặc I/O bất đồng bộ thật. Không làm việc nặng đồng bộ trước `await` đầu tiên của một method mà App gọi.
3. Không chặn đồng bộ trên Task trong App (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).
4. Quy tắc được khoá bằng architecture test (quét source) và một kiểm tra Debug trên `ReviewCatalog` (bind thread chủ ở composition root).

## Hệ quả

- Hết data race trên catalog ở luồng mở folder, xoá file mất, đổi folder nhanh.
- `WpfPresentationSink` gần như luôn chạy nhánh gọi trực tiếp, không còn `Dispatcher.Invoke` đồng bộ trên hot path.
- Continuation được post ở `DispatcherPriority.Normal` thay vì `Send` của `Dispatcher.Invoke`; khi giữ phím điều hướng, thứ tự công việc có thể thay đổi. Vì vậy AR04 bắt buộc so sánh P95 `key → present` với baseline AR02e (đồ thị production) và chỉ chấp nhận nếu chậm đi không quá 5 % và không quá 1 ms.
- WD01 không còn phụ thuộc OC14.

## Phương án đã cân nhắc

- **Giữ nguyên, marshal thủ công qua `IUiScheduler` tại từng chỗ sửa catalog:** nhiều điểm chạm, dễ sót, không kiểm tra tự động được.
- **Làm `ReviewCatalog` thread-safe (lock):** che race dữ liệu nhưng vẫn phát `PropertyChanged` ngoài UI thread và vẫn cần `Dispatcher.Invoke` trong sink; thêm chi phí lock trên hot path.
