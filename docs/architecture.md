# Kiến trúc PhotoReview

Tài liệu này mô tả cấu trúc sau đợt refactor, các luồng runtime chính và nơi bảo vệ INV-1…INV-12. Chi tiết cache và benchmark nằm trong [APP-MECHANISMS-VI.md](APP-MECHANISMS-VI.md).

## Các project và hướng phụ thuộc

```text
PhotoReview.App (WPF composition root, View, ViewModel, coordinator)
  ├─> PhotoReview.Core (catalog, settings, session, file action, abstractions; net10.0, không WPF)
  ├─> PhotoReview.Imaging (decode, cache, preload)
  ├─> PhotoReview.Imaging.Raw (metadata + JPEG preview của camera RAW, ADR 0009)
  ├─> PhotoReview.Imaging.LibRaw (P/Invoke libraw.dll: decode cảm biến, thumbnail dự phòng)
  ├─> PhotoReview.Imaging.TurboJpeg (đăng ký khi TurboJpegAvailability.Probe() thành công — AR01)
  ├─> PhotoReview.Platform.Windows (Explorer, Recycle Bin, memory/monitor)
  └─> PhotoReview.Benchmarking (benchmark dùng chung; backend của cửa sổ Benchmark, nạp khi mở cửa sổ — AR18)
        └─> PhotoReview.PerfAnalysis (phân tích perf session)

Imaging, Imaging.Raw, Imaging.LibRaw, Imaging.TurboJpeg, Platform.Windows ─> Core
Imaging.Raw, Imaging.LibRaw, Imaging.TurboJpeg ─> Imaging
Core ─> PhotoReview.Localization.Generator (source generator, netstandard2.0)
tools/PhotoReview.Benchmark.Cli ─> App, Benchmarking, Imaging*, Platform.Windows, PerfAnalysis
```

`PhotoReview.Core` không phụ thuộc WPF. `MainWindow` giữ phần view/input; `MainViewModel`, coordinator và service điều phối hành vi. `App.xaml.cs` là composition root đăng ký dependency và nối implementation Windows/WPF vào abstraction.

**AppHost (AR02a–d, xong):** `PhotoReview.App.Composition.AppHost.BuildServices(overrides)` là điểm vào duy nhất dựng `IServiceProvider` — gọi `App.ConfigureServices`, áp `overrides` (nếu có), rồi `BuildServiceProvider()`. `App.App_Startup`, `Benchmark.Cli` (AR02c) và test tích hợp (AR02b) đều dùng hàm này, chỉ khác override để thay `IPreloadController`, `IPresentationObserver` hay `IMoveOverride`. `MainWindow` chỉ có **một constructor DI** (AR02d); các trường public cũ đã thành thuộc tính chỉ-đọc (`Files`, `CurrentIndex`, `CompareSelectedPath`, `Metrics`, `IsFileActionInProgress`). Seam production: `ViewportSizeSource` (F3 — `MainWindow` gán `Get` về `GetViewportSize` ngay sau `InitializeComponent()`), `IPresentationObserver`/`NullPresentationObserver`, seam preload (`MainViewModelCompositionRoot` chỉ dựng `PreloadScheduler` thật khi không có `IPreloadController` nào được đăng ký) và `IMoveOverride` (`null` ở production).

## Khoảng trống đã biết (cập nhật 2026-09-27)

Rà soát kiến trúc 2026-09-23 và 2026-09-26 đã xong (tóm tắt: [`refactoring/HISTORY.md`](refactoring/HISTORY.md)): một composition root (`AppHost`), thread affinity thực thi bằng test (ADR 0005), `AppSettings` không còn persistence tĩnh.

- **Cache đĩa không theo `PHOTOREVIEW_DATA_ROOT`:** `AppPaths` cố ý giữ `cache\` và `thumbnails\` dưới `%LOCALAPPDATA%\PhotoReview` (chỉ Journal/Sessions/Log theo override) — cô lập cache đĩa theo data root vẫn là khoảng trống mở.
- **NAS / ổ chậm (Q-R29, mở):** `ThumbnailCache.BuildKey` stat đồng bộ trên UI thread mỗi lần điều hướng nguội; preload cả folder chưa có giới hạn ưu tiên I/O.

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
  -> PreloadControllerAdapter / PreloadScheduler (nền)
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

Committed: D exists = `AlreadyDone`, S và D missing = `Lost`, còn lại `Unknown`. Recycle: S missing = `RecycleUnverifiable` (hoặc `PermanentlyDeleted` nếu entry đánh dấu `Permanent`; capture lẫn cả hai = `PartiallyPermanentlyDeleted`), S present = `NotRecycled`. Chi tiết trong comment của `RecoveryFileCheck`.

**Copy lỗi giữa chừng (COPY-PARTIAL-01, quyết định c):** `File.Copy` không chứng minh được file đích do chính lần gọi tạo ra, nên một Copy hết đĩa giữa chừng để lại file đích dở và app không tự xóa (tránh xóa nhầm file lạ). Entry là Failed, Recovery hiện `Conflict`; người dùng tự xóa file dở rồi thử lại.

**Chế độ journal (ADR 0007, IO03, `AppSettings.JournalDurability`, đọc live cho mỗi lần ghi):** *Fast* (mặc định) mở stream không `WriteThrough`, `Flush()` thường sau mỗi bản ghi; *PowerLossSafe* giữ `WriteThrough` + `Flush(true)` và `FileActionService` ghi Prepared trên pool thread (`Task.Run`) rồi mới mutation/Committed — UI không bị chặn. Cả hai: Prepared → mutation → Committed, một bản ghi = một dòng JSON, đọc bỏ qua dòng hỏng.

**Thao tác đang chạy (Q-R27):** `JournalTransaction` giữ một marker `ILiveOperationRegistry` theo Id (app: named event `WindowsLiveOperationRegistry`, `Local\`, theo SID) từ trước Prepared đến sau Committed/Failed; reconcile (kể cả của process khác dùng chung journal) bỏ qua entry có marker sống. Process chết thì Windows đóng handle, nên entry sót lại vẫn được reconcile.

## Threading (ADR 0005)

Tầng App (ViewModel, Coordinator, Services, Window) gắn với UI thread: **không bao giờ** `ConfigureAwait(false)` và không chặn đồng bộ trên Task (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`), nên mọi continuation quay về `SynchronizationContext` của WPF và `ReviewCatalog`, `ViewerState`, `CompareViewModel`, `MainViewModel` chỉ bị sửa trên UI thread. Core, Imaging và Platform.Windows thì ngược lại: luôn `ConfigureAwait(false)` và đặt việc CPU/I-O nặng trong `Task.Run` (hoặc I/O async thật) — phần đồng bộ trước `await` đầu tiên của một method mà App gọi phải nhẹ, vì nó chạy trên UI thread. Khi một callee có phần đầu đồng bộ nặng, sửa ở callee (hoặc App bọc lời gọi trong `Task.Run`), không thêm `ConfigureAwait(false)` vào App. Khoá bằng: `AppThreadAffinityTests` (quét source `src/PhotoReview.App`), guard Debug `ReviewCatalog.AssertOwnerThread` (composition root gọi `BindToCurrentThread()`; unit test không bind thì không kiểm tra), và metric `CrossThreadPresentCount` (số lần `WpfPresentationSink` phải `Dispatcher.Invoke` vì nhận cập nhật ngoài UI thread; kỳ vọng 0, hiện trong cửa sổ Diagnostics và `metrics.json` của perf session).

**Analyzer và task không ai await:** `Microsoft.VisualStudio.Threading.Analyzers` đặt `VSTHRD100` (async void) và `VSTHRD110` (gọi async bị bỏ rơi) cùng `CA2012` (ValueTask) ở mức error cho `src/` (`.editorconfig`; các rule VSTHRD khác tắt). Handler `async void` của WPF bọc việc trong `MainWindow.RunGuardedAsync` (log + status "action failed", bỏ qua cancellation) hoặc command của view-model; task cố ý fire-and-forget đi qua `TaskLogging.FireAndLog` để lỗi được log kèm ngữ cảnh thay vì rơi vào `UnobservedTaskException`.

## Luồng decoder và cache

`PreviewImageService` chụp backend hiện hành khi tạo `ImageCacheKey`. Key gồm path chuẩn hóa, length, mtime, Original/target width, backend và orientation. RAM cache, disk cache và in-flight dedup dùng identity này. `ImageDecoderFactory` tạo backend được chọn; WIC Direct và TurboJPEG được bọc bởi `FallbackImageDecoder`, quay về WPF cho nhóm lỗi codec được phép và ghi metrics backend thực tế.

Camera RAW (ADR 0009) đi qua `FormatRoutingDecoder` khi `RawSupportEnabled` (mặc định bật): đọc metadata giới hạn và vùng JPEG preview được chọn; `RawFullDecode=OnZoom` mới gọi LibRaw cho file hiện tại. Adobe RGB preview thiếu ICC dùng profile CC0 tích hợp. `RawPairMode` mặc định `Separate`; người dùng có thể chọn gom cặp trong Settings. Khi RAW tắt, `FormatRoutingDecoder` ném `NotSupportedException` cho đuôi RAW. Dự phòng LibRaw (thumbnail ORF, RAW không có JPEG) và cổng tuần tự một slot: xem ADR 0009. `libraw.dll` chỉ được nạp từ thư mục assembly của app (`LibRawAvailability`, `DllImportSearchPath.AssemblyDirectory`).

**Zoom (option A cho #43):** ngoài Fit, `ViewerState.Zoom` tính theo pixel GỐC — phần tử ảnh có kích thước `OriginalWidth × Zoom / DpiScale` DIP (100 % = 1 pixel nguồn trên 1 pixel thiết bị), không phụ thuộc bitmap đang hiển thị. `ImagePresenter` hiện preview ngay ở đúng kích thước đó, còn `ZoomDetailLoader` gọi `PreviewImageService.DecodeOriginalAsync` (thread riêng, không vào RAM LRU/disk cache) cho ảnh HIỆN TẠI rồi thay `Source` mà không đổi layout/scroll. Chỉ giữ tối đa một original (của ảnh hiện tại); điều hướng sẽ hủy decode chưa chạy, bỏ kết quả trễ (token navigation) và thả original. Về Fit thì dùng lại preview. Quyết định: [ADR 0008](adr/0008-zoom-source-pixel.md).

**Nhập liệu khi zoom:** phím mũi tên trên ảnh đang zoom chỉ pan (`KeyboardPan`, bước = `ArrowPanStepPercent`, mặc định 10 % viewport, 1–100); không bao giờ chuyển ảnh trừ khi bật `ArrowKeyNavigatesAtZoomEdge` (mặc định tắt, Q-R32). Menu chuột phải: Fit, "Zoom to N %" (`ClickZoomPercent`), submenu Zoom levels; cùng nhóm Zoom trong Settings ▸ Mouse & zoom (level luôn sửa được, click-to-zoom, bước pan, phím tắt zoom chỉ đọc). Kéo/pan/glide/wheel/click-zoom nằm ở `PointerInputController`, Fit ở `FitViewController` (AR13).

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
| INV-7 | Bỏ snapshot Explorer trễ khi catalog đã tương tác/đổi; kết quả probe đọc được (AR16) chỉ áp **sau** khi thứ tự Explorer đã chốt, nên snapshot luôn được kiểm tra với toàn bộ danh sách đã liệt kê | `FolderLoadCoordinator`, `ReviewCatalog`, `ExplorerSnapshotValidator` |
| INV-8 | Handle decode dùng share ReadWrite/Delete, SequentialScan và đóng sớm | Các `IImageDecoder`, `ThumbnailCache` |
| INV-9 | Mở file: present ngay, điều hướng/action chờ snapshot (`PendingOrder`) rồi đi theo thứ tự Explorer; mở folder present fallback trước. Probe đọc được bắt đầu sau frame đầu, không giữ `PendingOrder`; gỡ file lỗi giữ ảnh hiện tại theo path (hoặc chuyển tiếp như Delete nếu chính nó lỗi) | `FolderLoadCoordinator`, `MainViewModel` |
| INV-10 | Logging mặc định tắt; tắt thì không tạo log | `FileLog`, `AppLog`, `AppSettings.LoggingEnabled` |
| INV-11 | Config hỏng được backup/reset; config khóa dùng default RAM | `SettingsStore` |
| INV-12 | Backend lỗi fallback WPF; cache không lẫn backend | `ImageDecoderFactory`, `FallbackImageDecoder`, `ImageCacheKey`, `ReviewMetrics` |

## Settings có ảnh hưởng kiến trúc

| Setting | Giá trị | Điểm áp dụng |
|---|---|---|
| `DecoderBackend` | `WicDirect` mặc định (nhanh nhất, ADR 0001); `Wpf`; `TurboJpeg` | `SettingsStore` → `PreviewStateContext` → `PreviewImageService`/`ImageDecoderFactory`. Đổi khi đang xem làm clear cache và present lại ảnh hiện tại. |
| `ScalingQuality` | `HighQuality` mặc định; `Linear` | `MainViewModel` → `ViewerState` → WPF `RenderOptions.BitmapScalingMode`; không đổi decode/cache key. |
| `UseSourceBytesCache` | `false` mặc định | Chụp lúc composition trong `App.xaml.cs`; bật cache byte 16 GiB. Cần khởi động lại để mọi dependency nhận cùng policy. |
| `RawSupportEnabled` / `RawFullDecode` / `RawPairMode` | `true` / `Never` / `Separate` | Mở 8 định dạng RAW; mặc định xem embedded JPEG, chỉ giải mã cảm biến qua LibRaw khi bật `OnZoom`. Gom JPG+RAW là lựa chọn riêng trong Settings và mặc định tắt. ADR 0009. |
| `PreloadForwardCount` / `PreloadBackwardCount` | `32` / `8` mặc định; `1`-`500` / `0`-`500` | `SettingsStore` → `PreloadWindow.FromSettings` → `PreloadScheduler`/`PreloadOrderService.Build`; sàn phần trăm RAM (`RamBudgetPolicy.MinimumCachePercent`) tính theo cửa sổ này. Chụp lúc composition; hiệu lực sau khi khởi động lại (như `PreloadWorkerCount`). |
| `KeyboardZoomAnchor` | `ViewportCentre` mặc định (cũng khi config cũ không có field); `Pointer` (neo tại con trỏ nếu đang trên viewport, giữa khung nhìn nếu không) | `PointerInputController.ResolveKeyboardAnchor` (dùng `PointerGestures.ResolveKeyboardZoomAnchor` + `IImageSurface.PointerPosition`) cho `ZoomInAsync`/`ZoomOutAsync`/`ZoomActualSizeAsync`/`ToggleClickZoomAsync`; con lăn chuột và click-to-zoom luôn neo tại con trỏ. |
| `InitialViewMode` | `Fit` mặc định; còn `Percent100`/`Percent200`/`FitWidth`/`FitHeight`/`ClickZoomLevel`. `Percent400` chỉ còn để đọc config cũ: `SettingsNormalizer` đổi thành `Percent200` | `ViewerState.ApplyInitialViewMode` (zoom thuần) → `PointerInputController.ApplyInitialViewAsync` (thêm bước cuộn cho FitWidth/FitHeight, vì controller giữ `IImageSurface`) → gọi từ `WpfPresentationSink.ApplyInitialViewModeOverride`, `MainWindow` gán sau khi `_pointer` dựng xong (hook mặc định trong `MainViewModelCompositionRoot` chỉ đổi zoom). |
| `FitWidthAnchor` / `FitWidthAnchor2` | `Centre` / `BottomThird` | Neo dọc Fit width: 1 = xem ban đầu + phím `W`; 2 = phím `D4`, menu, chuột giữa. Chi tiết: decisions/FITWIDTH2-MIDDLECLICK.md. |
| `MiddleClickAction` | `ActualSize` | Nhấn chuột giữa trên ảnh; `MiddleClickResolver` -> `ReviewCommand`. |
| `KeepZoomAcrossImages` | `false` mặc định | Khi bật, `ViewerState.ApplyInitialViewMode` không làm gì khi đổi ảnh (trả về `false`): Fit vẫn Fit, mức zoom giữ nguyên, ScrollViewer tự giữ/kẹp offset. Phím `K` (`ShortcutMappings.ToggleKeepZoom`, lệnh `ReviewCommand.ToggleKeepZoom`) bật/tắt và lưu ngay, như `ToggleInfoOverlay`. |
| `ImageTransition` / `ImageTransitionMs` | `None` mặc định; `Fade`; `120` ms mặc định, `40`-`400` | Q-R37: `ImagePresenter.UpdateCurrentImage` → `ImageTransitionDecision.ShouldTransition` (chỉ true khi đổi sang file khác; không bao giờ cho nâng cấp thumbnail→preview→gốc cùng file, ảnh đầu sau khi mở folder, hoặc khi Compare) → `IPresentationSink.SetCurrentImage(image, isFileChange)` → `MainViewModel.ImageChanging` (raise TRƯỚC `PropertyChanged(CurrentImage)`) → `MainWindow` chụp khung hình cũ (`OutgoingImage`) và fade lớp đó (`Opacity` 1→0) trên ảnh mới đã hiển thị đầy. `None` không tạo phần tử/animation nào. |
| `SetZoomAlsoSetsClickLevel` | `true` mặc định | Chọn preset/Custom trong submenu "Zoom" của menu chuột phải: bật thì ghi đè cả `ClickZoomPercent` (`MainWindow.ApplyClickZoomLevelAsync`); tắt thì chỉ zoom một lần qua `PointerInputController.SetClickZoomLevelAsync`. `MainWindow.ApplyZoomMenuSelectionAsync` chọn nhánh. |
| `ShowFolderMenuItems` | `true` mặc định | Ẩn/hiện cụm "Open folder / Next folder / Previous folder" trong menu chuột phải, đọc lại mỗi lần mở (`MainWindow.ImageContextMenu_Opened`); phím tắt `NextFolder`/`PreviousFolder` vẫn hoạt động. |
| `ShowRecycleMenuItem` | `false` mặc định | Ẩn/hiện riêng mục "Đưa vào Thùng rác" trong menu chuột phải, đọc lại mỗi lần mở; phím tắt vẫn hoạt động. Mục hiện kèm phím tắt đang cấu hình (`Shortcuts.SendToRecycleBin`). |

## Kiểm tra cập nhật thủ công (chỉ dùng mạng ở đây)

PhotoReview.Core.Updates (IUpdateChecker, UpdateChecker, AppVersion, UpdateUrlPolicy) là **nơi duy nhất trong ứng dụng chạm tới mạng**, và chỉ chạy khi người dùng bấm **Cài đặt → Chung → Cập nhật → Kiểm tra cập nhật**. Không tự kiểm tra, không tự tải, không tự cài, không ép cập nhật. Một request GET https://api.github.com/repos/ConanKLOP2/PhotoReview/releases/latest (HTTPS, có User-Agent, timeout 10 giây, CancellationToken, async — không chặn UI) chỉ đọc `tag_name`/`html_url`; không gửi gì về ảnh hoặc đường dẫn. Bỏ qua draft/prerelease; so sánh số major.minor.patch với phiên bản đang chạy (BuildInfo.GetVersion, chấp nhận tiền tố `v` và +metadata). Kết quả: UpToDate / UpdateAvailable(version, url) / Failed(Offline, Timeout, RateLimited, BadResponse, InvalidVersion). Tầng HTTP tiêm được qua HttpMessageHandler nên test không dùng mạng thật. Nút **Mở trang tải về** chỉ hiện khi có bản mới và chỉ mở URL https://github.com/ConanKLOP2/PhotoReview/... (UpdateUrlPolicy) bằng trình duyệt mặc định.

## Runtime và GC (AR12c)

Workstation concurrent GC (mặc định .NET) là lựa chọn có chủ đích, không phải bỏ sót: (i) app một cửa sổ, độ trễ UI quan trọng hơn throughput; (ii) buffer pixel của `BitmapSource` nằm ở native heap (MIL), heap managed nhỏ nên Server GC không giúp. `InvariantGlobalization` **không** bật vì `vi.json` và sắp xếp `CurrentCulture` cần ICU. `TieredPGO`/`TieredCompilation` dùng mặc định .NET 10 (đã bật). Đo lại chỉ khi `%` thời gian GC pause trong perf session > 1 %.

## Build, test, benchmark và publish

Lệnh build/test/publish/benchmark: xem [README](../README.md); quy tắc test: [TESTING.md](TESTING.md). `tools/verify-all.ps1` build solution, chạy năm project xUnit (loại `Category=Manual`; mặc định cũng loại `Native`/`Slow`, riêng `Integration`+`Slow` vẫn chạy ở lượt thêm), kiểm tra docs/OPEN-DECISIONS/i18n, publish framework-dependent và verify artifact. Benchmark và T73 GUI acceptance vẫn là gate riêng vì test tự động không chứng minh hình ảnh đã render đúng trên desktop.

## Chính sách ghi session và file không đọc được (ADR 0007)

- **Session** (`SessionStore.Save`) ghi nguyên tử (file tạm + rename) nhưng **không fsync** (`WriteAllTextAtomic(..., durable: false)`); Settings giữ `durable: true`. `SessionStore.Load` coi file session rỗng/hỏng là "không có session" (chỉ ghi log).
- **Quét folder** (AR16, Q-AR7 c): `IFileSystem.EnumerateFilesWithStat(dir, include, onSkipped)` chỉ liệt kê (không mở từng file) để trình diễn ảnh đầu ngay; sau frame đầu `FolderLoadCoordinator` chạy `TryProbeReadable` nền (≤ 8 song song, cùng generation). Khi thứ tự Explorer đã chốt, kết quả về UI thread: load cũ → bỏ; còn hiện hành → `ReviewCatalog.RemovePaths` (giữ ảnh hiện tại, hoặc chuyển tiếp như Delete), sink bỏ preload/cache của file đó, và file bị **bỏ qua có báo cáo** qua `IFolderLoadSink.OnFilesSkipped` (log + cảnh báo "Bỏ qua N file không đọc được" và cửa sổ danh sách), không dùng `IgnoreInaccessible` im lặng. Liệt kê bị ngắt giữa chừng vẫn báo ngay khi catalog sẵn sàng; chính folder không liệt kê được vẫn là lỗi (`OnFailed`).
