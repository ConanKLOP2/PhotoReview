---
id: NOWPF-WP09
order: 630
summary: |-
  WP-09 (đợt 2, 2026-10-11): dời phần không-WPF của PhotoReview.App sang PhotoReview.App.Shared bằng git mv (63 rename, namespace giữ nguyên): ViewModels, Coordinators (trừ FullscreenWindowPlacer), Input, Composition/{MainViewModelCompositionRoot,ServiceFactories,DecoderProviders,LibRawPreviewFallback}, Services thuần, Localization (trừ TrExtension), AppLog, BuildInfo, FileHashService... C-16 thực thi: SharedServiceRegistration.AddPhotoReviewShared + IPresentationSinkFactory (WpfPresentationSinkFactory); IClipboardService tiêm qua DI (hết new WpfClipboardService trong lõi). Rule 6 (K-2) thay bằng luật theo assembly L-SHARED. Hợp đồng v1.2 (chỉ thêm IClipboardService vào C-13). Hành vi app không đổi.
---

# NOWPF-WP09 - tách PhotoReview.App.Shared (dời file) (2026-10-11)

Thẻ: [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md) mục WP-09. Tiền đề đã merge: WP-06, WP-07, WP-08. Số đo: [perf/2026-10-11-wp09-app-shared.md](../perf/2026-10-11-wp09-app-shared.md).

## 1. Đã làm

- **Dời (git mv, 63 rename, 57 file >= 97 %)** sang `src/PhotoReview.App.Shared` (namespace `PhotoReview.App.*` giữ nguyên, nên không đổi using ở App/test/Benchmark.Cli):
  `ViewModels/*`, `Coordinators/*` (trừ `FullscreenWindowPlacer` - Win32 trên `Window` WPF), `Input/*`, `Composition/{MainViewModelCompositionRoot,ServiceFactories,DecoderProviders,LibRawPreviewFallback}`,
  `Services/{ViewportSizeSource,PreviewStateContext,IPresentationObserver,InitialImagePrewarm,InitialViewportPredictor,RenderFrameTrace,IClipboardService,ShortcutKeyName,WpfKeyNameValidator}`,
  `Localization/{LocalizationService,LanguageOptions,TranslationExport,TranslationProblems,LocalizationSource}`, `AppLog`, `TaskLogging`, `LaunchArguments`, `BuildInfo`, `FileHashService`, `MainWindowHelpers`, `MainWindowConvergence`.
  Ở lại App (WPF thật): `AppHost` (gọi `App.ConfigureServices`), `TrExtension`, Converters, `WpfDialogService`/`WpfFolderPicker`/`WpfClipboardService`/`WpfPresentationSink`/`WpfImageSurface`/`WpfInputAdapters`, `DispatcherUiScheduler`, `StartupWarmup`, `StartupWindowReveal`, `RecoveryPresenter`, `WindowPlacementService`, mọi cửa sổ.
- **C-16 thực thi:** `SharedServiceRegistration.AddPhotoReviewShared(SharedServiceOptions)` (file mới `Composition/SharedServiceRegistration.cs`) = toàn bộ đăng ký không-WPF cũ của `App.ConfigureServices` (mục 1-7), codec/decoder fallback lấy từ options.
  `App.ConfigureServices` chỉ còn: host services WPF (`IUiScheduler`, `IDialogService`, `IFolderPicker`, `IClipboardService`, `IPresentationSinkFactory`) + `AddPhotoReviewShared(WpfBitmapSourceCodec, WpfBitmapImageDecoder)` + `MainWindow`.
  Dịch vụ do host cấp mà thiếu thì `GetRequiredService` ném lỗi to (không có mặc định ẩn).
- **`IPresentationSinkFactory`:** `MainViewModelCompositionRoot` dựng sink qua factory của host (`WpfPresentationSinkFactory` giữ `ReviewMetrics`); không còn `new WpfPresentationSink` trong lõi.
- **Clipboard:** `MainViewModel(..., IClipboardService? clipboard = null)`; composition root dùng `GetRequiredService<IClipboardService>()`; không tiêm (unit test) = `UnavailableClipboardService` (TrySetText = false). `Win32ClipboardService` (Shell.Win32) khai báo `: IClipboardService`.
- **Seam mới ở lõi:** `DecoderProviders.Create(sourceReader, codec, wpfDecoder?, isLibRawNeeded)` (không còn WPF cứng; `wpfDecoder = null` = không liệt kê provider Wpf, factory ánh xạ "Wpf" sang WicDirect - đường NE-8),
  `ServiceFactories.CreateWebpHeicDecoder(sourceReader, codec, wpfFallback?, ...)` (fallback null = WicDirect đứng một mình). Ghi log lỗi bắt buộc: `AppLog.ErrorForced`/`AppLog.WriteForced` (chuyển từ `App.xaml.cs`; `App.LogStartupErrorForced`/`App.WriteForced` còn, uỷ quyền).
- **L-AOT:** `InitialViewportPredictor` (7 P/Invoke) và `WheelMessageSource` (1) đổi `DllImport` -> `LibraryImport` (cùng entry point; `GetMonitorInfoW` tường minh) vì App.Shared là assembly L-AOT.
- **Luật kiến trúc:** Rule 6 (K-2) thay bằng "Rule 6 -> L-SHARED" (ViewModels/Input/Coordinators/Composition chỉ ở App.Shared, trừ `AppHost` và `FullscreenWindowPlacer`) + L-SHARED theo assembly (đã có từ WP-01) + mới "csproj App.Shared không UseWPF/WinForms, không tham chiếu App/Shell.*/Imaging.Wpf". ST03/ST06 trỏ tới assembly mới. L-AFFINITY đã quét App.Shared từ WP-01.
- **Hợp đồng v1.2:** namespace không đổi nên chữ ký C-xx giữ nguyên; duy nhất thêm `IClipboardService` (C-13, dời, trước chờ WP-09) vào `ContractSurfaceTests` và tái sinh `Approved/contracts.v1.txt` bằng `PHOTOREVIEW_REGENERATE_CONTRACTS=1` (lead duyệt trong PR này).
- **Phát hành:** `tools/verify-release.ps1` và hai danh sách file trong `ToolScriptTests` thêm `PhotoReview.App.Shared.dll` (assembly đã được app nạp từ WP-01 nhưng chưa được kiểm).
- **Test:** `BuildInfo*Tests` đổi `typeof(BuildInfo).Assembly` -> assembly App (BuildTime chỉ đóng dấu ở App); 5 chỗ gọi `DecoderProviders.Create` thêm codec/wpf decoder; mới `SharedServiceRegistrationTests` (12), `CopyPath` (clipboard không tiêm), Shell.Tests (Win32ClipboardService là IClipboardService), L-SHARED csproj.

## 2. Lệch / để lead biết

1. `SharedServiceOptions.ThumbnailDecoder` (khoá ở v1) KHÔNG dùng được: từ WP-06 `ThumbnailCache` nhận codec, không nhận decoder. Chỉ kiểm không null; WPF truyền decoder WPF. Đề xuất bỏ trường này ở hợp đồng v1.3 (không đổi trong gói này).
2. `AppHost` giữ ở App (gọi `App.ConfigureServices`); thẻ không liệt kê. Shell Win32 sẽ có entry riêng ở WP-20.
3. `WpfKeyNameValidator` dời nguyên tên (thực chất thuần: `ShortcutKeyName` + `KeyId`); đổi tên chỉ là việc dọn, không làm ở đây.
4. Thẻ ghi "sửa `AssemblyInfo.cs` (InternalsVisibleTo)": App.Shared đã có IVT đủ từ WP-01 (App, Shell, các test, Benchmark.Cli) nên không cần sửa.
5. Chưa dời (vẫn WPF hoặc ngoài thẻ): `RecoveryPresenter` (System.Windows.Media), `WindowPlacementService`, `Converters`, cửa sổ phụ -> WP-19b/WP-23+.

## 3. Kiểm chứng

- Build Release `PhotoReview.slnx`: 0 warning / 0 error. Test (CI filter, runner desktop ẩn): Architecture, Core, Imaging, Shell, Shell.Integration, App (gồm Category=UI), Integration xanh; flaky đã biết (`AppForwardedOpenTests` A3) xanh khi chạy lại.
- Số đo ảnh đầu (22 vòng xen kẽ, máy rất bận): +2,9 % median, nửa thấp -0,3 % -> không hồi quy > 5 % (xem perf doc). `tune-matrix` S3 chưa đo (máy bận).
- **Mutation thủ công, 27 đột biến** (sonnet, worktree riêng): 15 bị diệt bởi test của gói; 6 sống sót không tương đương và được lấp bằng test mới (codec của ThumbnailCache/PreviewImageService phải là codec của options;
  `AppLog.ErrorForced` của SettingsStore; callback `SetCurrentImage` -> `NotifyCurrentImageChanged`; `ViewerState` transient; `UnavailableClipboardService` luôn false); 4 tương đương (slot Wpf lấp hai đường;
  `GetMonitorInfoW` vs `A`); 1 không kiểm được bằng unit test: hằng `ImdtTouchpad` của `WheelMessageSource` (chỉ quan sát được khi có thông điệp chuột thật đang dispatch). Luật L-SHARED: thêm `UseWPF`/
  ProjectReference Imaging.Wpf vào App.Shared.csproj -> test L-SHARED đỏ; thêm `System.Windows.Application` vào một ViewModel -> build đỏ.
- Phát hiện: gọi `WpfPresentationSink.SetStatusText/...` trong test khi một `Application` WPF còn sống trong tiến trình (do test khác) làm sink marshal qua `Dispatcher.Invoke` và treo testhost khi chạy gộp lớp;
  test của gói kiểm các delegate qua trường thay vì gọi sink.

## 4. Phần còn lại

Không còn phần nào của thẻ WP-09 chưa làm. Việc tiếp theo thuộc gói khác: WP-20 (shell gọi `AddPhotoReviewShared` với `PixelBufferImageCodec`), WP-19b (`RecoveryPresenter`, cửa sổ phụ), đo lại perf trên máy yên tĩnh khi đóng đợt 2.
