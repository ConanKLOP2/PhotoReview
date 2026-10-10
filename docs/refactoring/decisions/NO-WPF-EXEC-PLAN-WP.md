---
id: NO-WPF-EXEC-PLAN-WP
order: 202
summary: |-
  Phụ lục của NO-WPF-EXEC-PLAN: thẻ giao việc cho 37 gói WP-01..WP-37 (mục tiêu, file, phụ thuộc, hợp đồng, vùng xung đột, test bắt buộc, tiêu chí đo được, rủi ro, model, giờ-agent), mẫu prompt cho từng loại agent và checklist hợp nhất của lead; chưa giao gói nào.
---

# NO-WPF-EXEC-PLAN-WP - thẻ gói việc, prompt mẫu, checklist lead (2026-10-10)

Đọc cùng [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (nguyên tắc mục 2, hợp đồng mục 5, tương đương/perf mục 7). Mọi đường dẫn tính
từ gốc repo. "Hợp đồng" = mã C-xx ở mục 5 của file kế hoạch. "Xung đột" = file/thư mục gói này **ghi**; hai gói có vùng giao nhau
không chạy song song. Mọi gói: 0 cảnh báo Release, test có hang guard, `Category=UI` không bị loại, không đụng Thùng rác thật, PR không merge.

Ký hiệu model: **opus** = model mạnh nhất (đồng thời, an toàn dữ liệu, Fit/T89, bố cục, tích hợp); **sonnet** = tính năng có spec;
**haiku** = cơ học. Theo `CLAUDE.md` của người dùng: agent opus giao việc phụ (mutation check, thêm test, sửa từng file) cho sub-agent
sonnet/haiku và tự giữ vai trò điều phối + review.

---

## Đợt 0

### WP-01 - Khung project + hợp đồng v1 + test kiến trúc
- **Mục tiêu:** tạo mọi project mới (rỗng trừ file hợp đồng), file hợp đồng C-01..C-18 đúng chữ ký mục 5, luật kiến trúc khung, file duyệt
  `contracts.v1.txt`. Không đổi hành vi app.
- **Tạo:** `src/PhotoReview.App.Shared/PhotoReview.App.Shared.csproj` (net10.0-windows, `IsAotCompatible=true`, tham chiếu Core, Imaging,
  Platform.Windows, CommunityToolkit.Mvvm); `src/PhotoReview.Shell.Interop/`, `src/PhotoReview.Shell.Rendering/`, `src/PhotoReview.Shell.Win32/`
  (WinExe, `AssemblyName=PhotoReview`, `app.manifest` PerMonitorV2 + comctl32 v6, `Program.cs` hiện một cửa sổ trống rồi thoát với
  `--smoke`), `src/PhotoReview.Shell.WpfBridge/` (UseWPF, rỗng); `tests/PhotoReview.Shell.Tests/`, `tests/PhotoReview.Shell.Integration.Tests/`;
  file hợp đồng: `src/PhotoReview.Imaging/Pixels/PixelBuffer.cs` (C-01, thân `NotImplementedException`), `src/PhotoReview.Imaging/Decoding/IPlatformImageCodec.cs`,
  `PixelLease.cs`, `DecodedImage.cs` (C-02), `src/PhotoReview.Imaging/Pixels/PixelOps.cs` (C-03), `src/PhotoReview.Core/Abstractions/IUiDispatcher.cs` (C-04),
  `src/PhotoReview.App.Shared/Input/{FrameClock.cs,InputPrimitives.cs,KeyId.cs,KeyIdMapping.cs}` (C-05, C-06; `KeyId` sinh đủ giá trị từ
  `System.Windows.Input.Key` bằng script một lần, ghi kết quả vào file), `src/PhotoReview.App.Shared/Viewport/ViewportContracts.cs` (C-08),
  `src/PhotoReview.Shell.Rendering/Contracts/*.cs` (C-09, C-10, C-11 phần text), `src/PhotoReview.Shell.Win32/Overlay/OverlayContracts.cs`,
  `src/PhotoReview.App.Shared/Menus/MenuContracts.cs` (C-12), `src/PhotoReview.App.Shared/Services/IZoomPromptService.cs`,
  `src/PhotoReview.Shell.WpfBridge/ISecondaryWindowHost.cs` (C-13), `src/PhotoReview.App.Shared/Windowing/WindowingContracts.cs` (C-14),
  `src/PhotoReview.Shell.Win32/Hosting/ShellWindowContracts.cs` (C-15), `src/PhotoReview.App.Shared/Composition/SharedServiceContracts.cs`,
  `src/PhotoReview.Shell.Win32/Startup/ShellStartupContext.cs` (C-16), `tests/PhotoReview.TestSupport/Golden/GoldenContracts.cs` (C-17),
  `src/PhotoReview.Core/Diagnostics/ShellPerfMarks.cs` (C-18); `tests/PhotoReview.Architecture.Tests/ContractSurfaceTests.cs` +
  `Approved/contracts.v1.txt`; luật L-SHELL, L-AOT, L-CONTRACT trong `LayerDependencyTests.cs`/file mới `ShellRulesTests.cs`.
- **Sửa:** `PhotoReview.slnx`; `.github/workflows/ci.yml` (build 2 project test mới, cùng filter); `tools/verify-all.ps1` (thêm project test);
  `TestFilterDriftTests` sẽ đòi đồng bộ filter - cập nhật đúng chỗ.
- **Phụ thuộc:** NE-1, NE-2 đã chốt. **Hợp đồng:** sở hữu tất cả (khung). **Xung đột:** `PhotoReview.slnx`, `ci.yml`, `verify-all.ps1`,
  `tests/PhotoReview.Architecture.Tests/*` (mọi gói sau chỉ thêm file mới trong Architecture.Tests, không sửa file của WP-01 trừ khi thẻ ghi).
- **Test:** `ContractSurfaceTests.Contracts_MatchApprovedSurface` (mutate: đổi tên một thành viên -> đỏ); `ShellRulesTests.*` (mutate: thêm
  `using System.Windows` vào Shell -> đỏ); smoke `PhotoReview.exe --smoke` exit 0 (`Category=UI`).
- **Hoàn thành khi:** build Release 0 cảnh báo; toàn bộ test cũ xanh; 2 project test mới chạy trong CI; file duyệt sinh từ reflection, không viết tay.
- **Rủi ro:** thấp. **Model:** opus (đặt nền cho mọi gói). **Giờ:** 8-12.
- **Đã làm (2026-10-10):** xem [NOWPF-WP01-SCAFFOLD](NOWPF-WP01-SCAFFOLD.md) - thêm so với thẻ: App tham chiếu App.Shared và dời sớm
  `ViewerStretchMode`, `IPresentationSink`; Shell.Rendering tham chiếu App.Shared; L-SHARED + L-AFFINITY bật luôn; C-07/`ExifOrientation`/
  `IClipboardService` chưa khoá (v1.1).

## Đợt 1 - seam trong mã dùng chung (app WPF giữ nguyên hành vi)

### WP-02 - PixelBuffer, codec, PixelOps
- **Mục tiêu:** thực thi C-01..C-03; helper test `PixelAssert`.
- **Tạo/sửa:** `src/PhotoReview.Imaging/Pixels/PixelBuffer.cs`, `PixelOps.cs`, `src/PhotoReview.Imaging/Decoding/{PixelLease.cs,DecodedImage.cs,PixelBufferImageCodec.cs}`;
  `tests/PhotoReview.Imaging.Tests/Pixels/{PixelBufferTests.cs,PixelOpsOrientationTests.cs,PixelBufferImageCodecTests.cs}`;
  `tests/PhotoReview.TestSupport/PixelAssert.cs`.
- **Phụ thuộc:** WP-01. **Hợp đồng:** sở hữu C-01..C-03 (trừ `WpfBitmapSourceCodec`). **Xung đột:** `src/PhotoReview.Imaging/Pixels/*`, 3 file Decoding nêu trên.
- **Test bắt buộc:** cấp phát/giải phóng (Dispose hai lần an toàn, dùng sau Dispose ném `ObjectDisposedException`, finalizer trả bộ nhớ - đo bằng
  `PixelBuffer.LiveCount` Debug-only); 8 orientation so với tham chiếu WPF `TransformedBitmap` (test dùng `TestSupport.Windows`, so từng byte);
  ảnh lẻ 1x1, 1xN, Nx1; `TryGetSpan` false khi > 2 GB (giả lập bằng kích thước, không cấp phát thật); `HasAlpha`/`IsFullyOpaque` khớp
  `PreviewCacheFile.HasAlpha(BitmapSource)` hiện tại trên 6 định dạng; property test `ApplyOrientation(o) rồi ApplyOrientation(nghịch đảo o)` = gốc.
- **Hoàn thành khi:** test xanh; mutation (sub-agent haiku chạy Stryker phạm vi `Pixels/*`) >= 85 %; không ai dùng kiểu mới ngoài test (chưa nối).
- **Rủi ro:** TB (bộ nhớ native). **Model:** opus. **Giờ:** 12-18.

### WP-03 - WIC Direct, thumbnail nhúng, EXIF không qua WPF
- **Mục tiêu:** `WicDirectDecoder` ghi thẳng vào `PixelBuffer` rồi `codec.FromPixels`; `EmbeddedThumbnailReader` dùng WIC; `WpfExifReader` ->
  `WicExifReader`; `ExifOrientation` chỉ còn phần thuần (phần WPF giữ trong file `ExifOrientationWpf.cs` tạm thời trong Imaging, WP-06 dời đi).
- **Sửa:** `src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs`, `Decoding/EmbeddedThumbnailReader.cs`, `Decoding/ExifOrientation.cs`,
  tạo `Decoding/Wic/WicExifReader.cs`, `Decoding/ExifOrientationWpf.cs`; `src/PhotoReview.App/Composition/DecoderProviders.cs` (truyền codec WPF tạm =
  adapter nội bộ `BitmapSourceFromPixels` đặt ở `src/PhotoReview.Imaging/Decoding/WpfBitmapSourceCodec.cs`, WP-06 dời).
- **Phụ thuộc:** WP-02. **Hợp đồng:** tiêu thụ C-01..C-03; tạo bản tạm của `WpfBitmapSourceCodec` (C-02). **Xung đột:** `Imaging/Decoding/**` trừ
  `WpfBitmapImageDecoder.cs`, `WpfDecodedImage.cs`, `WpfImageAdapter.cs`; `App/Composition/DecoderProviders.cs`.
- **Test:** `DecoderQualityGateTests` (QG-1..6) xanh không nới ngưỡng; test mới `WicDirectDecoderPixelParityTests`: 30 JPEG/PNG/TIFF/WebP/HEIC (khi có codec)
  x 8 orientation -> pixel giống hệt đường cũ (`BitmapSource` cũ ghi làm tham chiếu ngay trong test); `EmbeddedThumbnailReaderTests` giữ; EXIF: `ExifSummary`
  giống hệt trên corpus EXIF hiện có; đếm copy: `ReviewMetrics` hoặc counter test cho thấy đường WIC -> PixelBuffer -> BitmapSource đúng 2 copy (như cũ).
- **Hoàn thành khi:** Imaging.Tests 100 % xanh; bench decode (`Benchmark.Cli --benchmark-all`, 24 MP -> FHD, WIC Direct) P50 không tệ hơn 5 %.
- **Rủi ro:** TB. **Model:** opus. **Giờ:** 14-20.

### WP-04 - Cache đĩa: encode/decode bằng WIC
- **Mục tiêu:** `PreviewCacheFile` (JPEG), `DiskCacheStore` (PNG), `PreviewImageService` persist, `ThumbnailCache` không dùng kiểu WPF; định dạng file không đổi.
- **Sửa:** `src/PhotoReview.Imaging/Caching/{PreviewCacheFile.cs,DiskCacheStore.cs,PreviewImageService.cs,ThumbnailCache.cs,AtomicCacheFile.cs}`; tạo
  `src/PhotoReview.Imaging/Caching/WicImageEncoder.cs`, `WicCacheImageReader.cs`; `src/PhotoReview.App/App.xaml.cs` chỉ ở chỗ đăng ký `ThumbnailCache`
  (truyền decoder thumbnail) - một hunk, ghi trong PR.
- **Phụ thuộc:** WP-02 (song song WP-03, WP-05). **Hợp đồng:** tiêu thụ C-01, C-02 (`ToPixels`/`PixelLease`). **Xung đột:** `Imaging/Caching/**`, một hunk `App.xaml.cs`.
- **Test:** `PreviewCacheCrossVersionTests`: (1) file do mã **trước** WP-04 ghi (fixture sinh trong test bằng `JpegBitmapEncoder` + header hiện tại) đọc được
  bởi mã mới, pixel lệch <= 1/255 trung bình; (2) file mã mới ghi đọc được bằng đường WPF cũ (`BitmapImage`); (3) chất lượng: PSNR mã mới vs cũ >= 45 dB;
  PNG disk cache tương tự; `ThumbnailCache*Tests`, `DiskCacheStore*Tests` giữ; test persist không giữ `BitmapSource` (channel `IDecodedImage`).
- **Hoàn thành khi:** Imaging.Tests + App.Tests xanh; kích thước file cache trung bình trên 50 ảnh F4 lệch <= 3 %; thời gian persist P50 không tệ hơn 10 %.
- **Rủi ro:** TB (tương thích cache). **Model:** opus. **Giờ:** 14-20.

### WP-05 - TurboJpeg và LibRaw ra PixelBuffer
- **Sửa:** `src/PhotoReview.Imaging.TurboJpeg/TurboJpegDecoder.cs`, `src/PhotoReview.Imaging.LibRaw/{RgbBgraResampler.cs,LibRawDecoder.cs}`; csproj nhận codec qua DI
  (`App/Composition/ServiceFactories.cs` - một hunk).
- **Phụ thuộc:** WP-02. **Hợp đồng:** tiêu thụ C-01..C-03. **Xung đột:** 2 project trên, một hunk `ServiceFactories.cs`.
- **Test:** test TurboJpeg/LibRaw hiện có xanh; parity pixel với đường cũ (8 orientation, 10 JPEG, RAW corpus nếu có - skip trong CI như hiện tại).
- **Hoàn thành khi:** như trên + bench TurboJpeg P50 không tệ hơn 5 %. **Rủi ro:** thấp. **Model:** sonnet. **Giờ:** 8-12.

### WP-06 - Tách `PhotoReview.Imaging.Wpf`, bỏ `UseWPF` khỏi Imaging*
- **Mục tiêu:** dời `WpfBitmapImageDecoder`, `WpfDecodedImage`, `WpfImageAdapter`, `WpfExifReader` (nếu còn), `ExifOrientationWpf`, `WpfBitmapSourceCodec` sang
  project mới; `ImageDecoderFactory` nhận `Func<IImageDecoder>? wpfDecoderFactory`; bỏ `UseWPF` ở Imaging, Raw, LibRaw, TurboJpeg, Benchmarking.
- **Tạo/sửa:** `src/PhotoReview.Imaging.Wpf/*`; csproj 5 project; `src/PhotoReview.App/PhotoReview.App.csproj`, `Composition/DecoderProviders.cs`;
  `tests/PhotoReview.Imaging.Tests/*.csproj` (tham chiếu Imaging.Wpf cho test backend WPF); luật L-IMG (`LayerDependencyTests.cs` Rule 2/7/9/10,
  `ImagingPublicSurfaceTests` mở rộng toàn namespace).
- **Phụ thuộc:** WP-03, WP-04, WP-05 merge. **Hợp đồng:** sở hữu `WpfBitmapSourceCodec` (C-02). **Xung đột:** csproj các project Imaging*, `Benchmarking`,
  `DecoderProviders.cs`, `LayerDependencyTests.cs`, `ImagingPublicSurfaceTests.cs`.
- **Test:** L-IMG (mutate: thêm `using System.Windows.Media` vào Imaging -> build hoặc test đỏ); `DecoderRegistrationTests` giữ; backend `Wpf` trong Settings vẫn
  chọn được ở bản WPF; test mới `ImageDecoderFactory_WithoutWpf_MapsWpfToWicDirect_AndKeepsSetting` (bản Win32; config không bị ghi lại).
- **Hoàn thành khi:** **perf gate đợt 1** (mục 7.4) đạt, ghi file mới trong `perf/` tên `<ngày>-nowpf-wave1`; 9.341+ test xanh.
- **Rủi ro:** TB. **Model:** opus (điểm tích hợp). **Giờ:** 8-12.

### WP-07 - Input và seam UI không-WPF
- **Mục tiêu:** thực thi C-06, C-07 (bản WPF), C-04 ở WPF: `PointD/PointerButton/KeyModifiers/KeyId/KeyIdMapping`; đổi chữ ký `IImageSurface`,
  `PointerInputController`, `MouseGestures`, `ShortcutRouter`, `WheelMessageSource`, `ShortcutKeyName`, `ViewportSnapshot` (bool thay Visibility);
  `DispatcherUiScheduler : IUiDispatcher`; adapter tại `MainWindow.xaml.cs`, `WpfImageSurface.cs`. Hành vi y nguyên.
- **Sửa:** `src/PhotoReview.App/Input/*.cs` (trừ `KineticPan.cs`, `TouchpadGestures.cs`, `ReviewCommand.cs`, `ViewportOperationVersion.cs` - không cần đổi),
  `MainWindowConvergence.cs`, `MainWindowHelpers.cs` (nếu dùng Point), `Services/{WpfImageSurface.cs,ShortcutKeyName.cs,WpfKeyNameValidator.cs,DispatcherUiScheduler.cs}`,
  `MainWindow.xaml.cs` (chỉ các handler input), `src/PhotoReview.App.Shared/Input/{KeyIdMapping.cs,InputPrimitives.cs}` (thực thi); test App.Tests liên quan
  (`PointerInputControllerTests*`, `ShortcutRouter*Tests`, `MouseGestures*`) đổi kiểu.
- **Phụ thuộc:** WP-01. **Hợp đồng:** sở hữu C-06, C-07 (WPF), adapter C-04 WPF. **Xung đột:** `App/Input/*`, `App/MainWindow.xaml.cs` (vùng input:
  `Window_KeyDown`, `MainImage_*`, `ImageScroll_*`, `WindowMessageHook`), `App/Services/WpfImageSurface.cs`, `MainWindowConvergence.cs`. Không chạy song song
  gói nào khác ghi `MainWindow.xaml.cs` (WP-08 chỉ ghi `ApplyFullscreenState`, `Window_Closing`, `PrepareFirstShow` - giao sau hoặc lead hợp nhất).
- **Test:** `KeyIdMappingTests` (G-KEY: mọi giá trị `Key` WPF -> `KeyId` cùng số + cùng tên; VK hai chiều khớp `KeyInterop` - test ở App.Tests vì cần WPF);
  toàn bộ test input/T89 hiện có xanh **không đổi assert**; `MainWindowBehaviorTests.Fit` (UI) xanh; mutation phạm vi `KeyIdMapping` >= 90 %.
- **Hoàn thành khi:** không còn `using System.Windows` trong `App/Input/*` trừ adapter; test xanh; không đổi `config.json`.
- **Rủi ro:** Cao (chạm đường Fit/T89 và phím tắt). **Model:** opus. **Giờ:** 14-20.

### WP-08 - Placement và F11 theo HWND
- **Mục tiêu:** C-14: `WindowPlacementData`, `IWindowPlacementStore` (`JsonWindowPlacementStore`), `WindowPlacementRules`, `IFullscreenController`
  (`FullscreenWindowPlacer` nhận `nint hwnd` + `WindowShowState`); `WindowPlacementService` WPF thành adapter mỏng; `InitialViewportPredictor` bỏ `WindowState`.
- **Sửa/tạo:** `src/PhotoReview.App.Shared/Windowing/{JsonWindowPlacementStore.cs,WindowPlacementRules.cs,WindowPlacementJsonContext.cs}`;
  `src/PhotoReview.App/WindowPlacementService.cs`, `Coordinators/FullscreenWindowPlacer.cs` (logic dời sang App.Shared `Windowing/FullscreenController.cs`,
  file cũ thành adapter), `Coordinators/MonitorLayout.cs` (dời), `Services/InitialViewportPredictor.cs`; `MainWindow.xaml.cs` vùng `ApplyFullscreenState`,
  `PrepareFirstShow`, `Window_Closing`.
- **Phụ thuộc:** WP-01. **Hợp đồng:** sở hữu C-14. **Xung đột:** các file trên; `MainWindow.xaml.cs` (3 method - xếp sau WP-07 hoặc rebase trên nó).
- **Test:** `WindowPlacementService*Tests`, `FullscreenWindowPlacer*Tests`, `WindowPlacementIsolationTests`, `FullscreenTransitionFrameTests` xanh; mới:
  `JsonWindowPlacementStoreRoundTripTests` (file do code cũ ghi -> đọc -> ghi -> byte-identical sau chuẩn hoá xuống dòng; JSON source-gen giữ đúng tên trường).
- **Hoàn thành khi:** test xanh, `window-placement.json` không đổi định dạng. **Rủi ro:** TB (F11 từng lỗi #351/#372). **Model:** sonnet. **Giờ:** 8-12.

### WP-09 - Tách `PhotoReview.App.Shared` (dời file)
- **Mục tiêu:** dời sang App.Shared: `ViewModels/*`, `Coordinators/*` (trừ adapter WPF), `Input/*` (thuần), `Composition/{MainViewModelCompositionRoot,ServiceFactories,DecoderProviders,LibRawPreviewFallback}.cs`
  (phần không-WPF), `Services/{ViewportSizeSource,PreviewStateContext,IPresentationObserver,InitialImagePrewarm,InitialViewportPredictor,RenderFrameTrace,IClipboardService}.cs`,
  `Localization/{LocalizationService,LanguageOptions,TranslationExport,TranslationProblems,LocalizationSource}.cs`, `AppLog.cs`, `TaskLogging.cs`, `LaunchArguments.cs`,
  `BuildInfo.cs`, `FileHashService.cs`, `MainWindowHelpers.cs`, `MainWindowConvergence.cs`. Tạo `SharedServiceRegistration.AddPhotoReviewShared` (C-16) từ phần
  không-WPF của `App.ConfigureServices`; `IPresentationSinkFactory`; bỏ `Clipboard = new WpfClipboardService()` mặc định trong `MainViewModel` (tiêm qua DI).
- **Sửa:** csproj App/App.Shared/tests, `App.xaml.cs` (ConfigureServices gọi hàm chung), `AssemblyInfo.cs` (InternalsVisibleTo), Architecture.Tests (L-SHARED,
  L-AFFINITY, cập nhật đường dẫn trong `AppCompositionTests`, `StructureOptimizeRulesTests`, `DialogBoundaryTests`, `HotPathHonestyRuleTests`, `LocalizationGuardTests`
  - các test quét theo đường dẫn source).
- **Phụ thuộc:** WP-06, WP-07, WP-08 merge. **Hợp đồng:** sở hữu C-16 (phần shared); có thể phát hành **hợp đồng v1.1** nếu đổi namespace (lead duyệt).
  **Xung đột:** gần như toàn bộ `src/PhotoReview.App` - **chạy một mình**, không gói nào khác ghi App trong lúc này.
- **Test:** toàn bộ suite xanh, không đổi assert; `git diff -M` cho thấy >= 95 % là rename; L-SHARED (mutate: thêm `using System.Windows` vào một VM -> đỏ).
- **Hoàn thành khi:** như trên + Benchmark.Cli build + perf smoke (`tune-matrix` S3 Repeat 3) không lệch ngoài nhiễu. **Rủi ro:** TB (diff lớn). **Model:** sonnet
  (cơ học nhưng lớn; lead review kỹ). **Giờ:** 12-18.

### WP-10 - Bộ ghi golden từ bản WPF (G-VIEW, G-INPUT, G-KEY, G-MENU)
- **Mục tiêu:** C-17 + công cụ ghi chạy trên **MainWindow WPF thật** (desktop ẩn), sinh `tests/Fixtures/golden/*.v1.json`, và test WPF xác nhận lại golden
  (để golden luôn đúng với bản WPF hiện hành).
- **Tạo:** `tests/PhotoReview.TestSupport/Golden/{GoldenContracts.cs,GoldenJsonContext.cs,GoldenFile.cs}`; `tests/PhotoReview.Integration.Tests/Golden/{ViewportGoldenRecorder.cs,
  InputScriptRecorder.cs,MenuGoldenRecorder.cs,GoldenWpfConformanceTests.cs}`; `tests/PhotoReview.App.Tests/Golden/KeyNameGoldenTests.cs`; `tests/Fixtures/golden/*.json`;
  `tools/diag/record-golden.ps1` (chạy recorder qua `run-tests-hidden.ps1` với `PHOTOREVIEW_GOLDEN_RECORD=1`, `Category=Manual`).
- **Phụ thuộc:** WP-01 (có thể bắt đầu trước WP-07: recorder dùng API WPF; nếu WP-07 merge trước, rebase). **Hợp đồng:** sở hữu C-17. **Xung đột:** chỉ file mới.
- **Test:** `GoldenWpfConformanceTests` (`Category=UI`) chạy golden đã commit lại trên WPF -> khớp 100 %; mutate: đổi `ScrollBarThickness` giả trong recorder ->
  conformance đỏ. Lưới và kịch bản: đúng mục 7.3 của file kế hoạch (>= 600 ca G-VIEW, >= 60 kịch bản G-INPUT, 12 ca G-MENU, mọi `Key`), cộng
  **G-OVL** `overlay-boxes.v1.json`: hộp bao (TranslatePoint + ActualWidth/Height) của `ToolbarPanel`, `StatusPanel`, `FolderInfoPanel`,
  `ZoomIndicatorPanel`, `CapturePairBadge`, `ComparePanel` ở 3 kích thước cửa sổ x 2 DPI x 2 cỡ chữ overlay, với chuỗi cố định.
- **Hoàn thành khi:** golden commit, conformance xanh 3 lần liên tiếp (không flaky), thời gian chạy conformance <= 3 phút. **Rủi ro:** TB (golden sai là nguy hiểm
  nhất của dự án). **Model:** sonnet (spec rõ), lead review lưới ca. **Giờ:** 16-24.

### WP-11 - JSON source generation cho mọi serializer (P2, AOT prep)
- **Mục tiêu:** 29 lời gọi `JsonSerializer` -> `JsonSerializerContext` (journal, session, placement, export/import, perf, ...), output byte-identical.
- **Sửa:** file Core/Imaging/Platform có `JsonSerializer.` (grep); tạo context theo project. Không chạm App (placement thuộc WP-08).
- **Phụ thuộc:** WP-01. **Xung đột:** file Core có serializer (danh sách grep trong PR); không chung với WP-02..06 (Imaging chỉ cache header - xác nhận bằng grep, nếu có
  thì giao phần Imaging cho WP-04).
- **Test:** test round-trip từng loại: file mẫu do code cũ ghi -> đọc -> ghi -> byte-identical; journal fault-injection xanh; `IsAotCompatible=true` cho Core không còn IL2026/IL3050.
- **Hoàn thành khi:** analyzer AOT Core 0 cảnh báo. **Rủi ro:** TB (dữ liệu người dùng: so byte). **Model:** sonnet. **Giờ:** 10-14.

## Đợt 2 - nền tảng shell (không chạm app WPF)

### WP-12 - WIC sang `[GeneratedComInterface]` (P2)
- **Sửa:** `src/PhotoReview.Imaging/Decoding/Wic/WicInterop.cs`, `WicCodecAvailability.cs`, chỗ dùng trong `WicDirectDecoder.cs`, `WicImageEncoder.cs`, `WicExifReader.cs`;
  `StrategyBasedComWrappers`.
- **Phụ thuộc:** WP-06. **Xung đột:** `Imaging/Decoding/Wic/*`, `Imaging/Caching/Wic*`. **Test:** Imaging.Tests xanh; parity pixel như WP-03; bench P50 không tệ hơn 3 %;
  analyzer AOT Imaging 0 cảnh báo. **Rủi ro:** TB (vòng đời COM, Release). **Model:** sonnet. **Giờ:** 10-14.

### WP-13a - Interop Win32 (user32/dwmapi/shcore/kernel32/ole32/comctl32)
- **Tạo:** `src/PhotoReview.Shell.Interop/Win32/{User32.cs,Dwmapi.cs,Shcore.cs,Kernel32.cs,Ole32.cs,Comctl32.cs,WindowMessages.cs,Structs.cs}` - chỉ khai báo
  `LibraryImport` + struct + hằng (WM_*, WS_*, SWP_*, DWMWA_*). Danh sách hàm: mọi P/Invoke hiện có trong App (kiểm kê mục 4) + RegisterClassExW, CreateWindowExW,
  DefWindowProcW, GetMessageW/PeekMessageW/TranslateMessage/DispatchMessageW, MsgWaitForMultipleObjectsEx, PostMessageW, SetCapture/ReleaseCapture, LoadCursorW/SetCursor,
  GetDpiForWindow, AdjustWindowRectExForDpi, TrackPopupMenuEx/CreatePopupMenu/AppendMenuW/DestroyMenu, TaskDialogIndirect, OpenClipboard/EmptyClipboard/SetClipboardData/
  CloseClipboard/GlobalAlloc/GlobalLock, DragAcceptFiles (dự phòng), RegisterDragDrop/RevokeDragDrop, GetSystemMetricsForDpi, GetCurrentInputMessageSource.
- **Phụ thuộc:** WP-01. **Xung đột:** `Shell.Interop/Win32/*`. **Test:** `InteropSignatureTests` (sizeof struct = giá trị Win32 SDK ghi trong test; gọi thử GetDpiForSystem, RegisterClassEx/Unregister).
- **Rủi ro:** thấp. **Model:** haiku. **Giờ:** 6-10.

### WP-13b - Interop D3D11/DXGI/D2D1/DWrite/shell COM
- **Tạo:** `src/PhotoReview.Shell.Interop/Graphics/{D3D11.cs,Dxgi.cs,D2D1.cs,DWrite.cs,Guids.cs}`, `Shell/{IFileOpenDialog.cs,IDropTarget.cs,IDataObject.cs}` -
  `[GeneratedComInterface]` theo thứ tự vtable của header SDK (ghi số thứ tự slot trong comment mỗi method), chỉ các method dùng tới + chỗ giữ slot.
- **Phụ thuộc:** WP-01. **Xung đột:** `Shell.Interop/Graphics/*`, `Shell.Interop/Shell/*`. **Test:** `D2DSmokeTests` (`Category=Native`): tạo D3D11 WARP device, D2D factory/device context,
  bitmap 4x4, vẽ, đọc lại pixel đúng; DWrite tạo text layout "Tiếng Việt" đo được kích thước > 0. Lỗi slot vtable = crash, nên test này bắt buộc.
- **Rủi ro:** TB. **Model:** sonnet. **Giờ:** 12-18.

### WP-14 - Cửa sổ, message loop, dispatcher, frame clock
- **Mục tiêu:** C-15 (`ShellWindow`), C-04 (`Win32UiDispatcher`, `Win32UiSynchronizationContext`), C-05 (`SwapChainFrameClock` + `TimerFrameClock` dự phòng),
  vòng lặp `MsgWaitForMultipleObjectsEx` (message, waitable swap chain, hàng đợi dispatcher), WndProc `UnmanagedCallersOnly`, xử lý `WM_DPICHANGED` (áp rect gợi ý),
  `WM_GETMINMAXINFO`, `WM_NCCREATE` cho DPI, đóng có `Closing` huỷ được.
- **Tạo:** `src/PhotoReview.Shell.Win32/Hosting/{ShellWindow.cs,MessageLoop.cs,Win32UiDispatcher.cs,Win32UiSynchronizationContext.cs,SwapChainFrameClock.cs,TimerFrameClock.cs,WndProcThunk.cs}`.
- **Phụ thuộc:** WP-13a (có thể bắt đầu với khai báo tạm cục bộ rồi đổi sang Interop khi WP-13a merge). **Hợp đồng:** sở hữu C-04 (Win32), C-05, C-15. **Xung đột:** `Shell.Win32/Hosting/*`.
- **Test (Shell.Tests + Shell.Integration.Tests):** thứ tự ưu tiên Send > Normal > Render > Background (gửi xen kẽ 1000 việc, kiểm thứ tự); Background chỉ chạy khi không còn
  message; `await` trên UI thread quay về đúng thread (`ADR 0005`); `InvokeAsync` từ thread pool hoàn thành; ngoại lệ trong việc post được log, loop không chết;
  `Closing` huỷ; DPI đổi giả lập bằng `SendMessage(WM_DPICHANGED)`; frame clock phát <= 1 lần/khung và dừng khi không subscriber. Không `Task.Delay` trong assert
  (dùng `TestSupport.Wait` có giới hạn).
- **Hoàn thành khi:** test xanh 20 lần liên tiếp (vòng lặp test `-Repeat` cục bộ); CPU nhàn rỗi của cửa sổ trống < 0,5 %. **Rủi ro:** Cao (đồng thời). **Model:** opus. **Giờ:** 16-24.

### WP-15 - Renderer Direct2D
- **Mục tiêu:** C-09, C-10: `D2DRenderSurface` (D3D11 device BGRA, DXGI flip-model `FLIP_DISCARD`, `SetMaximumFrameLatency(1)`, waitable), `CreateOffscreen` WARP,
  device lost -> tạo lại + `DeviceRecreated`, `GpuImageCache` (LRU theo bytes, `Prefetch` ở Background, tile > 16384), ánh xạ `ImageInterpolation`.
- **Tạo:** `src/PhotoReview.Shell.Rendering/{D2DRenderSurface.cs,D2DDrawContext.cs,GpuImageCache.cs,TiledGpuImage.cs,DeviceResources.cs}`.
- **Phụ thuộc:** WP-13b, WP-02 (PixelBuffer). **Hợp đồng:** sở hữu C-09, C-10. **Xung đột:** `Shell.Rendering/*` (trừ `Text/*` của WP-17).
- **Test:** offscreen WARP: vẽ ảnh 1:1 -> đọc lại giống từng byte; Fit downscale -> PSNR >= 40 dB so với WIC Fant làm tham chiếu (chỉ để bắt lỗi thô); tile: ảnh 30000x2000
  vẽ đúng ở mép tile (không đường nối, so với tham chiếu CPU); device lost giả (lớp `DeviceResources` có seam ném `D2DERR_RECREATE_TARGET`) -> cache rỗng, khung kế vẽ được;
  `GpuImageCache` không vượt `BudgetBytes`; upload 1920x1080 P95 <= 2 ms trên máy này (`Category=Manual` báo số, không assert thời gian trong CI).
- **Hoàn thành khi:** test xanh; cửa sổ demo (`PhotoReview.exe --demo <file>`) vẽ ảnh, resize mượt, không rò GPU sau 1000 lần upload/evict (đo `GpuBytes`). **Rủi ro:** Cao. **Model:** opus. **Giờ:** 20-30.

### WP-16 - Engine viewport + ViewportController
- **Mục tiêu:** C-08 `ViewportLayoutEngine` khớp G-VIEW; `ViewportController` (`Shell.Win32/Viewing/ViewportController.cs`) implement `IImageSurface` + `IFitSurface` (C-07)
  trên `IShellWindow` + `IUiDispatcher` + `IFrameClock`; chạy `FitViewController` và `PointerInputController` **không sửa** trên nó.
- **Tạo:** `src/PhotoReview.App.Shared/Viewport/ViewportLayoutEngine.cs`, `src/PhotoReview.Shell.Win32/Viewing/{ViewportController.cs,ViewportState.cs}`;
  `tests/PhotoReview.Shell.Tests/Viewport/{ViewportGoldenTests.cs,ViewportInputGoldenTests.cs,ViewportControllerTests.cs}`.
- **Phụ thuộc:** WP-01 (hợp đồng), WP-10 (golden) - có thể bắt đầu engine trước khi golden xong, nhưng không merge trước. WP-07 (chữ ký C-07) merge trước khi nối `PointerInputController`.
- **Hợp đồng:** sở hữu C-08, C-07 (Win32). **Xung đột:** `App.Shared/Viewport/*`, `Shell.Win32/Viewing/*`.
- **Test:** G-VIEW 100 % ca trong 0,5 DIP; G-INPUT 100 % checkpoint (chạy `PointerInputController` thật + `ViewportController` với fake window/clock thời gian cố định);
  `FitViewControllerTests` + `FitViewControllerMutationGapTests` chạy lại với `ViewportController` (adapter test) xanh; property test: offset luôn trong [0, Max].
- **Hoàn thành khi:** như trên + mutation `ViewportLayoutEngine` >= 90 % (sub-agent sonnet chạy Stryker). **Rủi ro:** Cao (Fit/T89). **Model:** opus. **Giờ:** 16-24.

### WP-17 - Text DirectWrite + bộ overlay + animator
- **Tạo:** `src/PhotoReview.Shell.Rendering/Text/{DWriteTextRenderer.cs,TextLayoutCache.cs}`, `src/PhotoReview.Shell.Win32/Overlay/{OverlayHost.cs,PanelElement.cs,TextElement.cs,
  ButtonElement.cs,Animator.cs,DarkPalette.cs}` (`DarkPalette.cs` sao giá trị màu từ `Themes/DarkPalette.xaml`, bảng tên -> ColorF, test so với XAML).
- **Phụ thuộc:** WP-13b, WP-15 (hợp đồng C-09 đủ để bắt đầu với fake). **Hợp đồng:** sở hữu C-11. **Xung đột:** `Shell.Rendering/Text/*`, `Shell.Win32/Overlay/*` (phần khung; WP-23 thêm file panel cụ thể).
- **Test:** đo chữ tiếng Việt có dấu, cắt "…" khi vượt MaxWidth; `DarkPaletteTests` (mọi khoá trong XAML có trong bảng, cùng giá trị); animator: fade 1->0 trong 200 ms với
  clock giả -> opacity đúng tại 0/50/100/200 ms, `FadeTo` mới huỷ tween cũ (như `FadeTargetGate`); hit-test bỏ qua phần tử opacity 0.
- **Rủi ro:** TB. **Model:** sonnet. **Giờ:** 12-18.

### WP-18 - Mô hình menu + host native
- **Tạo:** `src/PhotoReview.App.Shared/Menus/ContextMenuModelBuilder.cs` (dùng `ContextMenuItems.Compute`, shortcut text từ `Shortcuts`, logic `BuildZoomMenu`/`ApplyContextMenuLayout`
  của `MainWindow` được **sao** sang builder - MainWindow WPF không đổi); `src/PhotoReview.Shell.Win32/Menus/{NativePopupMenuHost.cs,DarkModeMenus.cs}` (uxtheme
  `SetPreferredAppMode`/`AllowDarkModeForWindow` qua `GetProcAddress` ordinal, rơi về sáng).
- **Phụ thuộc:** WP-13a, WP-10 (G-MENU). **Hợp đồng:** sở hữu C-12. **Xung đột:** `App.Shared/Menus/*`, `Shell.Win32/Menus/*`.
- **Test:** G-MENU 12 ca khớp thứ tự id + separator; builder không tạo separator đầu/cuối/đôi (property test trên mọi tập ẩn); host: test UI mở menu bằng
  `PostMessage(WM_KEYDOWN, VK_ESCAPE)` đóng được, trả null.
- **Rủi ro:** thấp-TB. **Model:** sonnet. **Giờ:** 10-14.

### WP-19a - Hộp thoại native + clipboard
- **Tạo:** `src/PhotoReview.Shell.Win32/Dialogs/{Win32DialogService.cs,TaskDialogs.cs,FolderPicker.cs,Win32ClipboardService.cs}`.
- **Phụ thuộc:** WP-13a/b. **Hợp đồng:** sở hữu phần native C-13. **Xung đột:** `Shell.Win32/Dialogs/*` trừ `WpfBridgeLoader.cs`.
- **Test:** `TaskDialog` hiện trên desktop ẩn và đóng bằng `PostMessage` (IDCANCEL) -> `ShowConfirmation` false; folder picker: chỉ test tạo/huỷ COM (không mở UI trong CI);
  clipboard: set -> đọc lại bằng `GetClipboardData` (`Category=Native`, khôi phục nội dung cũ). Chuỗi nút/tiêu đề lấy từ `Tr`.
- **Rủi ro:** thấp. **Model:** sonnet. **Giờ:** 8-12.

### WP-19b - `App.WpfWindows` + `Shell.WpfBridge` (cửa sổ phụ WPF nạp muộn)
- **Mục tiêu:** dời 8 cửa sổ phụ + `RecoveryPathPanel` + `RecoveryPresenter` + `Themes/*` + `TrExtension` + `Converters` cần cho chúng sang library `src/PhotoReview.App.WpfWindows`;
  `WpfDialogService` của App dùng library; `Shell.WpfBridge` implement C-13b, owner = HWND Win32 (`WindowInteropHelper.Owner`), resource dictionary nạp vào
  `Application` tạo lười (`new Application { ShutdownMode = OnExplicitShutdown }` một lần, không `Run`) hoặc merge vào từng Window; modeless (Benchmark) chạy được nhờ hook
  `ComponentDispatcher` trong `MessageLoop` (WP-14 cung cấp điểm móc `IMessageFilter`).
- **Tạo/sửa:** project mới; csproj App; `Services/WpfDialogService.cs`; `MainWindow.xaml.cs` (chỉ `OpenClickZoomCustomDialogAsync` -> `IZoomPromptService`);
  `src/PhotoReview.Shell.WpfBridge/*`; `src/PhotoReview.Shell.Win32/Dialogs/WpfBridgeLoader.cs` (`[MethodImpl(MethodImplOptions.NoInlining)]`); luật L-SHELL/L-COMP.
- **Phụ thuộc:** WP-09, WP-14 (điểm móc loop). **Hợp đồng:** sở hữu C-13b, `IZoomPromptService`. **Xung đột:** các file cửa sổ phụ, `WpfDialogService.cs`, `App.csproj`,
  `DialogBoundaryTests.cs`, `XamlThemeRulesTests.cs`, `LocalizationGuardTests.cs` (đường dẫn XAML).
- **Test:** mọi test cửa sổ phụ hiện có xanh (đường dẫn mới); test shell: mở Settings qua bridge trên desktop ẩn, lưu, `SettingsStore` phát reload, đóng bằng
  `PostMessage(WM_CLOSE)`; **test nạp muộn**: khởi động shell, hiện ảnh, assert `AppDomain.CurrentDomain.GetAssemblies()` không chứa `PresentationFramework` trước khi mở
  cửa sổ phụ; phím Tab/Enter trong Benchmark (modeless) hoạt động.
- **Hoàn thành khi:** mở Settings lần đầu <= 600 ms, lần sau <= 150 ms (`Category=Manual` báo số). **Rủi ro:** TB-Cao (vòng lặp lồng, focus). **Model:** opus. **Giờ:** 14-20.

## Đợt 3 - lắp tính năng vào shell

### WP-20 - Composition root + startup shell
- **Mục tiêu:** `Program.Main` [STAThread] theo trình tự: mốc `appStartup` -> `LaunchArguments` -> đọc config + placement prefetch + warm parser trên pool -> `InstanceScope`
  (prefix theo NE-6) + forward -> early decode (`InitialViewportPredictor`, `InitialImagePrewarm`, codec pixels) -> tạo D3D device song song trên pool -> `ShellWindow`
  ẩn + cloak + `SetWindowPlacement` trước `ShowWindow` -> DI (`AddPhotoReviewShared` + phần shell) -> `ReviewCatalog.BindToCurrentThread` -> trình bày ảnh đầu -> uncloak sau
  Present đầu -> reconcile journal (INV-6, thứ tự như `App.RecoverJournalAsync`) -> Explorer prefetch đã chạy từ đầu. Mốc perf C-18. Giai đoạn A (4-6 giờ, đi trước
  các gói đợt 3): `ShellMainWindow` khung + DI chạy được để gói khác cắm vào; giai đoạn B: phần còn lại.
- **Tạo:** `src/PhotoReview.Shell.Win32/{Program.cs,Startup/ShellStartup.cs,Startup/ShellCompositionRoot.cs,Startup/StartupCloak.cs,ShellMainWindow.cs,Startup/ForwardedOpenHandler.cs}`.
- **Phụ thuộc:** WP-09, WP-14, WP-15, WP-16, WP-19a. **Hợp đồng:** sở hữu C-16 (shell), C-18. **Xung đột:** `Shell.Win32/Program.cs`, `Startup/*`, `ShellMainWindow.cs` (gói khác
  chỉ thêm file `ShellMainWindow.<Feature>.cs` partial + đăng ký một dòng trong `ShellCompositionRoot.RegisterFeatures` - dòng thêm **cuối** danh sách).
- **Test:** `ShellStartupOrderTests` (thứ tự: lock trước early decode; reconcile sau lock và trước file action đầu - fake journal); `ShellStartupFirstShowTests` (port
  `StartupFirstShowIntegrationTests`: không khung trắng = cửa sổ cloak tới Present đầu; placement áp trước show); forward: instance 2 forward path và thoát 0 (port
  `AppForwardedOpenTests`); INV-10: log tắt -> không file log.
- **Hoàn thành khi:** ảnh đầu R2R median <= 450 ms (probe 8 vòng), cửa sổ thấy được <= 150 ms. **Rủi ro:** Cao. **Model:** opus. **Giờ:** 16-24.

### WP-21 - Trình bày ảnh, compare, crossfade
- **Tạo:** `src/PhotoReview.Shell.Win32/Viewing/{Win32PresentationSink.cs,ImageLayer.cs,CompareLayer.cs,CrossfadeLayer.cs,ShellMainWindow.Viewing.cs}`.
- **Mục tiêu:** `IPresentationSinkFactory` Win32; vẽ `CurrentImage` (`PixelBuffer`) theo `ViewportLayout` + `EffectiveScalingQuality`; `GpuImageCache.Prefetch` ảnh kế tiếp theo
  hướng duyệt; swap original (ADR 0008) không nhảy layout; compare 2 cột + viền chọn (LimeGreen/#555); crossfade Q-R37 (lớp ảnh cũ giữ khung cũ, opacity 1->0 QuadraticOut
  trong `ImageTransitionMs`, huỷ khi điều hướng mới); `TracePresented` + mốc `Presented/RenderedFrame`; `ClearCache` xoá GPU cache.
- **Phụ thuộc:** WP-20 (A), WP-15, WP-16, WP-17. **Hợp đồng:** tiêu thụ C-02, C-09, C-10, C-11. **Xung đột:** `Shell.Win32/Viewing/*` trừ file của WP-16.
- **Test:** port `ImageCrossfadeIntegrationTests` (đếm khung, opacity theo clock giả, None = 0 animation), `MainWindowZoomDetailTests`, compare chọn trái/phải; INV-1: điều hướng nhanh
  100 lần với decode trễ ngẫu nhiên -> ảnh cuối luôn đúng path (fake presenter delay); key->present S3 P95 <= 3 ms (Manual báo số).
- **Rủi ro:** Cao. **Model:** opus. **Giờ:** 16-24.

### WP-22 - Nhập liệu shell
- **Tạo:** `src/PhotoReview.Shell.Win32/Input/{PointerMessageHandler.cs,KeyboardMessageHandler.cs,WheelMessageHandler.cs,DropTarget.cs,ShellMainWindow.Input.cs}`.
- **Mục tiêu:** WM_LBUTTON*/MBUTTON*/MOUSEMOVE/MOUSEWHEEL/MOUSEHWHEEL/KEYDOWN/SYSKEYDOWN -> `PointerInputController`/`ShortcutRouter` (y như `MainWindow` WPF: arrow-pan trước,
  Esc, Space/Enter khi nút có focus, auto-repeat bị bỏ cho `IgnoresAutoRepeat`); double-click Fit (đếm click theo `GetDoubleClickTime`); capture/cursor; drag threshold
  `SM_CXDRAG`; `GetCurrentInputMessageSource` cho touchpad; kéo-thả `IDropTarget` (file đầu tiên -> `OpenPathAsync`).
- **Phụ thuộc:** WP-20 (A), WP-16. **Hợp đồng:** tiêu thụ C-06, C-07, C-15. **Xung đột:** `Shell.Win32/Input/*`.
- **Test:** G-INPUT chạy qua đường **message thật** (`SendMessage` vào HWND ẩn) khớp 100 % (khác WP-16 chạy thẳng controller); `KeyboardZoomFrameTests`, `KeepZoomAcrossImagesIntegrationTests`
  port; auto-repeat Recycle không lặp (lParam bit 30); drop với `IDataObject` giả.
- **Rủi ro:** TB-Cao. **Model:** sonnet (logic dùng chung, spec = golden). **Giờ:** 12-18.

### WP-23 - Overlay HUD, toolbar, status, auto-hide
- **Tạo:** `src/PhotoReview.Shell.Win32/Overlay/{ToolbarPanel.cs,StatusPanel.cs,FolderInfoPanel.cs,ZoomIndicatorPanel.cs,CapturePairBadge.cs,ToolsMenu.cs,ShellMainWindow.Overlay.cs}`.
- **Mục tiêu:** vị trí/margin/padding/CornerRadius/FontSize theo `MainWindow.xaml` (margin 8, padding 6,4, radius 4, `InfoOverlay.FontSize`, MaxWidth 640 cho folder info,
  badge top-center lệch 44); nội dung bind `PropertyChanged` của `MainViewModel`/`InfoOverlayViewModel`; `ToolbarAutoHidePolicy`/`InfoOverlayAutoHidePolicy` + animator
  (fade in 150/out 200 ms, hot zone 24); nút Tools mở menu Tools (C-12 `BuildToolsMenu`); tooltip (`TTM_*` native hoặc tự vẽ - chọn native); nút "View list" skipped.
- **Phụ thuộc:** WP-17, WP-18, WP-20 (A). **Hợp đồng:** tiêu thụ C-11, C-12. **Xung đột:** `Shell.Win32/Overlay/*Panel.cs` (file mới).
- **Test:** bố cục: hộp bao từng panel khớp `overlay-boxes.v1.json` (G-OVL, ghi bởi WP-10) +/- 2 px ở 3 kích thước cửa sổ, 2 DPI; auto-hide theo clock giả; click-through khi ẩn;
  i18n đổi ngôn ngữ trực tiếp -> text đổi (`Localizer.CurrentChanged`).
- **Rủi ro:** TB. **Model:** sonnet. **Giờ:** 14-20.

### WP-24 - Menu chuột phải, submenu Zoom, custom zoom
- **Tạo:** `src/PhotoReview.Shell.Win32/Menus/{ContextMenuController.cs,ShellMainWindow.Menus.cs}`; nối `IZoomPromptService` (bridge, WP-19b).
- **Mục tiêu:** chuột phải -> `ContextMenuModelBuilder.Build` -> `IPopupMenuHost.Show` -> lệnh (Undo, Recycle, Fit, Zoom to N, Zoom submenu, Refresh, folder, editor ngoài, Copy name/path,
  Settings); `SetZoomAlsoSetsClickLevel`, "Đặt mức hiện tại làm mức thu phóng khi nhấn" như `MainWindow.ApplyZoomMenuSelectionAsync`.
- **Phụ thuộc:** WP-18, WP-19b, WP-22. **Xung đột:** `Shell.Win32/Menus/ContextMenuController.cs`.
- **Test:** port `ContextMenuRedesignTests`, `ZoomContextMenuTests`, `ClickZoomCustomDialogTests` (qua seam `IPopupMenuHost` giả chọn id); G-MENU qua controller.
- **Rủi ro:** thấp. **Model:** sonnet. **Giờ:** 8-12.

### WP-25 - F11, đa màn hình, DPI, title bar, nhãn instance
- **Tạo:** `src/PhotoReview.Shell.Win32/Windowing/{ShellFullscreen.cs,ShellPlacement.cs,TitleBar.cs,ShellMainWindow.Windowing.cs}`.
- **Mục tiêu:** `IFullscreenController` (WP-08) trên HWND shell; lưu placement khi đóng (kể cả khi đang F11, R7-10); `WM_DPICHANGED` -> `ViewerState.DpiScale`,
  `ViewportSizeSource.TargetDecodeBox` (`AdaptivePreviewPolicy`, multiplier 1.15), resize surface; dark title bar + caption #171717 (`WindowsDarkTitleBar`);
  title = `FolderTitle` + nhãn instance + "beta"; restore + activate khi nhận forward.
- **Phụ thuộc:** WP-08, WP-20 (A). **Xung đột:** `Shell.Win32/Windowing/*`.
- **Test:** port `FullscreenTransitionFrameTests` (không khung trung gian sai - `tools/diag/fullscreen-capture.ps1` trên shell), `FullscreenWindowPlacerExitTests`,
  `WindowPlacementIsolationTests`; màn hình ảo: `IMonitorLayout` giả 2 màn hình, rút màn hình 2 -> exit rect hợp lệ; round-trip placement với bản WPF (file bản WPF ghi ->
  shell mở cùng vị trí, và ngược lại).
- **Rủi ro:** TB-Cao (lịch sử lỗi F11). **Model:** opus. **Giờ:** 12-16.

### WP-26 - Thanh cuộn (theo NE-3)
- **Tạo:** `src/PhotoReview.Shell.Win32/Viewing/ScrollBars.cs`. NE-3 (a): vẽ track/thumb theo `DarkScrollBars.xaml` (màu `Dark.ScrollTrack`, độ dày = golden), kéo thumb, click
  track = page, hit-test trước ảnh (như `IsInsideScrollBar`). (b)/(c): theo quyết định.
- **Phụ thuộc:** WP-16, WP-17. **Xung đột:** file trên. **Test:** G-VIEW đã gồm thanh cuộn; kéo thumb -> offset tỉ lệ; click track; pixel box so WPF +/- 1 px.
- **Rủi ro:** thấp. **Model:** sonnet. **Giờ:** 6-10.

## Đợt 4 - tương đương, perf, phát hành song song

### WP-27a - Harness test UI shell
- **Tạo:** `tests/PhotoReview.Shell.Integration.Tests/Harness/{ShellTestHost.cs,MessageInjector.cs,FrameRecorder.cs}` (khởi động `ShellMainWindow` trong tiến trình test trên STA
  thread riêng, data root tạm, `PHOTOREVIEW_ISOLATE_CONFIG`, gửi `SendMessage/PostMessage`, đọc trạng thái VM, render offscreen); luật `ShellTestsDoNotUseSendInput`.
- **Phụ thuộc:** WP-20. **Test:** chính harness có test tự kiểm (mở/đóng 50 lần không rò HWND/GDI handle). **Model:** sonnet. **Giờ:** 16-24.

### WP-27b - Port kịch bản UI + bộ kịch bản thủ công
- **Mục tiêu:** port 45 file `Category=UI` của Integration.Tests có tương ứng ở shell (danh sách mục 7.2 của kế hoạch); viết file decisions mới tên `NO-WPF-MANUAL-SCRIPT`
  (25 bước cho người dùng, mục 7.3; có frontmatter `id/order/summary`, chạy `tools/generate-open-decisions.ps1`).
- **Phụ thuộc:** WP-27a, WP-21..25. **Xung đột:** `tests/PhotoReview.Shell.Integration.Tests/Scenarios/*`. **Test:** chính là sản phẩm; mỗi test port phải fail khi mutate code tương ứng
  (ghi trong PR: mutate gì, đỏ ở đâu). **Model:** sonnet. **Giờ:** 16-24.

### WP-28 - Golden pixel G-PIX
- **Tạo:** `tests/PhotoReview.Integration.Tests/Golden/PixelGoldenRecorder.cs` (WPF `RenderTargetBitmap` của MainWindow + hộp bao panel), `tests/Fixtures/golden/pixel/*.png`
  (nhỏ, <= 5 MB tổng), `tests/PhotoReview.Shell.Tests/Golden/PixelGoldenTests.cs` (WARP).
- **Phụ thuộc:** WP-21, WP-23. **Test:** ngưỡng mục 7.3 (2). **Model:** sonnet. **Giờ:** 10-14.

### WP-29 - Perf host + probe
- **Tạo:** `tools/PhotoReview.Benchmark.Cli/ShellHost/*` (perf session S2/S3/S4 trên shell, cùng CSV), `tools/diag/startup-probe.ps1` (probe khởi động xen kẽ 2 exe, mốc C-18,
  WM_CLOSE, không SendInput), cập nhật `tune-matrix.ps1` tham số `-App Wpf|Shell`.
- **Phụ thuộc:** WP-20, WP-21. **Test:** probe chạy 2 vòng smoke trong `Category=Manual`. **Hoàn thành khi:** perf gate đợt 3 đo được và ghi `perf/<ngày>-nowpf-wave3.md`.
- **Model:** sonnet. **Giờ:** 12-18.

### WP-30 - Đóng gói hai exe, CI, liên kết Explorer
- **Sửa:** `.github/workflows/ci.yml`, `release.yml` (publish R2R cả hai exe vào một thư mục), `tools/verify-release.ps1` (kiểm cả hai exe chạy `--smoke`),
  `deploy/install-photo-review-association.ps1`/`uninstall-*.ps1` (tham số `-Exe`), Settings "Cập nhật đường dẫn" trỏ exe đang chạy.
- **Phụ thuộc:** WP-20. **Xung đột:** các file trên (`ci.yml` cũng bị WP-01 sửa - WP-30 rebase). **Test:** `TestFilterDriftTests`, verify-release trên artifact CI.
- **Model:** sonnet. **Giờ:** 8-12.

### WP-31 - Tài liệu
- **Sửa:** ADR mới `0010-win32-direct2d-shell` trong `docs/adr/` (thay 0002, 0004), `docs/architecture.md`, `docs/APP-MECHANISMS-VI.md`, `docs/TESTING.md`, `docs/INDEX.md` (giữ budget
  `docs-budget.ps1`). **Phụ thuộc:** WP-20. **Model:** haiku. **Giờ:** 4-6.

## Đợt 5 - Native AOT và cửa sổ phụ

### WP-32 - Spike S1 đo Native AOT (cần MSVC + Windows SDK)
- Theo mục 5 của NO-WPF-MIGRATION-PLAN, nhưng dùng chính shell (đã có) thay vì viewer tạm: `dotnet publish src/PhotoReview.Shell.Win32 -r win-x64 -p:PublishAot=true`; đo
  G0.1..G0.7 bằng `tools/diag/startup-probe.ps1`; ghi `perf/<ngày>-no-wpf-spike.md`. **Phụ thuộc:** NW-2 (người dùng cài toolchain); có thể chạy sớm hơn (sau WP-15) với
  `--demo`. **Model:** opus. **Giờ:** 16-24.

### WP-33 - COM shell / Recycle Bin không late-bound
- **Sửa:** `src/PhotoReview.Platform.Windows/Explorer/{ExplorerOrderService.cs,ExplorerComInterop.cs}` (`IShellWindows`/`IShellBrowser`/`IFolderView` vtable),
  `ComEnumeration.cs`, `WindowsRecycleBin.cs` (`IFileOperation` + `FOFX_RECYCLEONDELETE`, bỏ `dynamic` và `Microsoft.VisualBasic.FileIO`).
- **Ràng buộc:** **chỉ test bằng fake**; Native test chỉ tạo và xoá item của chính nó; **không mutation trên đường xoá thật** (AGENTS.md). Explorer order đo lại F4 (~60 ms).
- **Phụ thuộc:** WP-12 (mẫu COM). **Model:** opus (an toàn dữ liệu). **Giờ:** 20-30.

### WP-34 - `IsAotCompatible` toàn bộ
- Bật cho Core, Imaging*, Platform.Windows, App.Shared; xử lý IL2xxx/IL3xxx (DI, CommunityToolkit, `Enum.Parse`...). **Phụ thuộc:** WP-11, WP-12, WP-33. **Model:** sonnet. **Giờ:** 10-16.

### WP-35 - Thay cửa sổ phụ (theo NW-3)
- Kế hoạch riêng sau spike S3 (NW-3). Settings là phần lớn nhất (~60 setting, 222 chuỗi). **Model:** opus điều phối + nhiều sonnet. **Giờ:** 120-200 (ước thô).

### WP-36 - PublishAot cho `PhotoReview.exe` + CI
- `PublishAot=true` (khi NW-3 xong hoặc bỏ cửa sổ WPF khỏi bản AOT), CI publish AOT trên `windows-latest`, `verify-release.ps1` kiểm artifact AOT. Publish cục bộ cần MSVC.
- **Model:** sonnet. **Giờ:** 8-12.

## Đợt 6 - gỡ WPF

### WP-37 - Xoá bản WPF
- Sau >= 2 bản phát hành ổn định của shell và người dùng đồng ý: xoá `src/PhotoReview.App`, `Imaging.Wpf`, `Shell.WpfBridge`, `App.WpfWindows` (nếu NW-3 đã thay), `TestSupport.Windows`,
  test WPF; giữ golden JSON (shell vẫn kiểm). ADR cập nhật. **Model:** sonnet. **Giờ:** 8-12.

---

## Phụ lục A - Mẫu prompt giao việc

Lead điền `<...>`. Mọi prompt bắt đầu bằng khối **QUY TẮC CHUNG** rồi khối theo loại.

### A.0 QUY TẮC CHUNG (dán vào mọi prompt)

```text
Bạn làm gói <WP-xx> của kế hoạch docs/refactoring/decisions/NO-WPF-EXEC-PLAN.md (+ thẻ gói trong NO-WPF-EXEC-PLAN-WP.md) cho repo
C:\MyProjects\PhotoReview (.NET 10). Trả lời/ghi chú bằng tiếng Việt.

Bắt buộc:
1. Worktree riêng: git fetch origin; git worktree add .claude/worktrees/<wp-xx-ten> -b <loai>/<wp-xx-ten> origin/master. Không sửa,
   commit, pull trong checkout chính; không git stash trần.
2. Đọc: AGENTS.md, task_on_progress.md, docs/INDEX.md, AGENTS.local.md (nếu có); mục 2, 7.3 và các hợp đồng <C-..> ở mục 5 của
   NO-WPF-EXEC-PLAN.md; thẻ <WP-xx>. Không đọc cả thư mục decisions/.
3. Hợp đồng <C-..> đã ĐÓNG BĂNG. Không đổi chữ ký. Nếu thấy phải đổi: dừng, báo lead (lý do + chữ ký đề xuất), không tự sửa
   tests/PhotoReview.Architecture.Tests/Approved/contracts.v1.txt.
4. Chỉ ghi các file trong "vùng xung đột" của thẻ; cần chạm file khác thì ghi rõ hunk trong mô tả PR.
5. App WPF không được đổi hành vi nhìn thấy. Không thêm gói NuGet. Mã mới: LibraryImport/[GeneratedComInterface], không reflection/dynamic.
6. Test: luôn có hang guard. Ưu tiên tools/run-tests-hidden.ps1 <args> hoặc tools/verify-all.ps1 -Hidden. Nếu chạy dotnet test trực tiếp:
   dotnet test <proj> -c Release --settings tests/test.runsettings --blame-hang --blame-hang-timeout 120s --filter "Category!=Manual&Category!=Native&Category!=Slow"
   Không bao giờ loại Category=UI. Lệnh dài chạy nền (run_in_background), không vòng chờ không giới hạn.
7. Test phải đỏ khi mã bị hỏng: với mỗi nhánh mới, mutate thử (hoặc giao sub-agent haiku/sonnet chạy Stryker theo docs/MUTATION-TESTING.md)
   và ghi kết quả trong PR. Không test đọc source text, không assert thời gian cố định, không Task.Yield polling.
8. KHÔNG chạy mã xoá/quét/làm rỗng Thùng rác thật (kể cả mutation). Dùng fake. KHÔNG SendInput/SetForegroundWindow trong test.
9. i18n: chuỗi giao diện qua Tr.*; khoá mới thêm CUỐI src/PhotoReview.Core/Localization/Languages/en.json và vi.json, tiền tố "shell.";
   chạy tools/i18n-check.ps1. Log/CSV/journal dùng CultureInfo.InvariantCulture, không dùng Tr.
10. Build: dotnet build PhotoReview.slnx -c Release phải 0 cảnh báo, 0 lỗi. Không #pragma/editorconfig mới nếu không có lý do một dòng.
11. Không sửa task_on_progress.md, docs/ACTIVE-TASKS.md, OPEN-DECISIONS.md (trừ khi chạy generate-open-decisions.ps1 cho file decisions mới của mình),
    PERF-STATUS.md (trừ một bullet nếu gói có số đo). Số đo perf: file mới docs/refactoring/perf/<ngày>-<wp>.md.
12. Commit (dòng cuối: Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>), push, mở PR vào master, mô tả kết thúc
    "🤖 Generated with [Claude Code](https://claude.com/claude-code)". KHÔNG merge.
13. Báo cáo cuối: link PR; file đã đổi; test đã chạy (lệnh + số pass/fail/skip); mutation đã thử; số đo (nếu có) so với tiêu chí thẻ;
    điều chưa chắc; có đụng hợp đồng không.
```

### A.1 Gói rủi ro cao (opus) - WP-02/03/04/06/07/14/15/16/19b/20/21/25/32/33

```text
<QUY TẮC CHUNG>
Loại: gói rủi ro cao (<đồng thời | an toàn dữ liệu | Fit/T89 | bố cục | tích hợp>). Bạn là người điều phối và review:
- Tự viết phần lõi <liệt kê lớp lõi trong thẻ>.
- Giao cho sub-agent model sonnet: viết thêm test theo danh sách thẻ, port test; model haiku: mutation check từng file, sửa cảnh báo,
  cập nhật đường dẫn trong test kiến trúc. Sub-agent của bạn chỉ dùng sonnet/haiku; mỗi sub-agent cũng làm trong worktree của bạn hoặc
  worktree con, báo lại diff; bạn review từng diff.
- Trước khi viết mã: liệt kê bất biến bị chạm (INV-*, ADR 0005/0008, T89) và cách test chứng minh không vỡ.
- Không rút gọn vòng Fit 3 lượt. Không ConfigureAwait(false) ở tầng UI. Không chặn đồng bộ trên Task ở tầng UI.
Tiêu chí hoàn thành (đo được): <copy từ thẻ>.
```

### A.2 Gói tính năng có spec (sonnet) - WP-05/08/09/10/11/12/13b/17/18/19a/22/23/24/26/27a/27b/28/29/30/34/36/37

```text
<QUY TẮC CHUNG>
Loại: tính năng có spec. Spec = thẻ <WP-xx> + golden <G-..> (tests/Fixtures/golden/...). Golden là chuẩn: nếu mã đúng spec mà lệch golden,
dừng và báo lead (không sửa golden). Có thể giao sub-agent haiku cho việc cơ học (đổi đường dẫn, mutation check từng file).
Tiêu chí hoàn thành: <copy từ thẻ>.
```

### A.3 Gói cơ học (haiku) - WP-13a/31

```text
<QUY TẮC CHUNG>
Loại: cơ học. Làm đúng danh sách trong thẻ, không thêm logic. Gặp chỗ mơ hồ: ghi vào báo cáo, không đoán.
```

### A.4 Khảo sát chỉ đọc (Explore) - dùng trước khi giao gói hoặc khi review

```text
Read-only. Repo C:\MyProjects\PhotoReview (worktree <...>). Tìm <grep/đếm cụ thể, ví dụ: mọi chỗ dùng System.Windows trong src/PhotoReview.App.Shared>.
Trả: bảng file:dòng | API | dùng để làm gì; tổng đếm. Không đề xuất sửa.
```

## Phụ lục B - Checklist review và hợp nhất của lead

Cho **mỗi PR gói**:

1. `git fetch`; PR base là `origin/master` hiện tại (không stacked); `git merge-base --is-ancestor` kiểm các PR phụ thuộc đã vào master.
2. Diff chỉ trong vùng xung đột của thẻ (+ hunk đã khai báo). `git diff --stat origin/master...<head>`.
3. `ContractSurfaceTests` xanh và `Approved/contracts.v1.txt` **không đổi** (trừ PR hợp đồng của lead).
4. Tự build lại: `dotnet build PhotoReview.slnx -c Release` 0 cảnh báo; chạy `tools/verify-all.ps1 -Hidden` (gồm `Category=UI`); không tin con số "N test pass" của agent.
5. Golden: `GoldenWpfConformanceTests` (bản WPF) và test golden của shell xanh; không có file golden bị sửa ngoài WP-10.
6. Mutation: PR ghi đủ "mutate gì -> test nào đỏ"; lead thử lại ngẫu nhiên 1-2 mutation.
7. Bất biến: rà diff cho `ConfigureAwait(false)` ở UI, `.Result/.Wait()`, `Dispatcher.Invoke`/`GetRequiredService` rộng, `IgnoreInaccessible`, `SendInput`, đường Thùng rác thật,
   `JsonSerializer` không context trong mã mới, chuỗi tiếng Việt trong `.cs`.
8. Dữ liệu người dùng: nếu chạm định dạng file (config/session/journal/cache/placement) - phải có test round-trip hai chiều.
9. Perf: gói có tiêu chí perf thì có file `docs/refactoring/perf/...` với lô xen kẽ >= 8 vòng, CPU nền, median [min-max].
10. Docs: không sửa `task_on_progress.md`/`ACTIVE-TASKS.md`; nếu thêm file decisions thì đã chạy `generate-open-decisions.ps1`, `check-open-decisions.ps1`,
    `docs-budget.ps1 -Check`, `check-doc-links.ps1`.

Ở **mỗi điểm hợp nhất** (I-1a, I-1b, I-1c, I-2a, I-3, I-4, I-5):

1. Thứ tự merge theo mục 6; khi hai nhánh chạm cùng file (dự kiến: `MainWindow.xaml.cs` giữa WP-07/WP-08; `ci.yml` giữa WP-01/WP-30) lead tự làm một PR tích hợp và giải xung đột.
2. Sau khi người dùng merge: đồng bộ checkout chính (`git fetch` + `git merge --ff-only origin/master`) và build Release theo `AGENTS.local.md`, báo version.
3. Chạy perf gate của đợt (mục 7.4), ghi file perf mới, một bullet `PERF-STATUS.md` trong PR docs-sync.
4. Cập nhật `task_on_progress.md`/`ACTIVE-TASKS.md` trong **PR docs-sync** riêng (không trong PR gói).
5. Trước đợt 3: có kết quả NE-3..NE-9; trước đợt 5: NW-2/NW-3.
6. Hỏi người dùng xem bằng mắt ở cuối đợt 3 (kịch bản thủ công WP-27b) trước khi mở beta.
