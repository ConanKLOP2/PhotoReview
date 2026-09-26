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
      — cũng là backend của cửa sổ Benchmark trong app; assembly chỉ nạp khi mở cửa sổ (AR18, Q-AR9 a)

PhotoReview.Imaging ─> PhotoReview.Core
PhotoReview.Platform.Windows ─> PhotoReview.Core
PhotoReview.Imaging.TurboJpeg ─> PhotoReview.Imaging + PhotoReview.Core
```

`PhotoReview.Core` không phụ thuộc WPF. `MainWindow` giữ phần view/input; `MainViewModel`, coordinator và service điều phối hành vi. `App.xaml.cs` là composition root đăng ký dependency và nối implementation Windows/WPF vào abstraction.

**AppHost (AR02a–d, xong):** `PhotoReview.App.Composition.AppHost.BuildServices(overrides)` là điểm vào duy nhất dựng `IServiceProvider` — nó gọi `App.ConfigureServices`, áp `overrides` (nếu có), rồi `BuildServiceProvider()`. `App.App_Startup`, `Benchmark.Cli` (AR02c) và test tích hợp (AR02b) đều dùng cùng hàm này, chỉ khác override để thay `IPreloadController`, `IPresentationObserver` hay `IMoveOverride`. `MainWindow` giờ chỉ còn **một constructor DI duy nhất** (AR02d xoá các constructor phi-DI và `MainWindowHelpers.CreateTestViewModel` — composition root thứ hai không còn tồn tại); các trường public trước đây (`_files`, `_index`, `_compareSelectedPath`, `_metrics`, `_fileActionInProgress`) đã thành thuộc tính chỉ-đọc (`Files`, `CurrentIndex`, `CompareSelectedPath`, `Metrics`, `IsFileActionInProgress`). Các seam production hỗ trợ override: `ViewportSizeSource` (F3 — `MainWindow` gán `Get` về `GetViewportSize` ngay sau `InitializeComponent()`), `IPresentationObserver`/`NullPresentationObserver`, seam preload (`MainViewModelCompositionRoot` chỉ dựng `PreloadScheduler` thật khi không có `IPreloadController` nào được đăng ký) và seam di chuyển tệp (`IMoveOverride`, `null` ở production).

## Khoảng trống đã biết (rà soát 2026-09-23)

Chi tiết và kế hoạch: [`refactoring/ARCH-REVIEW-SUMMARY.md`](refactoring/ARCH-REVIEW-SUMMARY.md).

- ~~**Hai composition root** (F2, F3): test và `Benchmark.Cli --perf-session` dựng `MainViewModel` qua `MainWindowHelpers.CreateTestViewModel` (không preload, cache 64 MiB, không disk cache), khác đồ thị production mô tả ở đây.~~ → **AR02 xong (a–d):** chỉ còn một composition root (`AppHost.BuildServices`), xem mục AppHost ở trên.
- **Disk cache bỏ qua `IAppPaths`** (F4): `ThumbnailCache`/`PreviewImageService` nay đọc thư mục đĩa từ `IAppPaths.ThumbnailCacheDir`/`PreviewCacheDir` thay vì tự hard-code lại default giống hệt (AR02a, đã xong — không còn 2 nguồn sự thật). Nhưng bản thân `AppPaths` vẫn cố ý giữ hai thư mục cache dưới `%LOCALAPPDATA%\PhotoReview` bất kể `PHOTOREVIEW_DATA_ROOT` (chỉ Journal/Sessions/Log theo override) — cô lập cache đĩa theo `PHOTOREVIEW_DATA_ROOT` vẫn là khoảng trống mở, nằm ngoài phạm vi AR02a.
- ~~**Thread affinity chưa được thực thi** (F5)~~ — đã xử lý bởi AR04 (ADR 0005), xem mục [Threading](#threading-adr-0005). Còn lại: xác nhận perf gate/GUI trên máy thật.

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

Mở trực tiếp một file trình diễn ngay file đó (không chờ Explorer snapshot — truy vấn Explorer mất ~1–2 s với folder lớn; perf/startup), rồi áp thứ tự native khi snapshot tới mà giữ nguyên ảnh đang xem (`ReplaceOrder` giữ current, preload re-center theo index mới). Trong lúc snapshot chưa tới, điều hướng và file action chờ `FolderLoadCoordinator.PendingOrder` (tối đa timeout của truy vấn) trước khi tăng interaction generation, nên bước đầu tiên rời khỏi ảnh vẫn đi theo thứ tự Explorer và INV-7 không vứt snapshot. Truy vấn Explorer được `App_Startup` prefetch ngay sau khi dựng service (`IExplorerOrderProvider.Prefetch`), folder load join truy vấn đó; `ExplorerOrderService` đọc cả view bằng một `IEnumIDList` (`IFolderView::Items(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER)`, 512 PIDL/lần, tên file giải trong process) thay vì 2 lời gọi cross-process mỗi item (~60 ms thay vì ~1 s cho 1841 file; lỗi hoặc thiếu item thì quay lại đọc từng item). Snapshot nào đã có sẵn khi catalog sẵn sàng thì được áp trước frame đầu (cùng trạng thái cuối như đường trễ, không có frame fallback trung gian). Mở folder trình diễn fallback trước, sau đó chỉ áp dụng snapshot còn hợp lệ. `GenerationClock` và token presentation chặn kết quả của folder/ảnh cũ. `ImagePresenter` là choke point trình diễn và cập nhật session; view không tự decode.

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

**Recovery check.** Khi `RecoveryWindow` mở (và khi bấm Re-check), `RecoveryFileCheck` (Core, chỉ `GetFileStat`/`DirectoryExists`, không sửa file, journal vẫn append-only) chạy nền cho từng entry và đưa ra verdict. Path: Missing / Changed (size khác journal; nguồn thêm last-write) / Unreadable / Exists. Verdict cho Move/Copy Prepared/Failed (S = nguồn, D = đích):

| S | D | Verdict |
|---|---|---|
| exists | missing | `CanRetry` (chỉ verdict này bật nút Retry; `RecoveryRetryService` vẫn tự kiểm tra lại) |
| changed | missing | `SourceChanged` |
| missing | exists | `AlreadyDone` (đề nghị dismiss) |
| missing | changed | `DestinationChanged` |
| missing | missing | `Lost` |
| exists | exists | Copy: `AlreadyDone`; Move: `Conflict` (mọi tổ hợp cả hai còn: `Conflict`) |
| unreadable | any | `Unknown` (cũng khi entry không có đích) |

Committed: D exists = `AlreadyDone`, S và D missing = `Lost`, còn lại `Unknown`. Recycle: S missing = `RecycleUnverifiable`, S present = `NotRecycled`. Chi tiết trong comment của `RecoveryFileCheck`.

**Chế độ journal (ADR 0007, IO03, `AppSettings.JournalDurability`, đọc live cho mỗi lần ghi):** *Fast* (mặc định) mở stream không `WriteThrough`, `Flush()` thường sau mỗi bản ghi; *PowerLossSafe* giữ `WriteThrough` + `Flush(true)` và `FileActionService` ghi Prepared trên pool thread (`Task.Run`) rồi mới mutation/Committed — UI không bị chặn. Cả hai: Prepared → mutation → Committed, một bản ghi = một dòng JSON, đọc bỏ qua dòng hỏng.

## Threading (ADR 0005)

Tầng App (ViewModel, Coordinator, Services, Window) gắn với UI thread: **không bao giờ** `ConfigureAwait(false)` và không chặn đồng bộ trên Task (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`), nên mọi continuation quay về `SynchronizationContext` của WPF và `ReviewCatalog`, `ViewerState`, `CompareViewModel`, `MainViewModel` chỉ bị sửa trên UI thread. Core, Imaging và Platform.Windows thì ngược lại: luôn `ConfigureAwait(false)` và đặt việc CPU/I-O nặng trong `Task.Run` (hoặc I/O async thật) — phần đồng bộ trước `await` đầu tiên của một method mà App gọi phải nhẹ, vì nó chạy trên UI thread. Khi một callee có phần đầu đồng bộ nặng, sửa ở callee (hoặc App bọc lời gọi trong `Task.Run`), không thêm `ConfigureAwait(false)` vào App. Khoá bằng: `AppThreadAffinityTests` (quét source `src/PhotoReview.App`), guard Debug `ReviewCatalog.AssertOwnerThread` (composition root gọi `BindToCurrentThread()`; unit test không bind thì không kiểm tra), và metric `CrossThreadPresentCount` (số lần `WpfPresentationSink` phải `Dispatcher.Invoke` vì nhận cập nhật ngoài UI thread; kỳ vọng 0, hiện trong cửa sổ Diagnostics và `metrics.json` của perf session).

## Luồng decoder và cache

`PreviewImageService` chụp backend hiện hành khi tạo `ImageCacheKey`. Key gồm path chuẩn hóa, length, mtime, Original/target width, backend và orientation. RAM cache, disk cache và in-flight dedup dùng identity này. `ImageDecoderFactory` tạo backend được chọn; WIC Direct và TurboJPEG được bọc bởi `FallbackImageDecoder`, quay về WPF cho nhóm lỗi codec được phép và ghi metrics backend thực tế.

**Zoom (option A cho #43):** ngoài Fit, `ViewerState.Zoom` tính theo pixel GỐC — phần tử ảnh có kích thước `OriginalWidth × Zoom / DpiScale` DIP (100 % = 1 pixel nguồn trên 1 pixel thiết bị), không phụ thuộc bitmap đang hiển thị. `ImagePresenter` hiện preview ngay ở đúng kích thước đó, còn `ZoomDetailLoader` gọi `PreviewImageService.DecodeOriginalAsync` (thread riêng, không vào RAM LRU/disk cache) cho ảnh HIỆN TẠI rồi thay `Source` mà không đổi layout/scroll. Chỉ giữ tối đa một original (của ảnh hiện tại); điều hướng sẽ hủy decode chưa chạy, bỏ kết quả trễ (token navigation) và thả original. Về Fit thì dùng lại preview. Quyết định: [ADR 0008](adr/0008-zoom-source-pixel.md).

`SourceBytesCache` là tầng tùy chọn trước decode. Khi `UseSourceBytesCache=false`, service đọc stream nguồn như bình thường. Khi bật, preview, hash, preload và thumbnail JPEG dùng chung byte theo fingerprint; PNG thumbnail giữ stream fallback. Clear/evict dùng generation/epoch để tác vụ cũ không hồi sinh entry.

## Ánh xạ bất biến

| ID | Bất biến | Lớp bảo vệ chính |
|---|---|---|
| INV-1 | Cache identity đầy đủ; không present pixel cũ | `ImageCacheKey`, `PreviewImageService`, `ImagePresenter`, `GenerationClock` |
| INV-2 | Clear/evict vô hiệu hóa in-flight/persist cũ | `PreviewImageService`, `DiskCacheStore`, `ThumbnailCache`, `SourceBytesCache` |
| INV-3 | Advance một lần trước I/O; không present lại sau action | `MainViewModel`, `ImagePresenter` |
| INV-4 | Chỉ một file action chạy cùng lúc | `FileActionService`, `UndoService` |
| INV-5 | Action cũ không sửa catalog/undo sau đổi folder | `MainViewModel`, `GenerationClock`, `ReviewCatalog` |
| INV-6 | Recycle; journal Prepared → Committed/Failed; startup reconcile | `FileActionService`, `OperationJournal`, `RecoveryRetryService`, `UndoService`, `JournalStartupRecovery` (gọi từ `App` sau instance lock) |
| INV-7 | Bỏ snapshot Explorer trễ khi catalog đã tương tác/đổi | `FolderLoadCoordinator`, `ReviewCatalog`, `ExplorerSnapshotValidator` |
| INV-8 | Handle decode dùng share ReadWrite/Delete, SequentialScan và đóng sớm | Các `IImageDecoder`, `ThumbnailCache` |
| INV-9 | Mở file: present ngay, điều hướng/action chờ snapshot (`PendingOrder`) rồi đi theo thứ tự Explorer; mở folder present fallback trước | `FolderLoadCoordinator`, `MainViewModel` |
| INV-10 | Logging mặc định tắt; tắt thì không tạo log | `FileLog`, `AppLog`, `AppSettings.LoggingEnabled` |
| INV-11 | Config hỏng được backup/reset; config khóa dùng default RAM | `SettingsStore` |
| INV-12 | Backend lỗi fallback WPF; cache không lẫn backend | `ImageDecoderFactory`, `FallbackImageDecoder`, `ImageCacheKey`, `ReviewMetrics` |

## Settings có ảnh hưởng kiến trúc

| Setting | Giá trị | Điểm áp dụng |
|---|---|---|
| `DecoderBackend` | `WicDirect` mặc định (nhanh nhất, ADR 0001); `Wpf`; `TurboJpeg` | `SettingsStore` → `PreviewStateContext` → `PreviewImageService`/`ImageDecoderFactory`. Đổi khi đang xem làm clear cache và present lại ảnh hiện tại. |
| `ScalingQuality` | `HighQuality` mặc định; `Linear` | `MainViewModel` → `ViewerState` → WPF `RenderOptions.BitmapScalingMode`; không đổi decode/cache key. |
| `UseSourceBytesCache` | `false` mặc định | Được chụp lúc composition trong `App.xaml.cs`; bật cache byte 16 GiB cho service hỗ trợ. Thay đổi cần khởi động lại để toàn bộ dependency nhận cùng policy. |

## Kiểm tra cập nhật thủ công (chỉ dùng mạng ở đây)

PhotoReview.Core.Updates (IUpdateChecker, UpdateChecker, AppVersion, UpdateUrlPolicy) là **nơi duy nhất trong ứng dụng chạm tới mạng**, và chỉ chạy khi người dùng bấm **Cài đặt → Chung → Cập nhật → Kiểm tra cập nhật**. Không tự kiểm tra, không tự tải, không tự cài, không ép cập nhật. Một request GET https://api.github.com/repos/ConanKLOP2/PhotoReview/releases/latest (HTTPS, có User-Agent, timeout 10 giây, CancellationToken, async — không chặn UI) chỉ đọc 	ag_name/html_url; không gửi gì về ảnh hoặc đường dẫn. Bỏ qua draft/prerelease; so sánh số major.minor.patch với phiên bản đang chạy (BuildInfo.GetVersion, chấp nhận tiền tố  và +metadata). Kết quả: UpToDate / UpdateAvailable(version, url) / Failed(Offline, Timeout, RateLimited, BadResponse, InvalidVersion). Tầng HTTP tiêm được qua HttpMessageHandler nên test không dùng mạng thật. Nút **Mở trang tải về** chỉ hiện khi có bản mới và chỉ mở URL https://github.com/ConanKLOP2/PhotoReview/... (UpdateUrlPolicy) bằng trình duyệt mặc định.

## Runtime và GC (AR12c)

Workstation concurrent GC (mặc định .NET) là lựa chọn có chủ đích, không phải bỏ sót: (i) app một cửa sổ, độ trễ UI quan trọng hơn throughput; (ii) buffer pixel của `BitmapSource` nằm ở native heap (MIL), heap managed nhỏ nên Server GC không giúp. `InvariantGlobalization` **không** bật vì `vi.json` và sắp xếp `CurrentCulture` cần ICU. `TieredPGO`/`TieredCompilation` dùng mặc định .NET 10 (đã bật). Đo lại chỉ khi `%` thời gian GC pause trong perf session > 1 %.

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

## Chính sách ghi session và file không đọc được (ADR 0007)

- **Session** (`SessionStore.Save`) ghi nguyên tử (file tạm + rename) nhưng **không fsync** (`WriteAllTextAtomic(..., durable: false)`); Settings giữ `durable: true`. `SessionStore.Load` coi file session rỗng/hỏng là "không có session" (chỉ ghi log).
- **Quét folder**: `IFileSystem.EnumerateReadableFilesWithStat` thử mở đọc từng ảnh; file không đọc được (quyền, bị khóa, lỗi I/O) bị **bỏ qua có báo cáo** qua `IFolderLoadSink.OnFilesSkipped` (log + cảnh báo "Bỏ qua N file không đọc được" và cửa sổ danh sách), không dùng `IgnoreInaccessible` im lặng. Chính folder không liệt kê được vẫn là lỗi (`OnFailed`).
