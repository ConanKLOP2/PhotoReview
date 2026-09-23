# Kiến trúc PhotoReview

Tài liệu này mô tả cấu trúc sau đợt refactor, các luồng runtime chính và nơi bảo vệ INV-1…INV-12. Chi tiết cache và benchmark nằm trong [APP-MECHANISMS-VI.md](APP-MECHANISMS-VI.md).

## Các project và hướng phụ thuộc

```text
PhotoReview.App (WPF composition root, View, ViewModel, coordinator)
  ├─> PhotoReview.Core (catalog, settings, session, file action, abstractions)
  ├─> PhotoReview.Imaging (decode, cache, preload)
  ├─> PhotoReview.Imaging.TurboJpeg (đăng ký khi TurboJpegAvailability.Probe() thành công — AR01)
  ├─> PhotoReview.Platform.Windows (Explorer, Recycle Bin, memory/monitor)
  └─> PhotoReview.Benchmarking (benchmark dùng chung)

PhotoReview.Imaging ─> PhotoReview.Core
PhotoReview.Platform.Windows ─> PhotoReview.Core
PhotoReview.Imaging.TurboJpeg ─> PhotoReview.Imaging + PhotoReview.Core
```

`PhotoReview.Core` không phụ thuộc WPF. `MainWindow` giữ phần view/input; `MainViewModel`, coordinator và service điều phối hành vi. `App.xaml.cs` là composition root đăng ký dependency và nối implementation Windows/WPF vào abstraction.

## Khoảng trống đã biết (rà soát 2026-09-23)

Chi tiết và kế hoạch: [`refactoring/ARCH-REVIEW-SUMMARY.md`](refactoring/ARCH-REVIEW-SUMMARY.md).

- **Hai composition root** (F2, F3): test và `Benchmark.Cli --perf-session` dựng `MainViewModel` qua `MainWindowHelpers.CreateTestViewModel` (không preload, cache 64 MiB, không disk cache), khác đồ thị production mô tả ở đây. → AR02.
- **Disk cache bỏ qua `IAppPaths`** (F4): preview/thumbnail cache luôn ở `%LOCALAPPDATA%\PhotoReview`, không theo `PHOTOREVIEW_DATA_ROOT`. → AR02a.
- **Thread affinity chưa được thực thi** (F5): tầng App dùng `ConfigureAwait(false)` rồi sửa `ReviewCatalog` ngoài UI thread. Quy tắc đề xuất: ADR 0005. → AR04.

## Luồng mở folder và trình diễn ảnh

```text
MainWindow / command line / drag-drop
  -> MainViewModel
  -> FolderLoadCoordinator
       -> ReviewCatalog scan/fallback order
       -> ExplorerOrderService (song song)
       -> GenerationClock loại kết quả cũ
  -> ImagePresenter
       -> ThumbnailCache (Preview phase đầu)
       -> PreviewImageService
            -> ImageCacheKey + RAM/disk cache
            -> ImageDecoderFactory
                 -> WPF | WIC Direct | TurboJPEG
                 -> FallbackImageDecoder -> WPF
       -> WpfPresentationSink -> View binding
  -> PreloadController / PreloadScheduler (nền)
```

Mở trực tiếp một file chờ Explorer snapshot để giữ thứ tự native trước frame đầu. Mở folder trình diễn fallback trước, sau đó chỉ áp dụng snapshot còn hợp lệ. `GenerationClock` và token presentation chặn kết quả của folder/ảnh cũ. `ImagePresenter` là choke point trình diễn và cập nhật session; view không tự decode.

## Luồng thao tác file và Undo

```text
Input/action profile
  -> MainViewModel (advance catalog/present kế tiếp)
  -> FileActionService (gate một action)
       -> OperationJournal: Prepared
       -> IFileSystem / Recycle Bin
       -> OperationJournal: Committed hoặc Failed
  -> UndoService / RecoveryRetryService
  -> ReviewCatalog restore/reload khi cần
  -> SessionWriter (debounce, atomic SessionStore)
```

Ảnh kế tiếp được advance đúng một lần trước I/O. Nếu action thất bại, vị trí catalog được khôi phục. Nếu người dùng đã đổi folder, kết quả action cũ không được ghi undo hoặc sửa catalog mới. Delete dùng Recycle Bin; journal và fingerprint bảo vệ recovery/undo.

## Luồng decoder và cache

`PreviewImageService` chụp backend hiện hành khi tạo `ImageCacheKey`. Key gồm path chuẩn hóa, length, mtime, Original/target width, backend và orientation. RAM cache, disk cache và in-flight dedup dùng identity này. `ImageDecoderFactory` tạo backend được chọn; WIC Direct và TurboJPEG được bọc bởi `FallbackImageDecoder`, quay về WPF cho nhóm lỗi codec được phép và ghi metrics backend thực tế.

`SourceBytesCache` là tầng tùy chọn trước decode. Khi `UseSourceBytesCache=false`, service đọc stream nguồn như bình thường. Khi bật, preview, hash, preload và thumbnail JPEG dùng chung byte theo fingerprint; PNG thumbnail giữ stream fallback. Clear/evict dùng generation/epoch để tác vụ cũ không hồi sinh entry.

## Ánh xạ bất biến

| ID | Bất biến | Lớp bảo vệ chính |
|---|---|---|
| INV-1 | Cache identity đầy đủ; không present pixel cũ | `ImageCacheKey`, `PreviewImageService`, `ImagePresenter`, `GenerationClock` |
| INV-2 | Clear/evict vô hiệu hóa in-flight/persist cũ | `PreviewImageService`, `DiskCacheStore`, `ThumbnailCache`, `SourceBytesCache` |
| INV-3 | Advance một lần trước I/O; không present lại sau action | `MainViewModel`, `ImagePresenter` |
| INV-4 | Chỉ một file action chạy cùng lúc | `FileActionService`, `UndoService` |
| INV-5 | Action cũ không sửa catalog/undo sau đổi folder | `MainViewModel`, `GenerationClock`, `ReviewCatalog` |
| INV-6 | Recycle; journal Prepared → Committed/Failed; startup reconcile | `FileActionService`, `OperationJournal`, `RecoveryRetryService`, `UndoService` |
| INV-7 | Bỏ snapshot Explorer trễ khi catalog đã tương tác/đổi | `FolderLoadCoordinator`, `ReviewCatalog`, `ExplorerSnapshotValidator` |
| INV-8 | Handle decode dùng share ReadWrite/Delete, SequentialScan và đóng sớm | Các `IImageDecoder`, `ThumbnailCache` |
| INV-9 | Mở file chờ snapshot; mở folder present fallback trước | `FolderLoadCoordinator` |
| INV-10 | Logging mặc định tắt; tắt thì không tạo log | `FileLog`, `AppLog`, `AppSettings.LoggingEnabled` |
| INV-11 | Config hỏng được backup/reset; config khóa dùng default RAM | `SettingsStore` |
| INV-12 | Backend lỗi fallback WPF; cache không lẫn backend | `ImageDecoderFactory`, `FallbackImageDecoder`, `ImageCacheKey`, `ReviewMetrics` |

## Settings có ảnh hưởng kiến trúc

| Setting | Giá trị | Điểm áp dụng |
|---|---|---|
| `DecoderBackend` | `Wpf` mặc định; `WicDirect`; `TurboJpeg` | `SettingsStore` → `PreviewStateContext` → `PreviewImageService`/`ImageDecoderFactory`. Đổi khi đang xem làm clear cache và present lại ảnh hiện tại. |
| `ScalingQuality` | `HighQuality` mặc định; `Linear` | `MainViewModel` → `ViewerState` → WPF `RenderOptions.BitmapScalingMode`; không đổi decode/cache key. |
| `UseSourceBytesCache` | `false` mặc định | Được chụp lúc composition trong `App.xaml.cs`; bật cache byte 16 GiB cho service hỗ trợ. Thay đổi cần khởi động lại để toàn bộ dependency nhận cùng policy. |

## Build, test, benchmark và publish

```powershell
dotnet build PhotoReview.slnx -c Release
.\tools\verify-all.ps1
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-list-profiles
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-all 'C:\duong-dan\folder-anh' 'C:\duong-dan\ket-qua'
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

`verify-all.ps1` build solution, chạy năm project xUnit (loại `Category=Manual`), smoke file operation, fault injection, publish framework-dependent và verify artifact. Benchmark và T73 GUI acceptance vẫn là gate riêng vì test tự động không chứng minh hình ảnh đã render đúng trên desktop.
