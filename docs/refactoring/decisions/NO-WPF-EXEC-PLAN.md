---
id: NO-WPF-EXEC-PLAN
order: 201
summary: |-
  Kế hoạch thực thi bỏ WPF cho cửa sổ xem ảnh bằng nhiều agent song song (2026-10-10, chưa đổi mã): bản Win32 + Direct2D là project mới chạy song song với bản WPF tới khi tương đương hành vi, dùng chung Core/Imaging/ViewModels và dữ liệu người dùng; 18 hợp đồng C# đóng băng trước khi giao việc; 37 gói việc (WP-01..WP-37, vài gói chia a/b) chia 7 đợt, đợt 0-4 chạy được bằng toolchain hiện có (bản lai R2R), chỉ đợt 5 cần MSVC + Windows SDK; còn chờ chủ dự án chốt NE-1..NE-9.
---

# NO-WPF-EXEC-PLAN - kế hoạch thực thi bỏ WPF bằng nhiều agent song song (2026-10-10)

Trạng thái: **KẾ HOẠCH, chưa có mã nào đổi.** Kế hoạch cấp cao (vì sao, số đo, giai đoạn P-1..P6, quyết định NW-1..NW-5):
[NO-WPF-MIGRATION-PLAN](NO-WPF-MIGRATION-PLAN.md). Kết quả P-1: [P1-STARTUP-IN-WPF](P1-STARTUP-IN-WPF.md),
[perf](../perf/2026-10-10-p1-startup-in-wpf.md). Vì sao không Rust: [RUST-MIGRATION-EVAL](RUST-MIGRATION-EVAL.md).

Tài liệu này biến kế hoạch cấp cao thành việc giao được cho agent. Chia làm hai file:

- **File này:** nguyên tắc và bất biến (mục 2), cấu trúc project (mục 3), kiểm kê rò rỉ WPF (mục 4), **hợp đồng giao diện
  đóng băng** (mục 5), đợt và lịch song song (mục 6), chạy song song hai bản, tương đương hành vi, perf gate, rollback (mục 7),
  toolchain (mục 8), rủi ro và quyết định cần chốt (mục 9), điểm chưa chắc (mục 10).
- **[NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md):** thẻ chi tiết của từng gói WP-01..WP-37, mẫu prompt giao việc, checklist
  review/hợp nhất của lead.

**Agent nhận một gói chỉ cần đọc:** T0 (AGENTS.md, task_on_progress.md, docs/INDEX.md, AGENTS.local.md) + mục 2, 5 (chỉ các hợp
đồng gói đó sở hữu/tiêu thụ) và 7.3 của file này + thẻ gói của mình trong file WP. Không đọc lại cả thư mục decisions/.

Ánh xạ sang giai đoạn của kế hoạch cấp cao: P1 = đợt 1 (Imaging); P2 = WP-11, WP-12 (đợt 1-2) + WP-33, WP-34 (đợt 5);
P3 = đợt 2-4; P4 = WP-30 + beta; P5 = WP-35, WP-36; P6 = đợt 6. Spike S1 (P0) = WP-32, không chặn đợt 1-4 (xem NE-1).

## 1. Tóm tắt

- **Cách làm:** viết **project mới** `PhotoReview.Shell.Win32` (exe `PhotoReview.exe`) cho cửa sổ xem ảnh; app WPF
  `PhotoReview.App.exe` **tiếp tục được phát hành và chạy nguyên** tới khi bản mới đạt tương đương hành vi (mục 7.3). Hai bản dùng
  chung Core, Imaging*, Platform.Windows và một project mới `PhotoReview.App.Shared` (ViewModels, Coordinators, logic Input thuần,
  engine viewport thuần) tách ra từ App bằng việc **dời file**, không viết lại.
- **Đổi trên app WPF chỉ là tách seam giữ nguyên hành vi** (đợt 1): Imaging bỏ `BitmapSource` (thay bằng `PixelBuffer` +
  codec nền tảng), lớp Input bỏ `System.Windows.Point/Key/MouseButton`, placement/F11 nhận HWND. Mỗi PR như vậy phải để
  toàn bộ test hiện có xanh và perf gate không tệ hơn (mục 7.4).
- **18 hợp đồng** (C-01..C-18, mục 5) là ranh giới giữa các gói. Chúng được tạo dưới dạng file C# thật trong một PR đầu tiên
  (WP-01) và được khoá bằng test kiến trúc so chữ ký (`ContractSurfaceTests`); gói nào cần đổi hợp đồng phải dừng và báo lead.
- **37 gói (WP-01..WP-37), 7 đợt (0-6).** Đợt 0-4 (tới bản "lai" R2R: cửa sổ chính Win32, cửa sổ phụ WPF nạp muộn) **chạy hoàn toàn bằng
  toolchain đang có** (.NET SDK 10.0.401, crossgen2 trong cache NuGet). Chỉ WP-32 (đo spike S1 Native AOT) và bước publish AOT của
  WP-36 cần MSVC + Windows SDK (mục 8).
- **Công ước lượng:** đợt 0-4: ~400-590 giờ-agent cho các gói + ~120-170 giờ của lead (hợp nhất, review, perf gate) = ~520-760 giờ
  (khớp 12-19 người-tuần của kế hoạch cấp cao); đợt 5-6: ~230-370 giờ (phần lớn là WP-35 cửa sổ phụ). Song song tối đa: 6-8 agent ở đợt 1 và 2, 5-7 ở đợt 3.
- **Cần chủ dự án chốt trước khi giao đợt 1:** NE-1 (có chờ spike S1 không), NE-2 (tên exe/project), NE-3 (thanh cuộn), NE-5
  (cửa sổ phụ WPF chạy cùng tiến trình hay tách), NE-8 (fallback decoder = NW-4). Các NE còn lại chốt trước đợt 3 (mục 9).

## 2. Nguyên tắc và bất biến

### 2.1 Nguyên tắc thực thi (bắt buộc với mọi gói)

| # | Nguyên tắc | Kiểm bằng |
|---|---|---|
| N-1 | **App WPF không được đổi hành vi nhìn thấy** tới đợt 6. Chỉ được: dời file sang project dùng chung; thay kiểu WPF ở *ranh giới* bằng kiểu không-WPF với adapter tại chỗ; thêm seam (interface, factory). Không thêm tính năng UI mới vào bản WPF từ đầu đợt 3 (chỉ sửa lỗi) - xem NE-7. | Toàn bộ test hiện có (9.341 test, gồm `Category=UI`) xanh trên mỗi PR; perf gate đợt 1 (mục 7.4) |
| N-2 | Bản mới là **project riêng**; không `#if WPF` trong mã dùng chung; không tham chiếu `PresentationFramework/PresentationCore/WindowsBase` từ `App.Shared`, `Imaging*`, `Shell.*` (trừ `Shell.WpfBridge`). | `LayerDependencyTests` (luật mới mục 3.3) |
| N-3 | **Dữ liệu người dùng dùng chung, không migrate, không đổi định dạng:** `config.json`, `Sessions\`, journal, `cache\`, `thumbnails\`, `window-placement.json`, `Languages\`. Bản mới đọc mọi file bản cũ ghi và ngược lại; giá trị không hiểu được (ví dụ `DecoderBackend=Wpf` ở bản Win32) được **giữ nguyên khi ghi lại**, chỉ ánh xạ lúc dùng. | Test round-trip hai chiều (WP-06, WP-08, WP-20) |
| N-4 | Bất biến lõi giữ nguyên, cùng một lớp bảo vệ (bảng 2.2). Logic file action/journal/undo **không bị port**, chỉ được gọi từ shell mới. | Test Core/Imaging giữ nguyên, chạy cho cả hai bản |
| N-5 | **ADR 0005** áp dụng y nguyên cho `Shell.Win32`: tầng UI không `ConfigureAwait(false)`, không chặn đồng bộ trên Task; continuation quay về `SynchronizationContext` của UI thread Win32 (C-04). | `AppThreadAffinityTests` mở rộng quét `src/PhotoReview.App.Shared` + `src/PhotoReview.Shell.*` |
| N-6 | **T89/Fit:** vòng hội tụ `FitViewController` (3 lượt) được giữ nguyên cả ở bản mới (rẻ với engine đồng bộ); không rút gọn thành một lượt khi chưa có bằng chứng T89. Mọi hành vi zoom/pan/Fit của bản mới phải khớp bản WPF qua bộ golden (mục 7.3) **trước** khi port tính năng phụ thuộc. | Golden G-VIEW, G-INPUT (WP-10, WP-16) |
| N-7 | **Thùng rác thật:** không chạy mã xoá/quét/làm rỗng Thùng rác thật, kể cả mutation check. Bản mới gọi `IRecycleBin` sẵn có; test dùng fake; Native test chỉ xoá item của chính nó. | Rà review; `RealOsResourceTestsAreCategorised` |
| N-8 | **Không SendInput/SetForegroundWindow** trong test; test UI của bản mới gửi `WM_*` vào chính cửa sổ của nó bằng `SendMessage/PostMessage` và chạy trên desktop ẩn. | Review + test kiến trúc mới `ShellTestsDoNotUseSendInput` (WP-27) |
| N-9 | **Tốc độ duyệt là ưu tiên 1:** chuyển ảnh trong cache (key -> present) P50/P95 của bản mới không tệ hơn bản WPF; RAM peak không tăng quá 5 %. Tốc độ khởi động là lý do của cả dự án: mục tiêu ảnh đầu <= 450 ms (R2R) ở cuối đợt 3. | Perf gate mục 7.4 |
| N-10 | **i18n:** mọi chuỗi giao diện đi qua `Tr.*` (Core, ADR 0006); khoá mới thêm **cuối** `en.json`/`vi.json`, tiền tố `shell.`; không chuỗi tiếng Việt trong `.cs`; không dùng `Tr` cho log/CSV/journal. | `LocalizationGuardTests`, `LocalizedTextNotMachineReadableTests`, `tools/i18n-check.ps1` |
| N-11 | **AOT-ready từ đầu cho mã mới:** `Shell.*`, `App.Shared` dùng `LibraryImport`, `[GeneratedComInterface]`, `UnmanagedCallersOnly`, `JsonSerializerContext`; không reflection/`dynamic`/`Type.GetTypeFromProgID`; bật `IsAotCompatible=true` ngay (analyzer chạy được không cần MSVC). | Build Release 0 cảnh báo (IL2xxx/IL3xxx là lỗi) |
| N-12 | **Không gói NuGet mới** cho đợt 0-4 (COM sinh mã có trong SDK; không CsWin32). Thêm gói phải có quyết định riêng. | `Directory.Packages.props` không đổi (review) |
| N-13 | Mỗi gói: worktree riêng từ `origin/master`, PR riêng vào `master`, **không merge**, không stacked PR; không sửa `task_on_progress.md`/`ACTIVE-TASKS.md`; mọi lần chạy test có hang guard. | Checklist lead (file WP) |

### 2.2 Bất biến lõi và cách giữ ở bản mới

| Bất biến | Lớp bảo vệ hiện tại | Ở bản Win32 |
|---|---|---|
| INV-1 cache identity, không present pixel cũ | `ImageCacheKey`, `PreviewImageService`, `ImagePresenter`, `GenerationClock` | Không đổi (dùng chung). `GpuImageCache` (C-10) khoá theo `PixelBuffer.Id`, không theo path - không thể trình bày nhầm ảnh vì key ảnh vẫn do presenter chọn |
| INV-2 clear/evict vô hiệu in-flight | Imaging caches | Không đổi; `GpuImageCache.Clear()` gọi cùng chỗ với `ClearCache` của VM (WP-21) |
| INV-3/4/5 advance một lần, một action, action cũ không sửa catalog mới | `MainViewModel`, `FileActionService`, `GenerationClock` | Không đổi (VM dùng chung). Shell chỉ gọi lệnh VM |
| INV-6 Recycle + journal + reconcile sau instance lock | `FileActionService`, `OperationJournal`, `JournalStartupRecovery` | Thứ tự khởi động giữ nguyên: reconcile sau `InstanceScope.TryAcquire`, trước khi cho phép file action (WP-20, test `ShellStartupOrderTests`) |
| INV-7/9 snapshot Explorer, mở file present ngay | `FolderLoadCoordinator` | Không đổi |
| INV-8 share ReadWrite/Delete | decoder | Không đổi (decoder dùng chung) |
| INV-10 log mặc định tắt | `FileLog`, `AppLog` | `AppLog` dời sang App.Shared; shell không tạo log khi tắt (test) |
| INV-11 config hỏng backup/reset | `SettingsStore` | Không đổi |
| INV-12 fallback decoder, cache không lẫn backend | `ImageDecoderFactory`, `FallbackImageDecoder` | Bản WPF: giữ fallback WPF (qua `Imaging.Wpf`). Bản Win32: fallback theo NE-8 (khuyến nghị TurboJpeg cho JPEG + WIC thứ hai), `ActualBackend` vẫn vào `ImageCacheKey` |
| ADR 0003 journal tail-read | Core | Không đổi |
| ADR 0005 thread affinity | test quét source | Mở rộng sang App.Shared + Shell.* (N-5) |
| ADR 0007 độ bền ghi | Core | Không đổi; placement ghi qua `WriteAtomically` dùng chung |
| ADR 0008 zoom = pixel nguồn | `ViewerState` | `ViewerState` dùng chung; renderer vẽ ảnh ở `Original x Zoom` pixel thiết bị (DIP x DpiScale) |
| Undo | `UndoService` | Không đổi |
| Tốc độ duyệt | perf session | Perf gate mục 7.4 |

## 3. Cấu trúc project mới

### 3.1 Cây project (sau đợt 2)

```text
src/
  PhotoReview.Core                  (giữ)            net10.0
  PhotoReview.Imaging               (sửa, bỏ UseWPF) net10.0-windows  + PixelBuffer, codec, encoder WIC
  PhotoReview.Imaging.Wpf           (MỚI, UseWPF)    WpfBitmapImageDecoder, WpfDecodedImage, WpfImageAdapter,
                                                     WpfExifReader, WpfExifOrientation, WpfBitmapSourceCodec  (chỉ App WPF tham chiếu)
  PhotoReview.Imaging.Raw / LibRaw / TurboJpeg (sửa, bỏ UseWPF)
  PhotoReview.Benchmarking          (bỏ UseWPF)
  PhotoReview.Platform.Windows      (giữ; đợt 5 sửa COM cho AOT)
  PhotoReview.App.Shared            (MỚI, net10.0-windows, không WPF) ViewModels/, Coordinators/ (trừ phần WPF),
                                                     Input/ (thuần), Viewport/ (engine C-08), Menus/ (C-12), Composition/Common,
                                                     Services/ (thuần), AppLog, TaskLogging, LaunchArguments, BuildInfo, FileHashService
  PhotoReview.App                   (giữ, WPF)       MainWindow, cửa sổ phụ (tới WP-19b), adapter WPF, App.xaml.cs
  PhotoReview.App.WpfWindows        (MỚI ở WP-19b, UseWPF, library) Settings/Recovery/ActionProfiles/BatchReview/Benchmark/
                                                     Diagnostics/SkippedFiles/ClickZoomCustom + Themes + TrExtension (dời từ App)
  PhotoReview.Shell.Interop         (MỚI, không logic) LibraryImport user32/dwmapi/shcore/kernel32/ole32;
                                                     [GeneratedComInterface] D3D11/DXGI/D2D1/DWrite/IFileOpenDialog/IDropTarget
  PhotoReview.Shell.Rendering       (MỚI)            surface D2D, GpuImageCache, text, primitive vẽ (C-09, C-10, C-11b)
  PhotoReview.Shell.WpfBridge       (MỚI, UseWPF)    nạp muộn cửa sổ phụ WPF cho shell (C-13b)
  PhotoReview.Shell.Win32           (MỚI, WinExe, AssemblyName=PhotoReview) cửa sổ, message loop, dispatcher, input,
                                                     overlay/HUD/toolbar/menu, dialog native, composition root, startup
tests/
  PhotoReview.Shell.Tests           (MỚI, net10.0-windows, không WPF)  unit + golden + render WARP offscreen
  PhotoReview.Shell.Integration.Tests (MỚI)          HWND thật, Category=UI, desktop ẩn
  (giữ) Core/Imaging/App/Integration/Architecture.Tests; App.Tests vẫn test các kiểu đã dời (qua tham chiếu App.Shared)
tests/Fixtures/golden/              (MỚI) viewport-layout.v1.json, input-scripts.v1.json, key-names.v1.json,
                                          context-menu.v1.json, pixel/*.png  (C-17)
```

Tên project `Shell.*` là đề xuất (NE-2). Namespace của file dời sang App.Shared **giữ nguyên** (`PhotoReview.App.ViewModels`,
`PhotoReview.App.Coordinators`, `PhotoReview.App.Input`, ...) để diff chỉ là dời file + csproj; kiểu `internal` cần cho App/Shell
được mở bằng `InternalsVisibleTo` (App, Shell.Win32, App.Tests, Shell.Tests, Integration.Tests, Shell.Integration.Tests,
Benchmark.Cli). ADR 0004 ("không tách Presentation") hết điều kiện vì đã có consumer thứ hai không cần `MainWindow`; ADR 0010
mới (WP-31) thay 0002 và 0004.

### 3.2 Hướng phụ thuộc

```text
Shell.Win32 -> Shell.Rendering -> Shell.Interop
Shell.Win32 -> App.Shared -> Core, Imaging*, Platform.Windows, Benchmarking
Shell.Win32 -> Shell.WpfBridge (chỉ qua Lazy + [MethodImpl(NoInlining)], C-13b) -> App.WpfWindows -> App.Shared
App (WPF)   -> App.Shared, App.WpfWindows, Imaging.Wpf
Imaging.Wpf -> Imaging (không ngược lại)
Shell.Rendering -> Imaging (chỉ PixelBuffer), Core (Abstractions)
```

### 3.3 Thay đổi Architecture.Tests (WP-01 tạo khung, các gói bật dần)

| Luật | Nội dung | Gói bật |
|---|---|---|
| L-IMG (sửa Rule 2/9/10 + K-1) | `PhotoReview.Imaging`, `.Raw`, `.LibRaw`, `.TurboJpeg`, `PhotoReview.Benchmarking` không phụ thuộc `PresentationCore`, `PresentationFramework`, `WindowsBase`, `System.Xaml`, `PhotoReview.Imaging.Wpf`. `ImagingPublicSurfaceTests` mở rộng sang mọi namespace của Imaging (cả `Decoding`). | WP-06 |
| L-SHARED | `PhotoReview.App.Shared` không phụ thuộc WPF, `PhotoReview.App`, `PhotoReview.Shell.*`. Thay Rule 6 (K-2) bằng luật theo assembly. | WP-09 |
| L-SHELL | `Shell.Interop/Rendering/Win32` không phụ thuộc WPF/WinForms/`PhotoReview.App`/`App.WpfWindows`; `Shell.Win32` chỉ được tham chiếu `Shell.WpfBridge` ở đúng một file (`Dialogs/WpfBridgeLoader.cs`). | WP-01 (khung), WP-19b |
| L-AFFINITY | `AppThreadAffinityTests` quét thêm `src/PhotoReview.App.Shared`, `src/PhotoReview.Shell.Win32`. | WP-09, WP-14 |
| L-CONTRACT (mới) | `ContractSurfaceTests`: chữ ký public/internal của các kiểu hợp đồng (mục 5) khớp file duyệt `tests/PhotoReview.Architecture.Tests/Approved/contracts.v1.txt`; đổi hợp đồng = đổi file duyệt trong PR của lead. | WP-01 |
| L-AOT | `Shell.*`, `App.Shared` có `IsAotCompatible=true`; không `[ComImport]`, không `DllImport` (chỉ `LibraryImport`), không `dynamic`. | WP-01 |
| L-COMP (sửa AppCompositionTests/DialogBoundaryTests) | `MessageBox`/`Window` chỉ trong `App`/`App.WpfWindows`; Shell không `MessageBoxW` ngoài `Win32DialogService`. | WP-19a/b |
| L-PINV | `Shell.Win32`/`Rendering` không khai báo P/Invoke ngoài `Shell.Interop`. | WP-13 |

## 4. Kiểm kê rò rỉ WPF vào lớp dùng chung (grep + đọc tay, origin/master `df62253e`)

### 4.1 Theo project

| Project | `UseWPF` | File dùng API WPF thật | Ghi chú |
|---|---|---|---|
| Core | không | 0 (chỉ comment: `ImmediateUiScheduler`, `ISourceReader`, `PhotoReviewPerf`, `ReviewMetrics`, `ShortcutKeyCanonical`, `SettingsStore`) | Từ vựng phím tắt trong `config.json` là **tên enum `System.Windows.Input.Key`** (vd. `D1`, `Add`, `PageDown`) |
| Platform.Windows | không | 0 | 23 `DllImport`, COM `[ComImport]` (`ComEnumeration`, `ExplorerComInterop`), late-bound (`ExplorerOrderService`, `WindowsRecycleBin` `dynamic`) - chặn AOT, không chặn bản lai |
| PerfAnalysis, Localization.Generator | không | 0 | - |
| **Imaging** | **có** | 12/50 (bảng 4.2) | duy nhất có rò rỉ thật |
| Imaging.TurboJpeg | có | 1 (`TurboJpegDecoder`) | `BitmapSource.Create` + `WpfDecodedImage` + `TransformedBitmap` cho orientation |
| Imaging.LibRaw | có | 2 (`RgbBgraResampler.ToBitmap`, `LibRawDecoder` tạo `WpfDecodedImage`) | |
| Imaging.Raw | có | 0 (chỉ gọi `ExifOrientation.IsTransposed`, `IDecodedImage`) | bỏ `UseWPF` khi `ExifOrientation` sạch |
| Benchmarking | có | 0 (comment) | bỏ `UseWPF` |
| App: ViewModels | (App) | 0 System.Windows, **nhưng** `MainViewModel` mặc định `Clipboard = new WpfClipboardService()` | phải tiêm qua DI (WP-09) |
| App: Coordinators | | 1/25: `FullscreenWindowPlacer` (`Window`, `WindowState`, `WindowInteropHelper`) | WP-08 |
| App: Input | | 5/9: `IImageSurface` (`Point`), `PointerInputController` (`Point`, `Key`, `MouseButton`), `MouseGestures` (`Point` ở `ResolveKeyboardZoomAnchor`), `ShortcutRouter` (`Key`, `ModifierKeys`), `WheelMessageSource` (`Point`) | WP-07 |
| App: gốc | | `MainWindowConvergence.ViewportSnapshot` (trường `Visibility`), `WindowPlacementService` (`Window`, `WindowState`), `MainWindowHelpers` 0 | WP-07, WP-08 |
| App: Services | | `ShortcutKeyName` (`Key`), `WpfKeyNameValidator`, `DispatcherUiScheduler`, `AmbientDispatcher`, `DarkTitleBarChrome`, `StartupWindowReveal`, `StartupWarmup`, `InitialViewportPredictor` (so sánh `WindowState`), `WpfImageSurface`, `WpfPresentationSink`, `WpfDialogService`, `IDialogHost`, `WpfFolderPicker`, `WpfClipboardService` | adapter ở lại App; bản Win32 có bản riêng |
| App: Composition | | `MainViewModelCompositionRoot` tạo `WpfPresentationSink` | sink tiêm qua factory (WP-09) |
| App: Localization | | `TrExtension` (MarkupExtension), `LocalizationSource` (indexer INotifyPropertyChanged - không WPF) | `LocalizationService` dời App.Shared; `TrExtension` ở lại phía WPF |
| tools/Benchmark.Cli | (qua App) | `WpfTestHost`, `LocalUiNextProbe`, `PerfSession` dựng `MainWindow` | host shell mới ở WP-29; bản WPF giữ |

### 4.2 Imaging chi tiết và kế hoạch tách

| File | API WPF | Thay bằng | Gói |
|---|---|---|---|
| `Decoding/Wic/WicDirectDecoder.cs` L246-269 | `BitmapSource.Create(... Bgr32/Pbgra32)` + `Freeze` | WIC `CopyPixels` thẳng vào `PixelBuffer` (C-01), rồi `IPlatformImageCodec.FromPixels` (C-02). Giữ đúng 1 lần copy với codec pass-through; bản WPF: 2 lần như hôm nay (WIC -> buffer -> MIL) | WP-03 |
| `Decoding/ExifOrientation.cs` | `BitmapMetadata`, `TransformedBitmap`, `ScaleTransform/RotateTransform/TransformGroup` | `ExifOrientation` giữ `IsTransposed`/chuẩn hoá (thuần); xoay/lật = `PixelOps.ApplyOrientation` (C-03, managed, có test so với WPF) hoặc `IWICBitmapFlipRotator` trên nguồn WIC; phần WPF dời `Imaging.Wpf.WpfExifOrientation` | WP-03 |
| `Decoding/EmbeddedThumbnailReader.cs` | `BitmapDecoder`, `FormatConvertedBitmap`, `BitmapSource.Create`, `BitmapMetadata` | WIC `IWICBitmapFrameDecode::GetThumbnail` + `WICConvertBitmapSource(GUID_WICPixelFormat32bppPBGRA)` + query reader | WP-03 |
| `Decoding/WpfExifReader.cs` | `BitmapMetadata.GetQuery` | `WicExifReader` dùng `IWICMetadataQueryReader` (đã có trong `WicInterop`) + `ExifQueryInterpreter` sẵn có | WP-03 |
| `Decoding/WpfBitmapImageDecoder.cs`, `WpfDecodedImage.cs`, `WpfImageAdapter.cs` | `BitmapImage`, `BitmapFrame`, ... | **Dời nguyên** sang `Imaging.Wpf` (fallback INV-12 của bản WPF); thêm `DecodedImage` (C-02) thuần cho mọi decoder khác | WP-06 |
| `Decoding/ImageDecoderFactory.cs` L35 | `new WpfBitmapImageDecoder()` | factory nhận `Func<IImageDecoder>? wpfDecoderFactory` (null ở bản Win32 -> backend `Wpf` ánh xạ `WicDirect`, không ghi lại config) | WP-06 |
| `Caching/PreviewCacheFile.cs` | `JpegBitmapEncoder`, `BitmapImage` (đọc), `FormatConvertedBitmap`, `PixelFormats`, `CopyPixels`, `HasAlpha/IsFullyOpaque(BitmapSource)` | encoder/decoder WIC (`IWICBitmapEncoder` JPEG, `ImageQuality = DefaultJpegQuality/100`) trên `PixelBuffer`; `HasAlpha/IsFullyOpaque(PixelBuffer)`; **header + payload không đổi định dạng** (test đọc file do bản cũ ghi, và bản cũ đọc file bản mới ghi) | WP-04 |
| `Caching/DiskCacheStore.cs` L79-95 | `PngBitmapEncoder` | WIC PNG encoder trên `PixelBuffer` | WP-04 |
| `Caching/PreviewImageService.cs` L71-72, 632, 873 | `Channel<(BitmapSource ...)>`, cast | channel giữ `IDecodedImage`; persist worker lấy pixel qua `codec.ToPixels` (C-02 `PixelLease`) | WP-04 |
| `Caching/ThumbnailCache.cs` L6, 223 | `using` thừa; `WpfBitmapImageDecoder.DecodeWithFallback` | tiêm `IImageDecoder thumbnailDecoder` (bản WPF truyền decoder WPF, bản Win32 truyền WIC) | WP-04 |
| `Caching/AtomicCacheFile.cs` L7 | cref trong doc | sửa doc | WP-04 |
| TurboJpeg `TurboJpegDecoder.cs` L148-215 | `BitmapSource.Create`, `TransformedBitmap`, `WpfDecodedImage` | decode thẳng vào `PixelBuffer` (tjDecompress2 vào `Address`), `PixelOps.ApplyOrientation`, `DecodedImage` | WP-05 |
| LibRaw `RgbBgraResampler.cs` L78-82, `LibRawDecoder.cs` L212-267 | `BitmapSource.Create`, `WpfDecodedImage` | `ToPixels(BgraBuffer) -> PixelBuffer`, `DecodedImage` | WP-05 |

Chữ ký public đang lộ WPF (phải biến mất khỏi Imaging): `ExifOrientation.Read(BitmapMetadata?)`, `ExifOrientation.Apply(BitmapSource,int)`,
`ExifOrientation.CreateTransform(int)`, `WpfDecodedImage.Source`, `WpfDecodedImage(BitmapSource,...)`, `WpfImageAdapter.Materialize(BitmapSource)`;
internal: `WpfExifReader.Read(BitmapMetadata?)`, `DiskCacheStore.WriteAtomicallyAsync(BitmapSource,...)`, `PreviewCacheFile.ReadResult(BitmapSource Bitmap,...)`,
`PreviewCacheFile.WriteAtomicallyAsync(BitmapSource,...)`, `HasAlpha(BitmapSource)`, `IsFullyOpaque(BitmapSource)`, `RgbBgraResampler.ToBitmap(...)`.

### 4.3 Test đang dính WPF trong helper (theo kế hoạch cấp cao): Imaging.Tests ~40 file dùng `BitmapSource` trong helper/assert ->
helper `PixelAssert` mới trên `PixelBuffer` (WP-02 tạo, WP-03..05 dùng); test dành riêng cho backend WPF dời sang tham chiếu `Imaging.Wpf`.
`TestSupport.Windows` giữ WPF (fixture) cho tới đợt 6.

## 5. Hợp đồng giao diện (ĐÓNG BĂNG ở WP-01)

Quy tắc: WP-01 tạo **đúng** các khai báo dưới đây (thân phương thức `throw new NotImplementedException()` hoặc mặc định an toàn
nếu ghi rõ), cộng file duyệt `contracts.v1.txt`. Gói "sở hữu" viết phần thực thi; gói "tiêu thụ" chỉ gọi. Thêm thành viên mới
vào hợp đồng = PR nhỏ của lead (cập nhật file duyệt), không làm trong gói tính năng. Đơn vị: **DIP** cho toạ độ layout (giống WPF),
**px** cho pixel thiết bị; mọi `double` toạ độ là DIP trừ khi tên có `Pixel`.

| ID | Hợp đồng | Namespace / project | Sở hữu (thực thi) | Tiêu thụ |
|---|---|---|---|---|
| C-01 | `PixelBuffer`, `PixelLayout` | `PhotoReview.Imaging.Pixels` / Imaging | WP-02 | WP-03..06, WP-15, WP-21, WP-28 |
| C-02 | `IPlatformImageCodec`, `PixelLease`, `DecodedImage`, `PixelBufferImageCodec` | `PhotoReview.Imaging.Decoding` / Imaging | WP-02 (WPF codec: WP-06) | WP-03..06, WP-20, WP-21 |
| C-03 | `PixelOps`, `ExifOrientation` (phần thuần) | `PhotoReview.Imaging.Pixels` / Imaging | WP-02 | WP-03, WP-05 |
| C-04 | `UiPriority`, `IUiDispatcher` | `PhotoReview.Core.Abstractions` / Core | WP-14 (Win32), WP-07 (WPF adapter) | mọi gói UI |
| C-05 | `IFrameClock`, `FrameTick`, `FrameTickEventArgs` | `PhotoReview.App.Input` / App.Shared | WP-14 | WP-16, WP-21..23 |
| C-06 | `PointD`, `RectD`, `SizeD`, `PointerButton`, `KeyModifiers`, `KeyId`, `KeyIdMapping` | `PhotoReview.App.Input` / App.Shared | WP-07 | WP-16, WP-22, WP-24 |
| C-07 | `IImageSurface`, `IFitSurface`, `ViewportSnapshot` (bản không-WPF) | `PhotoReview.App.Input`, `PhotoReview.App.Coordinators` / App.Shared | WP-07 (WPF), WP-16 (Win32) | `PointerInputController`, `FitViewController` |
| C-08 | `ViewportInput`, `ViewportLayout`, `ViewportLayoutEngine`, `ScrollBarPolicy` | `PhotoReview.App.Viewport` / App.Shared | WP-16 | WP-21, WP-26, WP-10 (golden) |
| C-09 | `IRenderSurface`, `IDrawContext`, `PresentResult`, `ImageInterpolation`, `ColorF` | `PhotoReview.Shell.Rendering` | WP-15 | WP-17, WP-21, WP-23, WP-26, WP-28 |
| C-10 | `IGpuImage`, `IGpuImageCache` | `PhotoReview.Shell.Rendering` | WP-15 | WP-21 |
| C-11 | `ITextRenderer`, `TextStyle`, `ITextLayout`, `IOverlayElement`, `OverlayLayoutContext`, `IAnimator` | `PhotoReview.Shell.Rendering` (text), `PhotoReview.Shell.Win32.Overlay` | WP-17 | WP-23, WP-21 (crossfade), WP-26 |
| C-12 | `MenuItemModel`, `MenuItemKind`, `ContextMenuModelBuilder`, `IPopupMenuHost` | `PhotoReview.App.Menus` / App.Shared; host: `PhotoReview.Shell.Win32.Menus` | builder: WP-18; host: WP-18 | WP-24, WP-23 (menu Tools) |
| C-13 | `IDialogService` (giữ), `IFolderPicker` (giữ), `IClipboardService` (dời), `IZoomPromptService` (mới), `ISecondaryWindowHost` (mới) | Core / App.Shared / Shell.WpfBridge | WP-19a (native), WP-19b (bridge) | WP-20, WP-23, WP-24 |
| C-14 | `WindowPlacementData`, `IWindowPlacementStore`, `WindowShowState`, `IFullscreenController` | `PhotoReview.App.Windowing` / App.Shared | WP-08 | WP-20, WP-25 |
| C-15 | `IShellWindow`, `ShellCursor`, `IWindowMessageHandler`, `WindowMessage` | `PhotoReview.Shell.Win32.Hosting` | WP-14 | WP-16, WP-20..26 |
| C-16 | `SharedServiceOptions`, `SharedServiceRegistration.AddPhotoReviewShared`, `IPresentationSinkFactory`, `ShellStartupContext` | `PhotoReview.App.Composition` / App.Shared; `PhotoReview.Shell.Win32.Startup` | WP-09 (shared), WP-20 (shell) | App WPF, Shell |
| C-17 | Lược đồ golden JSON (`GoldenViewportCase`, `GoldenInputScript`, `GoldenKeyName`, `GoldenMenuCase`) | `PhotoReview.TestSupport.Golden` / TestSupport | WP-10 | WP-16, WP-22, WP-24, WP-07 |
| C-18 | Tên mốc perf + `ShellPerfMarks` | `PhotoReview.Core.Diagnostics` / Core | WP-20 | WP-29 |

### C-01 PixelBuffer

```csharp
namespace PhotoReview.Imaging.Pixels;

/// 4 byte/pixel, thứ tự byte B,G,R,X|A (khớp GUID_WICPixelFormat32bppBGR / 32bppPBGRA và DXGI_FORMAT_B8G8R8A8_UNORM).
public enum PixelLayout { Bgr32 = 0, Pbgra32 = 1 }

/// Bộ nhớ native (NativeMemory.AlignedAlloc, căn 64 byte) + GC.AddMemoryPressure(ByteCount); giải phóng ở Dispose
/// hoặc finalizer (SafeHandle bên trong). KHÔNG đếm tham chiếu: cache/presenter giữ tham chiếu managed như với
/// BitmapSource hôm nay; chỉ chủ sở hữu duy nhất (bộ đệm tạm của decoder, PixelLease có Owned=true) được Dispose.
public sealed class PixelBuffer : IDisposable
{
    public static PixelBuffer Allocate(int width, int height, PixelLayout layout); // width,height >= 1; ném OutOfMemoryException/ArgumentOutOfRangeException
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }          // = Width * 4, không padding
    public PixelLayout Layout { get; }
    public long ByteCount { get; }      // = (long)Stride * Height
    public long Id { get; }             // duy nhất trong tiến trình, tăng dần (khoá GpuImageCache)
    public bool IsDisposed { get; }
    public nint Address { get; }        // ném ObjectDisposedException nếu đã Dispose
    public Span<byte> GetRow(int y);    // 0 <= y < Height
    public bool TryGetSpan(out Span<byte> span); // false khi ByteCount > int.MaxValue
    public void Dispose();
}
```

### C-02 Codec nền tảng và ảnh đã giải mã không-WPF

```csharp
namespace PhotoReview.Imaging.Decoding;

/// Chuyển giữa pixel và "ảnh nền tảng" mà IDecodedImage.PlatformImage mang (object, giữ nguyên hợp đồng hiện tại).
/// Bản WPF: WpfBitmapSourceCodec (Imaging.Wpf) -> BitmapSource đã Freeze. Bản Win32: PixelBufferImageCodec -> chính PixelBuffer.
/// Thread-safe, gọi trên luồng decode/persist, không bao giờ trên UI thread trong hot path.
public interface IPlatformImageCodec
{
    string Name { get; }                              // "wpf" | "pixels" (chỉ để log/metrics)
    object FromPixels(PixelBuffer pixels);            // NHẬN quyền sở hữu pixels (WPF: copy rồi Dispose; pixels: trả nguyên)
    PixelLease ToPixels(object platformImage);        // để encode cache; ném ArgumentException nếu kiểu lạ
}

public readonly struct PixelLease : IDisposable
{
    public PixelLease(PixelBuffer pixels, bool owned);
    public PixelBuffer Pixels { get; }
    public bool Owned { get; }                        // true -> Dispose giải phóng (bản copy); false -> mượn
    public void Dispose();
}

public sealed class PixelBufferImageCodec : IPlatformImageCodec
{
    public static readonly PixelBufferImageCodec Instance;
    // FromPixels trả pixels; ToPixels trả new PixelLease((PixelBuffer)platformImage, owned: false)
}

/// Thay WpfDecodedImage cho mọi decoder không-WPF.
public sealed class DecodedImage : IDecodedImage
{
    public DecodedImage(object platformImage, int pixelWidth, int pixelHeight, long estimatedBytes,
        bool downscaled = false, int orientation = 1, DecoderBackend actualBackend = DecoderBackend.WicDirect,
        int originalWidth = 0, int originalHeight = 0, PhotoReview.Imaging.Metadata.ExifSummary? exif = null,
        bool isDegradedFallback = false);
    // các thuộc tính của IDecodedImage; OriginalWidth/Height = pixel khi truyền 0
}
```

Đăng ký: DI singleton `IPlatformImageCodec` (App WPF: `WpfBitmapSourceCodec.Instance`; Shell: `PixelBufferImageCodec.Instance`);
`WicDirectDecoder`, `TurboJpegDecoder`, `LibRawDecoder`, `PreviewCacheFile`, `DiskCacheStore`, `EmbeddedThumbnailReader` nhận codec qua
constructor (giá trị mặc định **không** được phép - tránh vô tình dùng sai codec).

### C-03 Thao tác pixel và orientation

```csharp
namespace PhotoReview.Imaging.Pixels;

public static class PixelOps
{
    /// orientation EXIF 1..8 (giá trị khác = 1). Trả về src nếu 1; ngược lại buffer mới và Dispose src (nhận quyền sở hữu).
    public static PixelBuffer ApplyOrientation(PixelBuffer src, int exifOrientation);
    public static bool HasAlpha(PixelBuffer px);        // Layout == Pbgra32 && !IsFullyOpaque
    public static bool IsFullyOpaque(PixelBuffer px);   // mọi A == 255 (Bgr32 -> true)
}
// PhotoReview.Imaging.Decoding.ExifOrientation giữ: public static bool IsTransposed(int orientation);
// public static int Normalize(int orientation); public static int ReadFromWic(...) là internal. Thành viên dùng kiểu WPF dời Imaging.Wpf.
```

### C-04 Dispatcher UI

```csharp
namespace PhotoReview.Core.Abstractions;

/// Ánh xạ WPF: Send->Send, Normal->Normal, Render->Render, Background->Background (DispatcherPriority).
public enum UiPriority { Send = 0, Normal = 1, Render = 2, Background = 3 }

public interface IUiDispatcher : IUiScheduler
{
    bool CheckAccess();
    void Post(Action action, UiPriority priority);                 // IUiScheduler.Post = Normal
    Task InvokeAsync(Action action, UiPriority priority);           // IUiScheduler.InvokeAsync = Normal
    ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default); // IUiScheduler.YieldAsync = Background
}
```

Win32 (`Shell.Win32.Hosting.Win32UiDispatcher`): hàng đợi theo mức + `PostMessage(hwndMessageOnly, WM_APP+1)`; Normal chạy trước
khi xử lý input kế tiếp; Render chạy sau khi xả Normal và trước khi vẽ khung; Background chỉ chạy khi hàng đợi message rỗng
(`MsgWaitForMultipleObjectsEx` + `PeekMessage`). `Win32UiSynchronizationContext.Post` = Normal; được cài trên UI thread trước khi tạo
service. `DispatcherUiScheduler` (WPF) cũng implement `IUiDispatcher` (WP-07) để mã dùng chung chỉ phụ thuộc interface.

### C-05 Đồng hồ khung hình

```csharp
namespace PhotoReview.App.Input;

public readonly record struct FrameTick(long Timestamp, double RenderingTimeMs, DisplayTiming? Timing); // Timestamp = Stopwatch ticks

public sealed class FrameTickEventArgs(FrameTick tick) : EventArgs { public FrameTick Tick { get; } = tick; }

public interface IFrameClock
{
    /// Phát trên UI thread, một lần mỗi khung sắp được trình bày, khi có ít nhất một subscriber hoặc RequestFrame() đang chờ.
    event EventHandler<FrameTickEventArgs>? Frame;
    void RequestFrame();
}
```

`IImageSurface.HookRenderFrame/UnhookRenderFrame/RenderingTime(EventArgs)` giữ chữ ký; bản Win32 trả `RenderingTime` từ
`FrameTickEventArgs`. Nguồn nhịp: waitable object của swap chain (`IDXGISwapChain2::GetFrameLatencyWaitableObject`) trong
`MsgWaitForMultipleObjectsEx`; `DisplayTiming` lấy từ `IDisplayClock` sẵn có (Platform.Windows).

### C-06 Kiểu nhập liệu không-WPF

```csharp
namespace PhotoReview.App.Input;

public readonly record struct PointD(double X, double Y);
public readonly record struct SizeD(double Width, double Height);
public readonly record struct RectD(double X, double Y, double Width, double Height)
{ public double Right => X + Width; public double Bottom => Y + Height; public bool Contains(PointD p); }

public enum PointerButton { Left = 0, Middle = 1, Right = 2, XButton1 = 3, XButton2 = 4 }   // = System.Windows.Input.MouseButton

[Flags] public enum KeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }   // = System.Windows.Input.ModifierKeys

/// Bản sao 1:1 tên + giá trị số của System.Windows.Input.Key (kể cả bí danh như Return = Enter = 6, Capital = CapsLock).
/// Lý do: config.json lưu tên Key của WPF; giữ y nguyên từ vựng mà không tham chiếu WPF.
public enum KeyId { None = 0, Cancel = 1, Back = 2, Tab = 3, LineFeed = 4, Clear = 5, Return = 6, Enter = 6, /* ... đủ tới DeadCharProcessed = 172 */ }

public static class KeyIdMapping
{
    public static KeyId FromVirtualKey(int virtualKey, bool isExtended);   // khớp KeyInterop.KeyFromVirtualKey (WPF)
    public static int ToVirtualKey(KeyId key);                              // khớp KeyInterop.VirtualKeyFromKey
    public static bool TryParse(string name, out KeyId key);               // Enum.TryParse(ignoreCase: false) + ShortcutKeyCanonical
}
```

Đổi chữ ký kéo theo (WP-07): `ShortcutRouter.TryResolve(KeyId key, KeyId systemKey, KeyModifiers modifiers, bool isFullscreen,
bool hasImage, bool hasComparePair = true, bool isCompareVisible = false, bool hasCapturePair = false)`;
`PointerInputController.OnImagePress(PointerButton changedButton, int clickCount, PointD position, int timestamp)`,
`OnImageMove(bool leftButtonPressed, PointD position, int timestamp)`, `OnImageRelease(PointD position, int timestamp)`,
`OnWheelAsync(WheelInput input, PointD position)`, `TryPanByArrow(KeyId key, bool isRepeat)`;
`PointerGestures.ResolveKeyboardZoomAnchor(KeyboardZoomAnchor mode, PointD? pointer, double viewportWidth, double viewportHeight) -> PointD`;
`WheelMessageSource.ScreenPoint(nint lParam) -> PointD`; `ShortcutKeyName.TryParse(string, out KeyId)`. Adapter WPF:
`(KeyId)(int)e.Key`, `new PointD(p.X, p.Y)`, `(PointerButton)(int)e.ChangedButton`, `(KeyModifiers)(int)Keyboard.Modifiers`.

### C-07 Bề mặt ảnh (bản không-WPF của seam hiện có)

```csharp
namespace PhotoReview.App.Input;
internal interface IImageSurface
{
    bool IsLoaded { get; }
    double HorizontalOffset { get; } double VerticalOffset { get; }
    double ViewportWidth { get; } double ViewportHeight { get; }
    double ExtentWidth { get; } double ExtentHeight { get; }
    (double Horizontal, double Vertical) DragThreshold { get; }     // DIP; Win32: GetSystemMetrics(SM_CXDRAG/SM_CYDRAG) / DpiScale
    void ScrollTo(double horizontal, double vertical);              // kẹp như ScrollViewer
    void UpdateLayout();                                             // Win32: tính lại ViewportLayout đồng bộ
    Task YieldToRenderAsync();                                       // Win32: IUiDispatcher.InvokeAsync(noop, UiPriority.Render)
    PointD ImageOrigin { get; }                                      // góc trên-trái phần tử ảnh trong toạ độ viewport
    PointD ToImageElement(PointD surfacePoint);
    double ImageActualWidth { get; } double ImageActualHeight { get; }
    (double Width, double Height)? SourceSize { get; }               // kích thước bitmap đang vẽ (DIP theo DPI 96 của bitmap, như BitmapSource.Width)
    void CaptureMouse(); void ReleaseMouseCapture();
    void SetPanCursor(bool panning);
    void HookRenderFrame(EventHandler handler); void UnhookRenderFrame(EventHandler handler);
    TimeSpan? RenderingTime(EventArgs e);
    long Timestamp { get; }
    DisplayTiming? DisplayTiming { get; }
    PointD? PointerPosition { get; }
}

namespace PhotoReview.App.Coordinators;
internal interface IFitSurface   // giữ nguyên thành viên hiện tại (ViewportSize, IsLoaded, UpdateLayout, UpdateFitSize, YieldToRenderAsync, Capture, ScrollHome)
// ViewportSnapshot: các trường Visibility đổi thành bool (HorizontalScrollBarVisible, VerticalScrollBarVisible); ViewportConvergence không đổi logic.
```

Lưu ý `SourceSize`: WPF `BitmapSource.Width` = pixel x 96 / DpiX của bitmap. Bản Win32 trả pixel x 96 / 96 (bitmap không mang DPI) -
**khác bản WPF nếu file có DPI khác 96**. WP-16 phải ghi golden cho ảnh 72/300 DPI và quyết định theo hành vi đo được của
`CalculateUniformImagePoint` (chỉ dùng tỉ lệ nên thường không ảnh hưởng) - xem mục 10.

### C-08 Engine viewport thuần (thay ScrollViewer + Image)

```csharp
namespace PhotoReview.App.Viewport;

public enum ScrollBarPolicy { Auto = 0, Overlay = 1, Hidden = 2 }   // Auto = như WPF (chiếm chỗ); chọn theo NE-3

public readonly record struct ViewportInput(
    double ClientWidth, double ClientHeight,          // DIP, vùng ImageScroll (cửa sổ trừ viền nếu có; overlay không trừ)
    double ScrollBarThickness,                        // DIP (WPF: SystemParameters.VerticalScrollBarWidth theo DarkScrollBars.xaml)
    ScrollBarPolicy ScrollBars,
    ViewerStretchMode Stretch,                        // từ ViewerState
    double ImageWidth, double ImageHeight,            // ViewerState.ImageWidth/Height (NaN trong Fit)
    double MaxImageWidth, double MaxImageHeight,      // ViewerState.MaxImageWidth/Height
    double BitmapWidth, double BitmapHeight);         // kích thước tự nhiên của bitmap (DIP) cho Stretch=Uniform

public readonly record struct ViewportLayout(
    double ViewportWidth, double ViewportHeight,      // = ScrollViewer.ViewportWidth/Height
    double ExtentWidth, double ExtentHeight,          // = ScrollViewer.ExtentWidth/Height
    bool HorizontalBarVisible, bool VerticalBarVisible,
    RectD ImageRect,                                  // vị trí phần tử ảnh trong toạ độ extent (căn giữa khi nhỏ hơn viewport)
    double MaxHorizontalOffset, double MaxVerticalOffset);

public static class ViewportLayoutEngine
{
    /// Mô phỏng: ScrollViewer(HorizontalScrollBarVisibility=Auto, VerticalScrollBarVisibility=Auto,
    /// HorizontalContentAlignment=Stretch, VerticalContentAlignment=Stretch) chứa Image(Stretch, MaxWidth/MaxHeight, Width/Height).
    /// Lặp điểm bất động cho thanh cuộn Auto (tối đa 3 vòng, giống MeasureOverride của ScrollViewer). Thuần, không cấp phát.
    public static ViewportLayout Compute(in ViewportInput input);
    public static (double Horizontal, double Vertical) ClampOffset(in ViewportLayout layout, double horizontal, double vertical);
}
```

Tiêu chí đúng: tái tạo `viewport-layout.v1.json` (ghi từ WPF thật, WP-10) với sai số **<= 0,5 DIP** cho mọi trường (cùng `Epsilon`
của `ViewportConvergence`). `ViewportController` (WP-16, `Shell.Win32.Viewing`) giữ offset, gọi engine, implement C-07.

### C-09 Bề mặt vẽ Direct2D

```csharp
namespace PhotoReview.Shell.Rendering;

public readonly record struct ColorF(float R, float G, float B, float A);
public enum ImageInterpolation { NearestNeighbor = 0, Linear = 1, HighQualityCubic = 2 }   // ScalingQuality.Linear->Linear, HighQuality->HighQualityCubic
public enum PresentResult { Presented = 0, Occluded = 1, DeviceRecreated = 2, Failed = 3 }

public interface IRenderSurface : IDisposable
{
    nint Hwnd { get; }
    int PixelWidth { get; } int PixelHeight { get; }
    double DpiScale { get; }                                 // GetDpiForWindow / 96
    nint FrameLatencyWaitHandle { get; }                     // cho message loop (C-05)
    bool IsWarp { get; }                                     // true khi rơi về WARP (RDP/không GPU) hoặc test
    void Resize(int pixelWidth, int pixelHeight, double dpiScale);   // ResizeBuffers; gọi trên UI thread
    IDrawContext BeginDraw();                                // DIP -> px qua SetDpi
    PresentResult EndDrawAndPresent();                       // Present(1, 0); D2DERR_RECREATE_TARGET / DXGI_ERROR_DEVICE_REMOVED -> tạo lại, trả DeviceRecreated
    event EventHandler? DeviceRecreated;                     // GpuImageCache phải xoá hết
    static abstract IRenderSurface CreateForWindow(nint hwnd, RenderSurfaceOptions options);
    static abstract IRenderSurface CreateOffscreen(int pixelWidth, int pixelHeight, double dpiScale); // WARP, cho test
}

public sealed record RenderSurfaceOptions(bool PreferWarp = false, bool AllowTearing = false, int MaxFrameLatency = 1);

public interface IDrawContext
{
    void Clear(ColorF color);
    void DrawImage(IGpuImage image, RectD destination, RectD? sourcePixels, ImageInterpolation interpolation, float opacity = 1f);
    void FillRectangle(RectD rect, ColorF color);
    void FillRoundedRectangle(RectD rect, double radius, ColorF color);
    void DrawRectangle(RectD rect, ColorF color, double strokeWidth);
    void DrawText(ITextLayout layout, PointD origin, ColorF color);
    void PushClip(RectD rect); void PopClip();
    void PushOpacity(float opacity); void PopOpacity();
    bool TryReadPixels(out PhotoReview.Imaging.Pixels.PixelBuffer pixels);   // chỉ offscreen/WARP; cho golden G-PIX
}
```

Ghi chú: `static abstract` trên interface cần C# 11 (có); nếu gây khó cho fake, WP-15 được đổi thành factory `RenderSurfaceFactory`
(đổi hợp đồng qua lead). Ảnh > 16384 px một chiều: `IGpuImage` chia tile nội bộ; `DrawImage` vẽ từng tile.

### C-10 Cache ảnh GPU

```csharp
namespace PhotoReview.Shell.Rendering;

public interface IGpuImage { long PixelBufferId { get; } int PixelWidth { get; } int PixelHeight { get; } long GpuBytes { get; } }

public interface IGpuImageCache
{
    /// UI thread. Upload ID2D1Bitmap1 từ PixelBuffer (CreateBitmap từ con trỏ, một lần copy CPU->GPU).
    IGpuImage GetOrUpload(PhotoReview.Imaging.Pixels.PixelBuffer pixels);
    /// Upload trước trong lúc rảnh (UiPriority.Background), bỏ qua nếu đã có. Dùng cho ảnh kế tiếp/trước theo hướng duyệt.
    void Prefetch(PhotoReview.Imaging.Pixels.PixelBuffer pixels);
    void Retain(IReadOnlyCollection<long> pixelBufferIds);   // giữ đúng các id này + LRU tới BudgetBytes
    long BudgetBytes { get; set; }                            // mặc định: 6 ảnh cỡ màn hình (NE: không nhân đôi RAM preload lên GPU)
    void Clear();
}
```

### C-11 Văn bản, overlay, animation

```csharp
namespace PhotoReview.Shell.Rendering;
public enum TextTrimming { None = 0, CharacterEllipsis = 1 }
public sealed record TextStyle(string FontFamily = "Segoe UI", double FontSizeDip = 12, bool Bold = false);
public interface ITextLayout : IDisposable { SizeD Size { get; } string Text { get; } }
public interface ITextRenderer   // DirectWrite; cache layout theo (text, style, maxWidth, trimming)
{
    ITextLayout CreateLayout(string text, TextStyle style, double maxWidth, TextTrimming trimming);
}

namespace PhotoReview.Shell.Win32.Overlay;
public readonly record struct OverlayLayoutContext(SizeD Client, double DpiScale, ITextRenderer Text);
public interface IOverlayElement
{
    string Id { get; }                    // khớp x:Name WPF: "ToolbarPanel", "StatusPanel", "FolderInfoPanel", "ZoomIndicatorPanel", "CapturePairBadge", "ComparePanel"
    bool IsVisible { get; }
    float Opacity { get; }
    RectD Bounds { get; }                 // sau Arrange
    void Arrange(in OverlayLayoutContext context);
    void Render(IDrawContext dc);
    bool HitTest(PointD point);           // overlay ẩn/opacity 0 => false (click-through như IsHitTestVisible=false)
}
public interface IAnimator   // tween opacity theo IFrameClock; FadeTo hủy tween cũ (như FadeTargetGate)
{
    void FadeTo(string elementId, float target, TimeSpan duration, Easing easing);
    bool IsAnimating { get; }
}
public enum Easing { Linear = 0, QuadraticOut = 1 }
```

### C-12 Mô hình menu

```csharp
namespace PhotoReview.App.Menus;   // App.Shared
public enum MenuItemKind { Command = 0, Toggle = 1, Submenu = 2, Separator = 3 }
public sealed record MenuItemModel(
    string Id,                         // tên ContextMenuItemId hoặc id con: "Zoom.Preset.150", "Zoom.Custom", "Zoom.AlsoSetClickLevel", ...
    MenuItemKind Kind, string Text, string? ShortcutText, bool IsEnabled, bool IsChecked,
    IReadOnlyList<MenuItemModel> Children, string? AutomationName);

public sealed record ContextMenuBuildContext(AppSettings Settings, bool HasImage, bool CanUndo, bool IsFit, double? EffectiveZoom,
    bool CanOpenInExternalEditor, bool CanCopyPath, bool IsCompareVisible);

public static class ContextMenuModelBuilder
{
    /// Dùng ContextMenuItems.Compute (Core) - cùng luật separator/ẩn hiện với MainWindow.ApplyContextMenuLayout + BuildZoomMenu.
    public static IReadOnlyList<MenuItemModel> Build(ContextMenuBuildContext context);
    public static IReadOnlyList<MenuItemModel> BuildToolsMenu(bool hasImages);   // Recovery, Diagnostics, Benchmark, ClearCache, RemoveNumbered, RemoveOriginal
}

namespace PhotoReview.Shell.Win32.Menus;
public interface IPopupMenuHost { string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip); } // modal, trả Id được chọn hoặc null
```

### C-13 Hộp thoại, clipboard, cửa sổ phụ

```csharp
// Giữ nguyên: PhotoReview.Core.Abstractions.IDialogService, PhotoReview.App.Coordinators.IFolderPicker.
namespace PhotoReview.App.Services;    // dời sang App.Shared
public interface IClipboardService { bool TrySetText(string text); }
public interface IZoomPromptService { int? PromptCustomZoom(int currentPercent); }    // thay new ClickZoomCustomDialog trong MainWindow

namespace PhotoReview.Shell.WpfBridge;  // UseWPF; chỉ Shell.Win32/Dialogs/WpfBridgeLoader.cs tham chiếu
public interface ISecondaryWindowHost
{
    bool ShowSettings(nint ownerHwnd, SettingsTarget target);
    void ShowRecovery(nint ownerHwnd);
    void ShowDiagnostics(nint ownerHwnd);
    void ShowBenchmark(nint ownerHwnd, string? folder);      // modeless như bản WPF
    void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries);
    bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items);
    int? PromptCustomZoom(nint ownerHwnd, int currentPercent);
}
public static class SecondaryWindowHostFactory { public static ISecondaryWindowHost Create(IServiceProvider services); }
```

Shell: `Win32DialogService : IDialogService` (WP-19a) làm native `ShowConfirmation/ShowMessage/ShowError` (`TaskDialogIndirect`,
comctl32 v6 qua manifest) và `PickFolder` (`IFileOpenDialog` + `FOS_PICKFOLDERS`), uỷ phần còn lại cho `ISecondaryWindowHost`
qua `Lazy<>` (WP-19b). `Win32ClipboardService` (`OpenClipboard/SetClipboardData(CF_UNICODETEXT)`, thử lại 5 x 20 ms như WPF).

### C-14 Placement và F11

```csharp
namespace PhotoReview.App.Windowing;  // App.Shared
public enum WindowShowState { Normal = 0, Minimized = 1, Maximized = 2 }

/// Lược đồ JSON y hệt window-placement.json hiện tại (IncludeFields, WriteIndented):
/// { "Length", "Flags", "ShowCommand", "MinPosition": {X,Y}, "MaxPosition": {X,Y}, "NormalPosition": {Left,Top,Right,Bottom} }
public sealed record WindowPlacementData(int Length, int Flags, int ShowCommand,
    int MinX, int MinY, int MaxX, int MaxY, int NormalLeft, int NormalTop, int NormalRight, int NormalBottom);

public interface IWindowPlacementStore
{
    Task<WindowPlacementData?> Prefetch(string placementPath);      // pool; kết quả dùng một lần
    WindowPlacementData? Read(string placementPath);                 // null nếu thiếu/hỏng/không thấy được (IsVisible < 80x80)
    void Save(string placementPath, WindowPlacementData data);       // WriteAtomically (tên tmp duy nhất)
    WindowPlacementData? TakePrefetched(string placementPath);
}

public static class WindowPlacementRules   // dời nguyên từ WindowPlacementService (thuần)
{
    public static int NormalizeShowCommand(int showCommand);
    public static int ResolveShowCommand(int showCommand, WindowShowState? fullscreenRestoreState);
    public static WindowShowState PlanStateBeforeShow(int savedShowCommand);
}

public interface IFullscreenController   // FullscreenWindowPlacer theo HWND
{
    WindowShowState StateBefore { get; }
    (int Left, int Top, int Right, int Bottom)? NormalBounds { get; }
    void Enter(nint hwnd, WindowShowState current);
    void Exit(nint hwnd);
}
```

### C-15 Cửa sổ shell

```csharp
namespace PhotoReview.Shell.Win32.Hosting;
public enum ShellCursor { Arrow = 0, SizeAll = 1, Wait = 2 }
public readonly record struct WindowMessage(nint Hwnd, uint Msg, nint WParam, nint LParam);
public interface IWindowMessageHandler { bool TryHandle(in WindowMessage message, out nint result); }  // chuỗi handler theo thứ tự đăng ký
public interface IShellWindow
{
    nint Hwnd { get; }
    double DpiScale { get; }
    SizeD ClientSizeDip { get; }
    bool IsActive { get; }
    bool IsLoaded { get; }                 // sau WM_CREATE + surface sẵn sàng, trước WM_DESTROY
    event EventHandler? ClientSizeChanged;
    event EventHandler? DpiChanged;        // WM_DPICHANGED (đã áp rect gợi ý)
    event EventHandler? Activated; event EventHandler? Deactivated;
    event EventHandler<CancelEventArgs>? Closing; event EventHandler? Closed;
    void AddMessageHandler(IWindowMessageHandler handler);
    void Invalidate();                     // yêu cầu vẽ khung kế tiếp (gộp)
    void SetTitle(string title);
    void SetCursor(ShellCursor cursor);
    void CapturePointer(); void ReleasePointer(); bool HasPointerCapture { get; }
    PointD ScreenToClientDip(PointD screenPixel);
    void Close();
}
```

### C-16 Composition dùng chung và startup shell

```csharp
namespace PhotoReview.App.Composition;   // App.Shared
public sealed record SharedServiceOptions(IPlatformImageCodec Codec, Func<IServiceProvider, IImageDecoder>? WpfFallbackDecoder,
    Func<IServiceProvider, IImageDecoder> ThumbnailDecoder);
public static class SharedServiceRegistration
{
    /// Mọi đăng ký hiện ở App.ConfigureServices KHÔNG phụ thuộc WPF (paths, file system, settings, session, journal, file actions,
    /// recycle bin, decoders, caches, preload, ViewModels, coordinators). App WPF và Shell gọi hàm này rồi thêm phần riêng.
    public static IServiceCollection AddPhotoReviewShared(this IServiceCollection services, SharedServiceOptions options);
}
public interface IPresentationSinkFactory { IPresentationSink Create(MainViewModelSinkCallbacks callbacks); } // WPF: WpfPresentationSink; Win32: Win32PresentationSink
public sealed record MainViewModelSinkCallbacks(Action<object?, bool> SetCurrentImage, Action<string> SetStatusText,
    Action ApplyInitialViewMode, Action<string> OnPresented);

namespace PhotoReview.Shell.Win32.Startup;
public sealed record ShellStartupContext(IReadOnlyList<string> Arguments, string? InitialPath, string? LaunchFolder,
    Task<AppSettings> Settings, Task<WindowPlacementData?> Placement, long ProcessStartTimestamp);
```

### C-17 Golden data

```csharp
namespace PhotoReview.TestSupport.Golden;   // TestSupport (net10.0), đọc/ghi bằng JsonSerializerContext
public sealed record GoldenViewportCase(string Name, ViewportInputDto Input, ViewportLayoutDto Expected);
public sealed record GoldenInputScript(string Name, GoldenSetup Setup, IReadOnlyList<GoldenInputStep> Steps, IReadOnlyList<GoldenCheckpoint> Expected);
public sealed record GoldenSetup(double ClientWidth, double ClientHeight, double DpiScale, int ImagePixelWidth, int ImagePixelHeight,
    string SettingsOverridesJson);       // phần config.json ghi đè (InitialViewMode, ClickZoomPercent, KineticPanEnabled, ...)
public sealed record GoldenInputStep(string Kind, double X, double Y, int Delta, string? Key, string? Modifiers, int TimestampMs, string? Command);
   // Kind: "wheel" | "hwheel" | "press" | "move" | "release" | "key" | "command" | "frame"(ms) | "resize"
public sealed record GoldenCheckpoint(int AfterStep, double Zoom, bool IsFit, double HorizontalOffset, double VerticalOffset,
    double ExtentWidth, double ExtentHeight, double ViewportWidth, double ViewportHeight, int DisplayZoomPercent);
public sealed record GoldenKeyName(string Name, int KeyValue, int VirtualKey);
public sealed record GoldenMenuCase(string Name, string SettingsOverridesJson, IReadOnlyList<string> VisibleIdsInOrder);
// *Dto: bản phẳng của ViewportInput/ViewportLayout (TestSupport không tham chiếu App.Shared)
```

### C-18 Mốc perf

Bản Win32 phát **cùng tên mốc** với bản WPF để dùng chung probe và so sánh: `appStartup`, `servicesBuilt`, `settingsFileLoaded`,
`languageLoaded`, `instanceLock`, `earlyDecodeStarted`, `mainWindowConstructed`, `placementRestoredBeforeShow`, `windowShown`,
`windowRevealed`, `Assign`, `Presented`, `RenderedFrame`. Thêm (chỉ shell): `d3dDeviceReady`, `swapChainReady`, `firstPresent`.
`RenderedFrame` của shell = `Present` của khung có ảnh đầu đã trả về (+ `DwmFlush` chỉ khi `PHOTOREVIEW_PERF_TRACE` bật).

```csharp
namespace PhotoReview.Core.Diagnostics;
public static class ShellPerfMarks { public const string D3dDeviceReady = "d3dDeviceReady"; public const string SwapChainReady = "swapChainReady"; public const string FirstPresent = "firstPresent"; }
```

## 6. Đợt và lịch song song

Chi tiết từng gói: [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md). Bảng dưới là lịch; "song song an toàn" = không chung file
(vùng xung đột ghi trong thẻ gói). Giờ = giờ-agent, dải lạc quan-thận trọng.

| Đợt | Gói (model, giờ) | Song song | Điểm hợp nhất (lead) | Toolchain |
|---|---|---|---|---|
| **0** khung + hợp đồng | WP-01 khung project + hợp đồng + test kiến trúc (opus, 8-12) | 1 gói | merge WP-01 = **đóng băng hợp đồng v1** | có sẵn |
| **1** seam trong mã dùng chung (app WPF giữ nguyên hành vi) | WP-02 PixelBuffer/codec/PixelOps (opus, 12-18) - WP-07 Input không-WPF (opus, 14-20) - WP-08 placement/F11 theo HWND (sonnet, 8-12) - WP-10 bộ ghi golden từ WPF (sonnet, 16-24) - WP-11 JSON source-gen (sonnet, 10-14) | 5 song song (không chung file) | I-1a sau WP-02: giao WP-03, WP-04, WP-05 (song song, 3 vùng file tách) | có sẵn |
| 1b | WP-03 WIC decode/thumbnail/EXIF (opus, 14-20) - WP-04 cache encode/decode WIC (opus, 14-20) - WP-05 TurboJpeg + LibRaw (sonnet, 8-12) | 3 song song | I-1b: WP-06 tách Imaging.Wpf + bỏ UseWPF (opus, 8-12) chạy sau cả 3; **perf gate đợt 1** | có sẵn |
| 1c | WP-09 tách App.Shared (sonnet, 12-18; sau WP-07, WP-08, WP-06) | 1 gói (chạm nhiều file) | I-1c: merge WP-09 -> hợp đồng v1.1 nếu có đổi namespace | có sẵn |
| **2** nền tảng shell | WP-12 WIC COM sinh mã (sonnet, 10-14) - WP-13a Interop Win32 (haiku, 6-10) - WP-13b Interop D3D/DXGI/D2D/DWrite (sonnet, 12-18) - WP-14 cửa sổ + message loop + dispatcher + frame clock (opus, 16-24) - WP-16 engine viewport + ViewportController (opus, 16-24) - WP-18 menu model + host native (sonnet, 10-14) - WP-19a dialog native + clipboard (sonnet, 8-12) | 7 song song (WP-13a/b trước WP-14/15 nếu cần interop; cho phép làm song song bằng cách WP-14/16 dùng fake trước) | I-2a: WP-15 renderer D2D (opus, 20-30, sau 13b) - WP-17 text/overlay toolkit (sonnet, 12-18, sau 13b + 15 hợp đồng) - WP-19b App.WpfWindows + WpfBridge (opus, 14-20) | có sẵn |
| **3** lắp tính năng | WP-20 composition root + startup (opus, 16-24) - WP-21 trình bày ảnh, compare, crossfade (opus, 16-24) - WP-22 chuột/phím/wheel/touchpad/drag-drop (sonnet, 12-18) - WP-23 overlay HUD/toolbar/status + auto-hide (sonnet, 14-20) - WP-24 menu chuột phải + zoom submenu + custom zoom (sonnet, 8-12) - WP-25 F11/đa màn hình/DPI/title (opus, 12-16) - WP-26 thanh cuộn (sonnet, 6-10, theo NE-3) | 6-7 song song sau WP-20 tạo `ShellMainWindow` khung (WP-20 giai đoạn A, 4-6 giờ, đi trước) | I-3: lead lắp `ShellMainWindow` + chạy golden + perf gate đợt 3 | có sẵn |
| **4** tương đương + phát hành song song | WP-27a/b test UI shell + port kịch bản (sonnet x2, 16-24 mỗi gói) - WP-28 golden pixel (sonnet, 10-14) - WP-29 perf host + probe (sonnet, 12-18) - WP-30 đóng gói 2 exe + CI + liên kết (sonnet, 8-12) - WP-31 docs/ADR 0010 (haiku, 4-6) | 5-6 song song | I-4: bản beta, người dùng dùng >= 2 tuần (P4) | có sẵn |
| **5** AOT + thay cửa sổ phụ | WP-32 spike S1 đo AOT (opus, 16-24, **cần MSVC + SDK**, chạy được bất cứ lúc nào sau khi cài) - WP-33 COM shell/Recycle Bin không late-bound (opus, 20-30) - WP-34 IsAotCompatible toàn bộ + trimming (sonnet, 10-16) - WP-35 cửa sổ phụ native/Avalonia theo NW-3 (kế hoạch riêng, 120-200) - WP-36 PublishAot + CI (sonnet, 8-12; bước publish cục bộ cần MSVC) | 3-4 song song | I-5: bản AOT | WP-32, WP-36 cần MSVC; phần còn lại có sẵn |
| **6** gỡ WPF | WP-37 xoá App WPF, Imaging.Wpf, WpfBridge, TestSupport.Windows; ADR (sonnet, 8-12) | 1 | I-6 | có sẵn |

Ghi chú lập lịch:

- **Đường găng:** WP-01 -> WP-02 -> (WP-03/04/05) -> WP-06 -> WP-09 -> WP-20 -> (đợt 3) -> WP-27. WP-07, WP-10, WP-13..19 không nằm
  trên đường găng nên làm sớm để giảm rủi ro.
- WP-14 và WP-16 không cần WP-09: chúng dùng hợp đồng (C-04, C-05, C-07, C-08) đã có từ WP-01; nếu WP-09 chưa merge, chúng tham chiếu
  App.Shared ở trạng thái khung (WP-01 tạo project rỗng chứa các file hợp đồng).
- Mỗi điểm hợp nhất: lead rebuild `PhotoReview.slnx -c Release` (0 cảnh báo), chạy `tools/verify-all.ps1 -Hidden`, golden, perf gate
  của đợt, rồi mới giao đợt kế tiếp.

## 7. Chạy song song hai bản, chuyển tính năng, tương đương hành vi, perf, rollback

### 7.1 Hai bản cùng tồn tại

| Mặt | Bản WPF (`PhotoReview.App.exe`) | Bản Win32 (`PhotoReview.exe`) |
|---|---|---|
| Thư mục phát hành | cùng một thư mục publish (WP-30), dùng chung `Languages\`, `libraw.dll`, `turbojpeg.dll` | cùng |
| Dữ liệu | `%LOCALAPPDATA%\PhotoReview\...` | **cùng** (N-3) |
| Single-instance | prefix `PhotoReview` | prefix `PhotoReviewShell` trong beta (NE-6) -> hai bản không forward nhầm cho nhau |
| Journal | dùng chung, an toàn đa tiến trình (Q-R27 marker, P02) | cùng |
| Liên kết `.jpg` + "Browse with PhotoReview" | `deploy/install-photo-review-association.ps1` | thêm tham số `-Exe` (WP-30); nút "Cập nhật đường dẫn" trong Settings ghi exe đang chạy - **người dùng chọn** bản nào |
| Cờ chọn | không cờ trong mã; chọn bằng exe | `PHOTOREVIEW_SHELL_*` chỉ cho chẩn đoán (vd. `PHOTOREVIEW_SHELL_WARP=1`) |
| Mở lại bằng bản cũ | - | lệnh "Mở bằng trình xem cũ" (menu Tools, chỉ trong beta): chạy `PhotoReview.App.exe "<file hiện tại>"` rồi đóng shell - lối thoát khi tính năng chưa port |

### 7.2 Chuyển từng tính năng

Bản Win32 không bật một tính năng nhìn thấy cho tới khi gói của nó đạt tiêu chí 7.3; trong lúc đó mục menu/phím tương ứng **ẩn**
(không lỗi). Bảng ánh xạ tính năng -> gói -> test tương đương:

| Tính năng (bản WPF) | Gói | Tương đương bằng |
|---|---|---|
| Ảnh đầu, cloak tới khung đầu, placement trước khi hiện, decode sớm | WP-20 | `ShellStartupFirstShowTests` (port `StartupFirstShowIntegrationTests`, `StartupFirstShowTests`), probe 7.4 |
| Fit / FitWidth 1-2 / FitHeight / 100 % / click-zoom / zoom bước / KeepZoom / InitialViewMode | WP-16, WP-22 | G-VIEW, G-INPUT; port `MainWindowBehaviorTests.Fit`, `KeyboardZoomFrameTests`, `KeepZoomAcrossImagesIntegrationTests`, `FitViewControllerTests` (chạy nguyên trên `ViewportController`) |
| Pan kéo, kinetic (Q-R40), mũi tên, glide theo vblank | WP-22 | G-INPUT (bước "frame" thời gian cố định); `KineticPanTests` không đổi (logic thuần); port `KineticPanFrameMeasurementTests` |
| Con lăn, Ctrl+wheel, wheel ngang, touchpad 2 ngón, chuột giữa | WP-22 | G-INPUT; `TouchpadGesturesTests`, `PointerInputControllerTests.Touchpad` (dùng chung) |
| Zoom chi tiết (original on demand, RAW swap) | WP-21 | port `MainWindowZoomDetailTests`; `ZoomDetailTests` dùng chung |
| Crossfade (Q-R37) | WP-21 | port `ImageCrossfadeIntegrationTests` (đếm khung, opacity theo thời gian giả) |
| Compare | WP-21 | `CompareViewModelTests` dùng chung + test chọn trái/phải bằng click/Enter/Space |
| Overlay: status, EXIF, folder info, zoom HUD, capture badge, skipped warning; auto-hide | WP-23 | `InfoOverlayZoomIndicatorTests` (chung), test bố cục overlay (vị trí góc, margin 8, padding 6,4) + G-PIX |
| Toolbar + Tools menu | WP-23 | test hit-test + lệnh |
| Menu chuột phải + tuỳ biến + zoom submenu + custom zoom | WP-18, WP-24 | G-MENU (thứ tự id nhìn thấy cho 12 cấu hình), port `ContextMenuRedesignTests`, `ZoomContextMenuTests`, `ClickZoomCustomDialogTests` |
| Phím tắt + action profiles | WP-07, WP-22 | `KeyIdMapping` = WPF cho mọi giá trị `Key` (G-KEY); `ShortcutRouter` test dùng chung |
| F11, đa màn hình, DPI đổi, title tối, nhãn instance | WP-25 | port `FullscreenTransitionFrameTests`, `FullscreenWindowPlacerExitTests`, `WindowPlacementIsolationTests`; `tools/diag/fullscreen-capture.ps1` |
| Kéo-thả, clipboard, mở editor ngoài | WP-22, WP-19a | test `IDropTarget` với `IDataObject` giả; clipboard round-trip |
| Single-instance forward | WP-20 | port `AppForwardedOpenTests`, `InstanceForwardPipeTests` (đã ở Platform, dùng chung) |
| Cửa sổ phụ (Settings, Recovery, ...) | WP-19b | test mở/đóng qua bridge trên desktop ẩn; `SettingsWindowContextMenuTests` giữ ở bản WPF |
| Recovery khi khởi động, file action, undo | WP-20 (thứ tự), còn lại dùng chung | `ShellStartupOrderTests`; fault-injection Core không đổi |

### 7.3 Tiêu chí tương đương hành vi

1. **Golden logic (tự động, chặn merge):** bộ ghi WP-10 chạy **bản WPF thật** (desktop ẩn, `Category=UI`) và ghi:
   - **G-VIEW** `viewport-layout.v1.json`: lưới >= 600 ca = client {800x600, 1280x720, 1920x1080, 1366x768 trừ thanh tác vụ, 3840x2160} x DPI
     {1.0, 1.25, 1.5, 2.0} x ảnh {6000x4000, 4000x6000, 800x600, 12000x1000 panorama, 1000x12000} x chế độ {Fit, 100 %, 200 %, FitWidth,
     FitHeight, zoom bước 25 %..400 %}; ghi ViewportWidth/Height, ExtentWidth/Height, thanh cuộn nhìn thấy, gốc ảnh (TranslatePoint),
     offset tối đa. Sai số chấp nhận 0,5 DIP.
   - **G-INPUT** `input-scripts.v1.json`: >= 60 kịch bản (wheel tại 5 điểm, Ctrl+wheel, FitWidth anchor Centre/TopThird/BottomThird,
     click-zoom bật/tắt, double-click Fit, kéo pan + thả kinetic với dấu thời gian cố định, mũi tên khi zoom/ở mép, touchpad pan/swipe,
     chuột giữa mỗi `MiddleClickAction`, KeepZoom qua 3 ảnh, swap RAW size, DPI đổi giữa chừng, resize khi FitWidth). Checkpoint sau
     từng bước: Zoom, IsFit, offset, extent, viewport, DisplayZoomPercent. Sai số 0,5 DIP / 0,001 zoom.
   - **G-KEY** `key-names.v1.json`: mọi tên `System.Windows.Input.Key` + giá trị + `KeyInterop.VirtualKeyFromKey`.
   - **G-MENU** `context-menu.v1.json`: 12 cấu hình settings (mặc định, ẩn từng nhóm, editor ngoài, Fit/zoom, không ảnh) -> danh sách id
     nhìn thấy theo thứ tự kể cả separator.
   Bản Win32 chạy cùng kịch bản trên `ViewportController` + `PointerInputController` thật (không cần cửa sổ, `Shell.Tests`) và phải khớp.
2. **Golden pixel (tự động, cảnh báo ở đợt 3, chặn ở đợt 4):** G-PIX (WP-28) render cùng ảnh + layout bằng WPF `RenderTargetBitmap` và
   bằng `IRenderSurface.CreateOffscreen` (WARP); so trên vùng ảnh: lệch hình học <= 1 px (so mép ảnh), PSNR >= 32 dB ở Fit, >= 28 dB ở zoom
   khác 100 % (bộ lọc khác nhau: WPF Fant/linear vs D2D cubic/linear - NE-9), **giống hệt** ở 100 % (NearestNeighbor không áp dụng, 1:1).
   Overlay: so hộp bao của từng panel (vị trí, kích thước +/- 2 px), không so pixel chữ (ClearType khác).
3. **Kịch bản thủ công cho người dùng (cổng đợt 3 và beta):** danh sách ở thẻ WP-27b - 25 bước, mỗi bước "làm gì / mong đợi / so với bản
   cũ", gồm: mở ảnh từ Explorer (không khung trắng), mở thư mục lớn F4, Next/Prev giữ phím, zoom/pan/kinetic chuột và touchpad, FitWidth 2
   bằng chuột giữa, F11 trên màn hình 2, rút màn hình 2 khi đang mở, đổi DPI, menu chuột phải tối, Settings mở và lưu, Recovery, Delete +
   Undo (thùng rác thật - **người dùng tự làm**, agent không chạy), RAW zoom, crossfade bật, dark title bar, nhãn instance.

### 7.4 Perf gate theo đợt (đo trên máy này, lô xen kẽ >= 8 vòng + 1 vòng bỏ, như mục 2 của kế hoạch cấp cao)

| Đợt | Chỉ số | Ngưỡng |
|---|---|---|
| 1 | Bản WPF: `tools/diag/tune-matrix.ps1` S2/S3/S4 (Repeat >= 8) | P50 và P95 key->present không tệ hơn baseline quá max(5 %, 1 ms) và ngoài dải A/A |
| 1 | Bản WPF: ảnh đầu (probe), publish R2R | median không tệ hơn trước đợt quá 20 ms |
| 1 | Peak working set F4 (preload 32/8) | <= +5 % |
| 1 | Decode bench (`Benchmark.Cli --benchmark-all`, WIC Direct, 24 MP -> FHD) | P50 không tệ hơn 5 %; số lần copy pixel đo bằng `ReviewMetrics` không tăng |
| 2 | Shell khung (cửa sổ + D2D + ảnh tĩnh), R2R | ảnh đầu median <= 250 ms; pan 100 %: khung > 16,7 ms <= 1 % |
| 3 | Shell đầy đủ, publish R2R | ảnh đầu median <= 450 ms (mục tiêu NW-5 a); cửa sổ thấy được <= 150 ms |
| 3 | Shell S2/S3/S4 (host WP-29) | P50/P95 key->present <= bản WPF cùng lô; P95 trong cache <= 3 ms (G0.5) |
| 3 | RAM | peak WS <= bản WPF + 5 %; GPU dedicated (Task Manager / `D3DKMTQueryStatistics`) <= `GpuImageCache.BudgetBytes` + 64 MB |
| 4 | Như đợt 3 trên bản phát hành + 2 tuần beta không lỗi dữ liệu | - |
| 5 | AOT | ảnh đầu median <= 350 ms; G0.3 (AOT nhanh hơn R2R >= 40 ms) quyết có giữ AOT |

Không so số qua ranh giới AR02c (quy tắc cũ); mỗi lô ghi CPU nền; kết quả mỗi gate vào một file mới `perf/` tên `<ngày>-nowpf-wave<N>`.

### 7.5 Rollback

- Đợt 1-2: mỗi PR revert được độc lập (`git revert`); app WPF là bản duy nhất người dùng chạy.
- Đợt 3-4: shell chưa phải mặc định; rollback = trỏ liên kết Explorer về `PhotoReview.App.exe` (script WP-30 `-Exe`), không đụng dữ liệu.
- Sau beta: giữ bản WPF trong thư mục phát hành >= 2 bản phát hành (P6 điều kiện).
- Nếu gate đợt 3 trượt (ảnh đầu > 450 ms sau 2 vòng tối ưu): dừng ở đợt 3, báo người dùng với số đo (NW-5), không xoá gì.

## 8. Toolchain

| Thứ | Hiện có trên máy | Cần cho | Đợt |
|---|---|---|---|
| .NET SDK 10.0.401, runtime 10.0.12 | có | mọi thứ | 0-6 |
| crossgen2 (R2R), `Microsoft.DotNet.ILCompiler` 10.0.12 | có (cache NuGet) | publish R2R; analyzer AOT (`IsAotCompatible`) chạy **không cần linker** | 0-5 |
| COM sinh mã `[GeneratedComInterface]`, `LibraryImport` | có (SDK) | toàn bộ interop shell | 2+ |
| Direct2D/DXGI/D3D11/DirectWrite/WIC runtime | có (Windows 11) | shell, WARP cho test | 2+ |
| Hidden desktop runner `tools/run-tests-hidden.ps1` | có | test UI cả hai bản | 1+ |
| **MSVC v14x x64 build tools** (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`) qua Visual Studio Installer có sẵn | **không** | `link.exe` cho Native AOT | 5 (WP-32, WP-36 publish cục bộ) |
| **Windows 11 SDK 10.0.26100** (`Microsoft.VisualStudio.Component.Windows11SDK.26100`) | **không** | `kernel32.lib`... cho link AOT | 5 |
| (Tuỳ chọn) Windows optional feature "Graphics Tools" (D3D debug layer) | không | debug D2D/D3D (`D3D11_CREATE_DEVICE_DEBUG`) khi điều tra; **không bắt buộc** | 2-3 |

Dung lượng ước: MSVC x64 ~2-3 GB + SDK ~1,5-2,5 GB (ổ C: còn ~14 GB); Graphics Tools ~0,1-0,3 GB (ước). Nguồn: Microsoft (VS Installer
`C:\Program Files (x86)\Microsoft Visual Studio\Installer`, Settings > Optional features). **Agent không cài**; người dùng cài (NW-2).
CI `windows-latest` đã có MSVC, nên WP-36 có thể publish AOT trên CI ngay cả khi máy chưa cài. **Kế hoạch không phụ thuộc AOT cho
đợt 0-4:** mọi gate đợt 0-4 đo bản R2R; mã mới AOT-ready nhờ analyzer (N-11) nên đợt 5 không phải sửa lại.

## 9. Rủi ro và quyết định cần chủ dự án chốt

### 9.1 Rủi ro

| Rủi ro | Mức | Giảm thiểu | Gói |
|---|---|---|---|
| Engine viewport lệch WPF (thanh cuộn Auto, căn giữa, làm tròn layout `UseLayoutRounding`) -> Fit/zoom khác | Cao | G-VIEW ghi từ WPF thật trước khi viết engine; sai số 0,5 DIP; WP-16 là opus | WP-10, WP-16 |
| Hồi quy kinetic/touchpad (đã tinh chỉnh nhiều vòng) | Cao | logic thuần dùng chung; chỉ adapter đổi; G-INPUT có bước frame | WP-22 |
| Thêm một lần copy pixel hoặc rò bộ nhớ native khi đổi `BitmapSource` -> `PixelBuffer` | TB | đếm copy trong `ReviewMetrics`; test đếm buffer sống (`PixelBuffer.LiveCount` chỉ Debug); perf gate đợt 1 | WP-02..06 |
| Upload GPU làm chậm key->present (8 MB/ảnh FHD) | TB | `IGpuImageCache.Prefetch` ảnh kế tiếp ở Background; đo P95 <= 3 ms | WP-15, WP-21 |
| WPF trong cùng tiến trình với message loop Win32: phím trong cửa sổ modeless (Benchmark), focus, DPI | TB | modal dùng vòng lặp riêng của WPF; modeless hook `ComponentDispatcher.RaiseThreadMessage` trong loop; test desktop ẩn | WP-19b |
| Định dạng cache đổi do encoder WIC khác `JpegBitmapEncoder` | TB | test chéo hai chiều file do bản cũ/mới ghi; header không đổi | WP-04 |
| Dark mode menu native không chính thức (uxtheme ordinal 135/133) | TB | NE-4; dự phòng menu sáng; hoặc owner-draw | WP-18 |
| Bảo trì hai UI | Cao | NE-7 đóng băng tính năng UI WPF từ đợt 3 | lead |
| Agent song song sửa trùng file | TB | vùng xung đột ở mỗi thẻ; lead giao theo lịch mục 6 | lead |
| Ảnh > 16384 px | TB | tile trong `IGpuImage`; golden pixel panorama 30000x2000 | WP-15 |
| Máy không GPU/RDP, device lost | TB | WARP; test `DeviceRecreated` bằng `D2DERR_RECREATE_TARGET` giả | WP-15 |
| Ổ C: đầy khi cài toolchain | TB | chỉ đợt 5; dọn `work/diag/tune-runs` trước | WP-32 |

### 9.2 Quyết định cần chốt (theo quy tắc "Asking the user to decide")

**NE-1 - Bắt đầu đợt 1 mà không chờ spike S1 (Native AOT)?**
Hiện trạng: S1 cần MSVC + SDK chưa cài (NW-2 chưa chốt). Sàn R2R đã đo (182 ms) đủ chứng minh bản lai nhanh hơn hiện tại ~600 ms.
- (a) **Bắt đầu đợt 0-4 ngay (R2R), S1 làm khi nào cài toolchain; S1 chỉ quyết đợt 5.** Ưu: không chờ; mọi công việc đợt 1-4 cần cho cả AOT
  lẫn R2R; không rủi ro dữ liệu. Nhược: nếu cuối cùng AOT không đáng, vẫn đã chọn đúng (bản lai là mục tiêu NW-5 a). Phải test: perf gate đợt 1-3.
- (b) Chờ S1 rồi mới làm. Ưu: số AOT có trước. Nhược: chặn mọi thứ bởi việc cài 4-5 GB; S1 không đổi quyết định đợt 1-4.
- **Khuyến nghị (a).** Sau khi chọn: giao WP-01, rồi đợt 1 theo mục 6.

**NE-2 - Tên project/exe.**
- (a) **`PhotoReview.Shell.*`, exe `PhotoReview.exe`, bản WPF giữ `PhotoReview.App.exe`.** Ưu: tên exe ngắn cho bản tương lai; khớp kế hoạch P4.
  Nhược: hai exe cùng tiền tố dễ nhầm trong beta (nhãn title "beta" giải quyết).
- (b) `PhotoReview.Win32.exe`. Ưu: rõ ràng trong beta. Nhược: phải đổi tên lần nữa ở P6 (liên kết Explorer phải cập nhật).
- **Khuyến nghị (a).**

**NE-3 - Thanh cuộn ở bản Win32.**
Hiện trạng: WPF `ScrollViewer` Auto, thanh cuộn tối (`DarkScrollBars.xaml`) **chiếm chỗ**, làm FitWidth phải sửa một lần (`CorrectForSideScrollbarAsync`).
- (a) **Thanh cuộn tự vẽ chiếm chỗ, giống WPF** (`ScrollBarPolicy.Auto`). Ưu: tương đương tuyệt đối, golden G-VIEW dùng nguyên; FitWidth/T89 không đổi.
  Nhược: ~6-10 giờ thêm (kéo thumb, click track); vẫn giữ "mẹo" sửa thanh cuộn bên.
- (b) Thanh cuộn mỏng nổi trên ảnh (không chiếm chỗ). Ưu: hiện đại, ảnh rộng hơn ~17 DIP, bỏ được `CorrectForSideScrollbarAsync`. Nhược: **đổi hành vi
  nhìn thấy** (vị trí FitWidth lệch ~17 DIP so bản cũ), golden phải ghi lại với chính sách khác, người dùng phải xem.
- (c) Không thanh cuộn, chỉ chỉ báo vị trí khi pan. Ưu: ít công nhất. Nhược: mất thao tác kéo thanh cuộn; đổi hành vi.
- **Khuyến nghị (a)** cho beta (tương đương trước), cân nhắc (b) sau P6 như một tính năng.

**NE-4 - Menu chuột phải.**
- (a) **Menu native `TrackPopupMenuEx` + dark mode qua uxtheme (ordinal không công bố, như Explorer/Notepad++), rơi về menu sáng nếu thiếu.**
  Ưu: bàn phím, accessibility, submenu, đa màn hình miễn phí; ít mã. Nhược: API không chính thức; màu không khớp hẳn `DarkMenus.xaml`.
- (b) Menu tự vẽ (cửa sổ popup + D2D). Ưu: giống hệt theme. Nhược: 2-3 ngày thêm, tự làm bàn phím/accessibility/submenu.
- **Khuyến nghị (a).**

**NE-5 - Cửa sổ phụ (Settings, Recovery, ...) trong bản lai.**
- (a) **Cùng tiến trình, nạp WPF muộn:** dời các cửa sổ phụ sang library `PhotoReview.App.WpfWindows` (dời file, không viết lại), shell gọi qua
  `Shell.WpfBridge` khi người dùng mở. Ưu: owner/modal đúng, settings áp ngay (cùng `SettingsStore`), ~300-400 ms chỉ lúc mở lần đầu. Nhược: dời file
  chạm app WPF (thuần cơ học); cần xử lý message loop lồng cho cửa sổ modeless.
- (b) Cùng tiến trình nhưng tham chiếu thẳng `PhotoReview.App` (exe). Ưu: không dời file. Nhược: kéo cả App WPF (DI, `App` class) vào shell; dễ nạp
  WPF sớm ngoài ý muốn.
- (c) Tiến trình riêng (`PhotoReview.App.exe --settings --owner <hwnd>`). Ưu: tách hẳn. Nhược: owner/modal giả lập, đồng bộ settings qua file, chậm
  hơn, khó test; hai tiến trình cùng ghi config.
- **Khuyến nghị (a).**

**NE-6 - Single-instance trong beta.**
- (a) **Prefix riêng `PhotoReviewShell`.** Ưu: hai bản không forward nhầm (giao thức có thể lệch phiên bản, SEC-02). Nhược: có thể mở cả hai bản cùng
  thư mục (journal vẫn an toàn đa tiến trình).
- (b) Dùng chung. Ưu: một cửa sổ duy nhất. Nhược: bản cũ nhận forward từ bản mới và ngược lại - khó đoán, rủi ro giao thức.
- **Khuyến nghị (a)**; hợp nhất ở P6.

**NE-7 - Đóng băng tính năng UI trên bản WPF từ đầu đợt 3.**
- (a) **Có: chỉ sửa lỗi; tính năng UI mới làm trên shell.** Ưu: không làm hai lần. Nhược: yêu cầu mới phải chờ shell.
- (b) Không: tiếp tục cả hai. Ưu: người dùng không chờ. Nhược: mỗi tính năng tốn ~2x, golden phải ghi lại.
- **Khuyến nghị (a).**

**NE-8 - Fallback decoder ở bản Win32 (= NW-4).**
- (a) WIC thứ hai (`WICConvertBitmapSource` + decoder mặc định). Ưu: không thêm phụ thuộc. Nhược: WPF cũng là WIC, nên lỗi WIC Direct thường lỗi luôn ở đây.
- (b) **TurboJpeg cho JPEG, WIC thứ hai cho định dạng khác.** Ưu: decoder độc lập thật cho JPEG (phần lớn ảnh). Nhược: thêm đường fallback cần test
  (`DecoderQualityGateTests`, fuzz).
- **Khuyến nghị (b)** (như NW-4). Bản WPF giữ fallback WPF.

**NE-9 - Chất lượng scale khi zoom khác 100 %.**
Hiện trạng: WPF `HighQuality` = Fant khi thu nhỏ; D2D có `HIGH_QUALITY_CUBIC`. Ở Fit, preview đã decode đúng hộp viewport nên gần 1:1 (khác biệt nhỏ).
- (a) **Chấp nhận D2D cubic/linear; G-PIX theo PSNR + người dùng xem bằng mắt.** Ưu: GPU, nhanh. Nhược: không giống bit-by-bit.
- (b) Thu nhỏ trên CPU bằng `IWICBitmapScaler` (Fant) mỗi lần đổi zoom < 100 %. Ưu: gần WPF hơn. Nhược: tốn CPU/ms mỗi bước zoom, thêm cache.
- **Khuyến nghị (a).**

Còn mở từ trước, không lặp lại ở đây: NW-2 (cài toolchain), NW-3 (cửa sổ phụ khi AOT), NW-5 (điều kiện dừng),
P1-STARTUP-IN-WPF (A/B/C/D). Kế hoạch này giả định NW-1 = (a) đã làm (P-1 xong ở #396/#397).

**Sau khi người dùng chốt:** lead ghi kết quả vào `summary` của file này, chạy `tools/generate-open-decisions.ps1`, giao WP-01, rồi các gói
đợt 1 theo prompt mẫu (file WP).

## 10. Điểm chưa chắc (ghi để agent kiểm, không giả định)

1. **Hành vi layout WPF chính xác** (làm tròn layout, `UseLayoutRounding` của MainWindow, độ dày thanh cuộn trong `DarkScrollBars.xaml`, viền
   `Border Canvas`) chưa đọc hết; C-08 mô tả theo XAML đã đọc, **G-VIEW là chuẩn**, không phải mô tả.
2. **DPI của bitmap** (`BitmapSource.Width` theo DpiX của file) có thể làm `SourceSize`/`ImageActualWidth` của WPF khác pixel/96; cần ca
   golden với JPEG 72/300 DPI (C-07 ghi chú).
3. App không có `app.manifest`; mức DPI awareness thực tế của bản WPF (PerMonitorV2 qua mặc định WPF .NET?) cần đo
   (`GetThreadDpiAwarenessContext`) trước khi WP-25 khẳng định "giống".
4. Chi phí upload GPU mỗi ảnh và hiệu quả `Prefetch` chưa đo; ngưỡng P95 <= 3 ms có thể cần giữ 2-3 ảnh lân cận trên GPU.
5. `static abstract` trong C-09 có thể bất tiện cho fake; cho phép WP-15 đề xuất factory (qua lead).
6. Số liệu giờ-agent là ước theo kích thước file (mục 4, kiểm kê) và kinh nghiệm các PR trước; chưa có số đo năng suất agent trên Direct2D.
7. Hành vi WPF lồng trong message loop Win32 với cửa sổ modeless (Benchmark) chưa thử; WP-19b phải làm spike nhỏ trước (4 giờ đầu của gói).
8. `GetCurrentInputMessageSource` trong WndProc thuần Win32 trả cùng kết quả như trong hook WPF - hợp lý nhưng chưa kiểm.
