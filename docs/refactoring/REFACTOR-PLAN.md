# PhotoReview — Plan tái cấu trúc (B + C3)

- **Ngày lập:** 2026-09-16 · **Baseline:** `master` @ `86282cd`
- **Quyết định hướng:** **B + C3** (người dùng chốt 2026-09-16).
  - **B:** giữ WPF, tách thành nhiều project theo lớp và chuyển sang MVVM.
  - **C3:** giữ WPF, thay bộ giải mã ảnh (decoder) sau interface `IImageDecoder`.
- **Trạng thái:** đã chốt hướng. Task nào gắn **⛔Qn** thì phải chờ người dùng trả lời câu hỏi Qn (mục 11) mới được làm.
- **Danh sách task:** [`REFACTOR-TASKS.md`](REFACTOR-TASKS.md). Mỗi task đủ nhỏ để một agent hoặc một model làm trong một phiên.
- **Chẩn đoán hiệu năng (chạy trước W1):** [`PERF-DIAGNOSIS-PLAN.md`](PERF-DIAGNOSIS-PLAN.md) · [`PERF-DIAGNOSIS-TASKS.md`](PERF-DIAGNOSIS-TASKS.md) (D00–D13). Kết quả D12 quyết định thứ tự ưu tiên của WC3 và W6.
- **Các hướng không chọn:** A (chỉ chia thư mục), C1 (WinUI 3), C2 (Avalonia), C4 (viết lại native). Lý do ở mục 3. C1 có thể mở lại qua ADR (T71).

---

## 1. Mục tiêu

1. Tách `MainWindow.xaml.cs` (1086 dòng) thành ViewModel và coordinator test được. Sau khi xong, file này ≤ 250 dòng.
2. Chia project theo lớp: Core / Imaging / Platform.Windows / App / Benchmarking. Dependency chỉ đi một chiều và có test kiến trúc kiểm tra.
3. Thay 69 test source-presence bằng test hành vi. Gộp hai bộ test thành một bộ xUnit.
4. **(C3)** Đưa decoder ra sau `IImageDecoder` và thêm backend nhanh hơn: WIC gọi trực tiếp có decode thu nhỏ, và/hoặc libjpeg-turbo. Backend mới phải có cổng chất lượng: màu/ICC, EXIF orientation, so pixel. Backend cũ (WPF `BitmapImage`) luôn giữ làm fallback.
5. Cải thiện chất lượng hiển thị: xử lý EXIF orientation, đặt `BitmapScalingMode` chất lượng cao cho ảnh chính.
6. Tối ưu theo 4 nguyên tắc của `AGENTS.md`: đọc đĩa ít nhất, dùng nhiều RAM, review nhanh, chất lượng ảnh tốt nhất.
7. Giữ nguyên các bất biến an toàn (mục 4.6). Có CI.

**Không phải mục tiêu:** tính năng người dùng mới, đổi UX (ngoài orientation và chất lượng scale), đa nền tảng, đổi UI framework.

---

## 2. Hiện trạng

### 2.1 Số liệu

| Hạng mục | Giá trị |
|---|---|
| Code và tài liệu có track | ~8.900 dòng / 93 file. Riêng `PhotoReview.App` ~4.850 dòng |
| Project | `PhotoReview.App` (WinExe, WPF và WinForms), `PhotoReview.Tests` (console, 147 `Check(...)`), `PhotoReview.Tests.Unit` (xUnit, 188 test) |
| File lớn nhất | `MainWindow.xaml.cs` 1086, `PhotoReview.Tests/Program.cs` 627, `ServiceBehaviorTests.cs` 419, `SourcePresenceTests.cs` 404 |
| Namespace | Toàn bộ nằm trong `PhotoReview.App`, không có thư mục con |
| Build | Chỉ có `Directory.Build.props`. Chưa có CI, `global.json`, `.editorconfig`, CPM, analyzer |
| Decode | `BitmapImage` + `DecodePixelWidth` (WIC qua WPF), `BitmapCacheOption.OnLoad`, `Freeze()`. Chưa xử lý EXIF orientation. Chưa đặt `BitmapScalingMode` (WPF mặc định dùng scale tuyến tính) |

### 2.2 Bản đồ thành phần

```text
App.xaml.cs ──► MainWindow (UI + điều phối + nghiệp vụ + I/O)
                 ├─ LoadFolderAsync        : scan, sort, Explorer snapshot, generation guard
                 ├─ ShowImageAsync         : cache key, thumbnail, decode, compare, hash, status, session save
                 ├─ Window_KeyDown         : ánh xạ phím → lệnh (chuỗi if)
                 ├─ ClassifyCurrentAsync   : Recycle (+ nhánh Move "Folder2" không còn được gọi)
                 ├─ ExecuteActionAsync     : Move/Copy + journal + undo
                 ├─ RemoveDuplicatesAsync  : hash batch + Recycle
                 ├─ Undo*                  : move stack + Recycle Bin restore
                 └─ zoom/fit/fullscreen, sibling folder, compare selection
Services (cùng namespace): PreviewImageService, PreloadScheduler, ThumbnailCache,
  DiskCacheStore(static), BoundedLruCache, ImageCacheKey, FileHashService,
  ExplorerOrderService(+COM), OperationJournal, RecoveryRetryService,
  RecycleBinRestoreService, SessionStore, AppSettings, AppLog(static), ReviewMetrics,
  PhysicalMemory, ImageSortService(P/Invoke StrCmpLogicalW), SiblingFolderService,
  ComparePairService, DragDropInputService, InstanceLock, WindowPlacementService,
  AdaptivePreviewPolicy, PreloadOrderService
Benchmark* (Engine, Executor, Models, Profiles, WorkloadRunner, Window) nằm trong app production.
```

### 2.3 Vấn đề (P1–P14)

| # | Vấn đề | Bằng chứng | Hệ quả |
|---|---|---|---|
| P1 | God class `MainWindow` | 20+ field trạng thái, 3 bộ đếm generation, UI + nghiệp vụ + I/O trộn lẫn | Không test được hành vi. Race chỉ được bảo vệ bằng comment |
| P2 | Test khóa vào chuỗi source | 69 test trong `SourcePresenceTests` và nhiều `mainWindow.Contains(...)` trong `Program.cs` | Refactor là test đỏ dù hành vi không đổi |
| P3 | Hai bộ test trùng nhau | 5 file giống nhau giữa `PhotoReview.Tests` và `Tests.Unit`. CLI vừa là test vừa là benchmark vừa là probe | Duy trì hai lần |
| P4 | Stringly-typed | `LoadingMode`, `ImageSortMode`, `InitialViewMode`, `ReviewAction.Operation`, `JournalEntry.Type/State` | Dễ gõ sai. Logic normalize rải rác |
| P5 | Đường dẫn dữ liệu không nhất quán | `PHOTOREVIEW_DATA_ROOT` được tự tính ở 3 nơi với root khác nhau. Config và cache bỏ qua biến này | Test có thể đụng dữ liệu thật |
| P6 | Static và global state | `AppLog`, `DiskCacheStore`, `AppSettings.Load()`. `PreloadScheduler` gọi `Dispatcher.Yield` | Test phải chạy tuần tự. Không inject được |
| P7 | I/O đồng bộ trên UI thread | Session được ghi sau mỗi lần present. `SetZoom` gọi `FileInfo`. Journal được đọc toàn bộ khi khởi động và không bao giờ compaction | Trái nguyên tắc 1 và 3 |
| P8 | Đọc nguồn nhiều lần | Preview mode: thumbnail, adaptive, header, hash. Mỗi bước mở file riêng | Trái nguyên tắc 1 |
| P9 | Chính sách RAM lệch đơn vị | Ngưỡng 16 GB (tính theo ảnh **đã decode**) được so với tổng byte **nén** trên đĩa | Quyết định full-folder preload có thể sai |
| P10 | Benchmark nằm trong binary production | 6 file `Benchmark*` | Binary lớn hơn, code lẫn lộn |
| P11 | WinForms chỉ dùng cho `FolderBrowserDialog` | `UseWindowsForms=true` | Namespace nhập nhằng (phải viết `System.Windows.MessageBox`…) |
| P12 | Code chết và bug nhỏ | Nhánh Folder2 không còn đường gọi. Undo so `Destination` phân biệt hoa thường. Có thể `Insert(-1)` trong `ExecuteActionAsync` | Rủi ro tiềm ẩn |
| P13 | AGENTS mâu thuẫn với thực tế | Yêu cầu "push origin master", nhưng lịch sử dùng PR | Agent có thể push thẳng master |
| P14 | **Chất lượng hiển thị** | Không áp EXIF orientation: ảnh chụp dọc có thể hiển thị nằm ngang. Chưa đặt `BitmapScalingMode` | Trái nguyên tắc 4 |

### 2.4 Điểm mạnh cần giữ

Epoch và in-flight dedup (`Lazy` + `GetOrAdd`), kiểm tra fingerprint sau decode, persist queue có giới hạn, prune được gộp theo thư mục. Journal Prepared/Committed/Failed và reconcile khi khởi động. Recycle Bin thay cho xóa thật. Explorer COM chạy trên STA pump có timeout và fallback. Nhiều service thuần đã được tách sẵn.

---

## 3. So sánh hướng và lý do chọn B + C3

### 3.1 Bảng so sánh

| Tiêu chí | B (phân lớp + MVVM) | C1 WinUI 3 + Win2D | C2 Avalonia + Skia | **C3 thay decoder** | C4 native C++ |
|---|---|---|---|---|---|
| Tái sử dụng code | ~90% | Core/Platform giữ được, Views + Imaging viết lại | Như C1 | ~95% | ~0% |
| Công sức (agent-day) | 8–12 | 15–25 | 15–25 | 5–9 | 40+ |
| Rủi ro hồi quy | Trung bình (có test giữ hành vi) | Cao | Cao | Thấp–TB (màu, EXIF, định dạng) | Rất cao |
| Tốc độ decode | Như cũ | Vẫn là WIC, không tự nhanh hơn | libjpeg-turbo | **Cải thiện trực tiếp** (decode thu nhỏ theo hệ số DCT, bỏ lớp `BitmapImage`) | Tối đa |
| Tốc độ render | Như cũ | Tiềm năng tốt hơn (D3D11) | Tốt | Như cũ | Tối đa |
| Chất lượng | Chỉnh được `BitmapScalingMode` | HighQualityCubic | Tốt | Kiểm soát được ICC và orientation | Toàn quyền |
| Triển khai | Như cũ | Cần Windows App Runtime hoặc self-contained, bản phát hành lớn hơn | Ổn | Thêm DLL native (nếu chọn turbojpeg) | Nhỏ |
| Đa nền tảng | Không cần | Không | Có, nhưng vô nghĩa (Explorer, Recycle Bin chỉ có trên Windows) | Không cần | Không |

### 3.2 Lý do

- Nút thắt có khả năng lớn nhất của review là **đọc đĩa và decode**. C1 vẫn decode bằng WIC nên không giải quyết nút này. C3 và R-4 giải quyết trực tiếp.
- B là nền bắt buộc để C3 (và C1 nếu có sau này) thay thế được mà không đụng UI.
- Chỉ mở lại C1 khi benchmark (T66) cho thấy `UiAssign`/render chiếm phần lớn thời gian key→present. Việc này được ghi trong ADR T71.
- Nhận định "nút thắt lớn nhất là I/O và decode" ở trên **chưa được đo**. Đợt chẩn đoán D00–D12 kiểm chứng nó theo quy tắc R-IO / R-DEC / R-UI / R-PRE (xem `PERF-DIAGNOSIS-PLAN.md` mục 8). Nếu kết quả khác, D13 sắp xếp lại thứ tự WC3/W6. Hướng B vẫn giữ nguyên.

### 3.3 Ràng buộc thiết kế để B không chặn các hướng sau (K-1…K-3)

| ID | Ràng buộc | Kiểm tra bằng |
|---|---|---|
| K-1 | Tầng Imaging (cache, preload, decoder) làm việc với `DecodedImage` (buffer BGRA32 + kích thước + metadata) hoặc một handle trừu tượng. Kiểu WPF `BitmapSource` chỉ xuất hiện trong **adapter** `WpfImageAdapter` ở biên | Test kiến trúc: namespace `PhotoReview.Imaging.Core` không tham chiếu `System.Windows.Media` |
| K-2 | ViewModel không tham chiếu `System.Windows.*`. Dùng `IUiScheduler`, `IDialogService` và ánh xạ phím ở biên | Test kiến trúc trên namespace `PhotoReview.App.ViewModels` |
| K-3 | Decoder được chọn qua `IImageDecoderFactory` theo setting `DecoderBackend`, có **fallback chain** (backend mới lỗi → backend WPF) | Unit test factory + test lỗi giả lập |
| K-4 | Event `PhotoReview-Perf` (D03/D04) và biến môi trường chẩn đoán (D05/D10) **được giữ** khi code bị di chuyển hoặc tách. Mỗi giai đoạn đo vẫn phát event tương ứng | Test `PerfTraceTests` + `--perf-session`/`--perf-analyze` chạy được ở cuối mỗi wave |

> **Lưu ý về K-1:** chuyển toàn bộ cache sang buffer thô làm mỗi ảnh phải copy thêm một lần sang `BitmapSource`. Phương án thực tế: cache lưu `IDecodedImage` có property `PlatformImage` (WPF `BitmapSource` đã `Freeze`) được tạo **một lần** khi decode. Ước lượng kích thước vẫn là w×h×4. Test kiến trúc chỉ cấm type WPF xuất hiện trong **chữ ký public** của cache và preload.

---

## 4. Kiến trúc đích

### 4.1 Layout

```text
src/
  PhotoReview.Core               net10.0
  PhotoReview.Imaging            net10.0-windows   (UseWPF, không Window)
  PhotoReview.Imaging.TurboJpeg  net10.0-windows   (tùy chọn C3, P/Invoke turbojpeg.dll)
  PhotoReview.Platform.Windows   net10.0-windows
  PhotoReview.Benchmarking       net10.0-windows
  PhotoReview.App                net10.0-windows   (WinExe)
tools/
  PhotoReview.Benchmark.Cli      net10.0-windows   (Exe)
  *.ps1
tests/
  PhotoReview.Core.Tests         net10.0
  PhotoReview.Imaging.Tests      net10.0-windows   (gồm cổng chất lượng decoder)
  PhotoReview.App.Tests          net10.0-windows   (ViewModel, coordinator)
  PhotoReview.Integration.Tests  net10.0-windows   (Trait Integration/Manual)
  PhotoReview.Architecture.Tests net10.0-windows
  Fixtures/                      (ảnh tổng hợp nhỏ + generator; ảnh thật lấy qua biến PHOTOREVIEW_FIXTURE_DIR)
```

```text
App ──► Imaging ──► Core
 │  ├─► Imaging.TurboJpeg ──► Imaging
 │  ├─► Platform.Windows ──► Core
 │  └─► Benchmarking ──► Imaging, Core
Benchmark.Cli ──► Benchmarking (+ Imaging.TurboJpeg)
```

### 4.2 PhotoReview.Core

```text
Abstractions/  IFileSystem, IClock, IAppPaths, IMemoryProbe, IRecycleBin, IExplorerOrderProvider,
               ILog, IUiScheduler, IDialogService, IKeyNameValidator, INaturalComparer
Model/         LoadingMode, ImageSortMode, InitialViewMode, FileOperationType, JournalState,
               DecoderBackend, LenientEnumConverter<T>
Settings/      AppSettings (POCO), ShortcutMappings, ReviewAction, SettingsValidator, SettingsStore,
               PerformanceOptions
Catalog/       ReviewCatalog, CatalogEntry, GenerationClock, ImageSortService (managed + INaturalComparer),
               SiblingFolderService, ComparePairService, DragDropInputService, ImageFileTypes,
               ExplorerSnapshotValidator
FileActions/   FileActionService, UndoService, DuplicateFinder, OperationJournal, RecoveryRetryService
Session/       SessionStore, SessionWriter (debounce)
Diagnostics/   ReviewMetrics, FileLog (ILog), BenchmarkStatistics
Caching/       BoundedLruCache
AppPaths.cs
```

### 4.3 PhotoReview.Imaging (B + C3)

```text
Decoding/
  IImageDecoder            Decode(DecodeRequest) → IDecodedImage ; ReadInfo(source) → ImageInfo
  DecodeRequest            Source (path hoặc ReadOnlyMemory<byte>), TargetWidth (0 = gốc), ApplyOrientation=true
  IDecodedImage            PixelWidth, PixelHeight, Downscaled, Orientation, EstimatedBytes, PlatformImage (object)
  ImageInfo                Width, Height (sau khi áp orientation), Orientation, HasIccProfile, Format
  IImageDecoderFactory     Create(DecoderBackend) + fallback chain
  WpfBitmapImageDecoder    (backend hiện tại, chuyển nguyên từ PreviewImageService.DecodeSource)
  WicDirectDecoder         (C3-a: COM IWICImagingFactory, IWICBitmapSourceTransform để decode thu nhỏ,
                            IWICColorTransform để chuyển ICC → sRGB, đọc orientation từ metadata)
  ExifOrientation          đọc tag 274 + áp rotate/flip (dùng chung cho mọi backend)
  WpfImageAdapter          buffer → BitmapSource.Create(...).Freeze()
Caching/    PreviewImageService, ThumbnailCache, DiskCacheStore (instance), SourceBytesCache (R-4)
Preload/    PreloadScheduler, PreloadOptions, RamBudgetPolicy
Hashing/    FileHashService
ImageCacheKey, AdaptivePreviewPolicy, PreloadOrderService
```

`PhotoReview.Imaging.TurboJpeg` (C3-b): `TurboJpegDecoder : IImageDecoder`, chỉ xử lý JPEG, các định dạng khác chuyển cho backend kế tiếp. Dùng `tj3Init`, `tj3DecompressHeader`, `tj3SetScalingFactor` (1/8…1), `tj3Decompress8` ra BGRA. ICC thì đọc qua WIC hoặc chuyển bằng `IWICColorTransform`. Nếu ảnh có ICC mà không chuyển được, **chuyển ảnh đó cho WIC**. DLL `turbojpeg.dll` (x64) được đóng gói kèm app. Giấy phép: IJG / BSD-3-Clause / zlib (ghi vào `THIRD-PARTY-NOTICES.md`).

### 4.4 C3: backend, chọn lựa và cổng chất lượng

| Backend | Phụ thuộc | Định dạng | Decode thu nhỏ | ICC | Ghi chú |
|---|---|---|---|---|---|
| `Wpf` (mặc định hiện tại) | Không | Mọi codec WIC | `DecodePixelWidth` | WPF tự xử lý khi không bật `IgnoreColorProfile` | Tham chiếu chất lượng |
| `WicDirect` (C3-a) | Không (COM có sẵn trong Windows) | Mọi codec WIC | `IWICBitmapSourceTransform` (JPEG hỗ trợ scale theo DCT) rồi đến `IWICBitmapScaler` (Fant/HighQualityCubic) | `IWICColorContext` + `IWICColorTransform` | **Ưu tiên thử trước** vì không thêm dependency |
| `TurboJpeg` (C3-b) | `turbojpeg.dll` | JPEG | `tj3SetScalingFactor` + scale bổ sung | Qua WIC transform hoặc fallback | Thường nhanh nhất cho JPEG. Cần đo |
| (Tham khảo) SkiaSharp / NetVips | Native lớn | Nhiều | Có | Có | **Không** triển khai trừ khi T86 cho thấy hai backend trên không đạt |

> Các nhận định về khả năng scale và ICC của từng backend phải được **xác minh trong T82/T85**. Nếu thực tế khác, cập nhật bảng này.

**Cổng chất lượng** (T80/T86). Backend mới chỉ được bật mặc định khi đạt **tất cả** điều kiện sau trên fixture:

1. **Kích thước:** đúng kích thước mục tiêu (±1 px), đúng hướng sau orientation.
2. **Sai khác pixel so với `Wpf`** (cùng target width, cùng orientation đã áp):
   - Full-res: PSNR ≥ 45 dB.
   - Downscale: PSNR ≥ 35 dB, ΔE trung bình ≤ 2 trên sRGB.
   - Ngưỡng này là tạm thời. T86 hiệu chỉnh và ghi lý do.
3. **ICC:** ảnh Adobe RGB / Display P3 cho kết quả tương đương `Wpf` (theo điều kiện 2). Nếu backend không hỗ trợ ICC thì phải fallback, không hiển thị sai màu.
4. **EXIF orientation 1–8:** cả 8 giá trị cho kết quả đúng.
5. **Ảnh lỗi hoặc cắt cụt:** ném exception thuộc nhóm đã biết (`IOException`, `NotSupportedException`, `FileFormatException`, `InvalidDataException`) để fallback hoạt động. Không crash native.
6. **An toàn file:** giữ `FileShare.ReadWrite | FileShare.Delete`, `SequentialScan`, và đóng handle ngay sau khi đọc (INV-8).
7. **Hiệu năng:** decode P50 và P95 nhanh hơn `Wpf` ít nhất 20% trên fixture ảnh thật (JPEG 12–50 MP) ở target width điển hình (viewport × DPI × 1.15). RAM peak không tăng quá 10%.

**Thay đổi cache key:** thêm `DecoderBackend` và `OrientationApplied` vào `ImageCacheKey` và vào tên file disk cache. Không được để pixel của backend này lẫn sang backend khác (INV-1).

### 4.5 PhotoReview.Platform.Windows và PhotoReview.App

- **Platform:** `ExplorerOrderService` + COM interop, `WindowsRecycleBin : IRecycleBin`, `PhysicalMemory : IMemoryProbe`, `InstanceLock`, `WindowsNaturalComparer` (StrCmpLogicalW).
- **App:** composition root dùng `Microsoft.Extensions.DependencyInjection`. MVVM dùng `CommunityToolkit.Mvvm`. Cấu trúc thư mục:
  - `Views/`
  - `ViewModels/`: `MainViewModel`, `ViewerState`, `CompareViewModel`, `StatusFormatter`
  - `Coordinators/`: `FolderLoadCoordinator`, `ImagePresenter`
  - `Input/ShortcutRouter`
  - `Services/`: `DialogService`, `FolderPicker` (`OpenFolderDialog`), `DispatcherUiScheduler`, `WpfKeyNameValidator`, `WindowPlacementService`
- `MainWindow.xaml`: đặt `RenderOptions.BitmapScalingMode="HighQuality"` cho ảnh chính và ảnh compare (T88). Có thể đổi trong settings nếu zoom bị chậm.

### 4.6 Bất biến bắt buộc giữ

| ID | Bất biến | Test |
|---|---|---|
| INV-1 | Cache key = path chuẩn hóa + length + mtime + mode + width (**+ backend + orientation** sau C3). Không present pixel cũ khi source đổi | `ImageCacheKeyTests`, `PreviewImageServiceTests` |
| INV-2 | Clear/evict tăng epoch. In-flight hoặc persist cũ không làm entry sống lại | `PreviewImageServiceTests`, `DiskCacheStoreTests` |
| INV-3 | Advance đúng một lần, trước thao tác filesystem. Không `ShowImage` sau khi action xong | T14 → T46 |
| INV-4 | Chỉ một file action chạy tại một thời điểm | T14 → T42 |
| INV-5 | Action hoàn tất sau khi đã đổi folder: không đăng ký undo, không sửa catalog | T14 → T46 |
| INV-6 | Delete đi vào Recycle Bin. Journal ghi Prepared → Committed/Failed. Reconcile khi khởi động | `OperationJournalTests`, smoke |
| INV-7 | Bỏ qua Explorer snapshot nếu catalog đã có tương tác hoặc tập file đã đổi | T14 → T44 |
| INV-8 | Handle đọc dùng `ReadWrite \| Delete` + `SequentialScan`, đóng sau khi đọc (mọi backend) | `FileActionConcurrencyTests`, T80 |
| INV-9 | Mở file thì chờ snapshot trước frame đầu. Mở folder thì present fallback trước | T14 → T44 |
| INV-10 | Logging mặc định tắt. Khi tắt không tạo file | `AppLogTests` |
| INV-11 | Config hỏng thì backup rồi reset. Config bị khóa thì dùng default trong RAM | `AppSettingsTests` |
| INV-12 | **(C3)** Backend lỗi thì fallback sang `Wpf`, người dùng vẫn xem được ảnh. Pixel của backend khác không lẫn vào cache | T84, T87 |

---

## 5. Lộ trình

| Wave | Nội dung | Task |
|---|---|---|
| W0 | Baseline, CI, công cụ build, quy trình git | T00, T02–T05 (T01 được thay bằng D07 + D12) |
| **WD** | **Chẩn đoán hiệu năng trên code hiện tại, trước W1** | D00–D13 |
| W1 | Gộp test, khóa hành vi INV trên code hiện tại | T10–T14 |
| W2 | Tách Core | T20–T26 |
| W3 | Tách Imaging và Platform, `IImageDecoder`, bỏ static | T30–T35 |
| **WC3** | **Nhánh C3, chạy song song với W4 sau khi xong T31c** | T80–T88 |
| W4 | Chia nhỏ `MainWindow` (MVVM) | T40–T47 |
| W5 | Tách Benchmark, bỏ WinForms, dọn code | T50–T53 |
| W6 | Hiệu năng R-1…R-4, tích hợp decoder đã chọn | T60–T66 |
| W7 | ADR, tài liệu, GUI acceptance, release | T71–T74 |

```text
W0 → WD (D00…D13) → W1 → W2 → W3 ─┬─► W4 → W5 → W6 → W7
                   └─► WC3 (T80…T86) ──► T87 (cần T40) ──► W6 (T66 đo cùng)
```

---

## 6. Đề xuất hiệu năng

| ID | Đề xuất | Nguyên tắc AGENTS | Đo bằng |
|---|---|---|---|
| R-1 | Lưu session có debounce, ghi trên worker | 1, 3 | Số lần ghi / 100 lần Next |
| R-2 | Journal: đọc ngược từ cuối khi khởi động, hoặc compaction | 1, 3 | Thời gian khởi động với journal 100k dòng |
| R-3 | Bỏ `FileInfo` trên UI thread, dùng metadata trong catalog | 3 | `StatCount` mỗi lần Next |
| R-4 | `SourceBytesCache` (byte nén, đọc một lần, dùng chung cho thumbnail/decode/dimension/hash) + `RamBudgetPolicy` theo w×h×4. Nếu tổng nguồn < 16 GB thì nạp toàn folder | 1, 2 | `SourceOpenCount` ≤ 1 mỗi ảnh, RAM peak |
| R-5 | Tách benchmark khỏi đường khởi động của app | 3 | Kích thước publish, thời gian khởi động |
| R-6 | `CatalogEntry` có sẵn length/mtime/dimension | 1, 3 | Số lần stat |
| R-7 | **C3**: decoder `WicDirect` / `TurboJpeg` | 3, 4 | Cổng mục 4.4 |

R-4 và C3 bổ trợ nhau: `DecodeRequest` nhận `ReadOnlyMemory<byte>` từ `SourceBytesCache`, nên decoder không phải mở file. Mọi kết luận "nhanh hơn" phải dựa trên benchmark so với baseline T01.

---

## 7. Multi-agent và bàn giao cho model khác

### 7.1 Vai trò

| Vai trò | Việc |
|---|---|
| Coordinator | Giữ `REFACTOR-TASKS.md` và `task_on_progress.md`, giao task, kiểm tra phụ thuộc và cổng ⛔ |
| Worker | Làm **một** task trong worktree riêng, branch `refactor/<ID>-<slug>`, chỉ sửa file có trong danh sách của task |
| Reviewer | Kiểm diff theo INV/K và tiêu chí hoàn thành, không để task lan phạm vi |
| Integrator | Merge vào `refactor/integration` theo thứ tự ID, chạy VERIFY, mở PR vào `master` cuối mỗi wave |

### 7.2 Quy tắc cho model thực thi

1. Mỗi phiên làm **một task**. Đọc hết mục task trước khi sửa.
2. Không sửa file ngoài danh sách **Files**. Cần sửa thêm thì đặt `BLOCKED` và ghi lý do.
3. Không đổi hành vi ngoài phần mô tả. Không "tiện tay" refactor chỗ khác.
4. Không xóa comment giải thích race, contract, fixture hay giới hạn WPF/Windows. Khi di chuyển hoặc tách code có `PhotoReviewPerf.Log.*` hoặc `DiagOptions`, phải giữ chúng lại (K-4).
5. Chuỗi tiếng Việt hiển thị cho người dùng phải giữ nguyên văn.
6. Hoàn thành = build, test và VERIFY đạt + nhật ký theo mẫu + trạng thái `DONE` + commit trên branch task.
7. Không push `master`. Không `--force`. Không tắt test để cho đạt.

### 7.3 Commit

`refactor(<layer>): <mô tả> (T21a)` + dòng attribution theo quy định của repo.

---

## 8. Kiểm thử

| Cấp | Lệnh (sau T24 và T53) |
|---|---|
| Unit | `dotnet test PhotoReview.slnx -c Release --filter "Category!=Integration&Category!=Manual&Category!=Quality"` |
| Integration | `dotnet test PhotoReview.slnx -c Release --filter Category=Integration` |
| Chất lượng decoder | `dotnet test tests/PhotoReview.Imaging.Tests -c Release --filter Category=Quality` (fixture tổng hợp chạy trong CI; ảnh thật qua `PHOTOREVIEW_FIXTURE_DIR`, không chạy trong CI) |
| Script | `tools/verify-all.ps1` |
| Benchmark | `dotnet run --project tools/PhotoReview.Benchmark.Cli -c Release -- --benchmark-all <folder> <out>` và `--decoder-bench <folder> <out>` (T81) |
| GUI | Checklist T73 |

**Parity gate:** trước T53, mọi `Check(...)` phải được ánh xạ sang xUnit trong `docs/refactoring/test-parity.md`.

---

## 9. Rủi ro và rollback

| Rủi ro | Mức | Giảm thiểu | Rollback |
|---|---|---|---|
| Hồi quy race khi tách `MainWindow` | Cao | Test INV viết trước (W1) | Revert merge của wave. Tag `pre-refactor-baseline` |
| Đổi layout làm hỏng script/publish | TB | T24 gói trong 1 commit, grep toàn repo | Revert T24 |
| Enum làm hỏng config/journal cũ | TB | `LenientEnumConverter` + fixture legacy | Converter fallback về default |
| **Decoder mới sai màu hoặc sai hướng** | TB | Cổng chất lượng 4.4, fallback chain, mặc định vẫn là `Wpf` cho tới khi T86 đạt | Setting `DecoderBackend=Wpf` |
| **turbojpeg native crash hoặc lỗi ABI** | TB | Dùng bản chính thức pin version, kiểm magic JPEG trước khi gọi, test ảnh hỏng | Không đóng gói DLL, dùng `WicDirect`/`Wpf` |
| **Rò rỉ COM (WicDirect)** | TB | Release trong `finally`, test lặp 10k lần theo dõi handle và bộ nhớ | Setting quay về `Wpf` |
| Cache key đổi làm vô hiệu disk cache cũ | Thấp | Chấp nhận. Prune quota sẽ dọn file cũ | — |
| `HighQuality` scaling làm zoom chậm | Thấp | Setting `ScalingQuality` | Đổi về `Linear` |
| R-4 gây áp lực RAM | TB | Kiểm headroom, flag | Tắt flag |
| Worker song song conflict | TB | Quy định sở hữu file | Rebase |

---

## 10. Tiêu chí hoàn thành toàn đợt

- [ ] `MainWindow.xaml.cs` ≤ 250 dòng. Test INV-3/4/5/7/9 chạy trên ViewModel/coordinator.
- [ ] Còn ≤ 5 test wiring XAML (parse XML). Chỉ còn một hệ xUnit. `test-parity.md` hoàn chỉnh.
- [ ] Test kiến trúc (dependency, K-1, K-2) đạt. Core build trên `net10.0`.
- [ ] Không còn `UseWindowsForms`. `PHOTOREVIEW_DATA_ROOT` chỉ được đọc trong `AppPaths`.
- [ ] `IImageDecoder` với `Wpf` + ít nhất một backend C3 đạt cổng 4.4. Fallback đã được test. ADR `0001` có số liệu.
- [ ] EXIF orientation đúng cho cả 8 giá trị. `BitmapScalingMode` chất lượng cao đã được bật.
- [ ] CI xanh. Benchmark P95 không tệ hơn baseline quá 5% ở profile nào. Decode nhanh hơn ít nhất 20% nếu backend C3 được bật mặc định.
- [ ] Tài liệu đã cập nhật. PR được merge. Publish thành công.

---

## 11. Câu hỏi còn mở và mặc định đề xuất

Task gắn ⛔Qn phải chờ người dùng trả lời Qn. Nếu người dùng chấp nhận **mặc định đề xuất**, Coordinator ghi lại xác nhận vào nhật ký T00.

| # | Câu hỏi | Mặc định đề xuất | Task bị chặn |
|---|---|---|---|
| Q2 | Giữ `BenchmarkWindow` trong app? | Giữ, tham chiếu `PhotoReview.Benchmarking` | T50b |
| Q3 | Chuyển project vào `src/`? Đường dẫn publish mới là `src/PhotoReview.App/bin/Release/net10.0-windows/publish` | Chuyển | T24 |
| Q4 | Đổi AGENTS sang quy trình PR? | Đổi | T05 |
| Q5 | Bật R-4 mặc định? | Bật sau khi T66 đạt, trước đó để flag tắt | T65 |
| Q6 | Thêm `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `NetArchTest.Rules` (chỉ trong test)? | Đồng ý | T35, T40 |
| Q7 | **(C3)** Cho phép đóng gói `turbojpeg.dll` (libjpeg-turbo, BSD/IJG/zlib)? | Đồng ý nếu T86 cho thấy nhanh hơn `WicDirect` ít nhất 15% | T85, T87 |
| Q8 | **(C3)** Bật backend mới làm mặc định khi đạt cổng? | Có, giữ setting để quay về `Wpf` | T87 |
