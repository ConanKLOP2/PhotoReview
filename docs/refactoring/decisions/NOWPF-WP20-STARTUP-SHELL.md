---
id: NOWPF-WP20
order: 710
summary: |-
  WP-20 (đợt 3 NO-WPF, 2026-10-12): composition root + startup shell. PhotoReview.exe (Shell.Win32) là gói ĐẦU TIÊN chạy thật: Program -> ShellStartup (DPI PerMonitorV2, đối số, InstanceScope prefix NE-6 PhotoReviewShell + forward qua pipe, decode sớm SAU khoá, cửa sổ cloak + placement TRƯỚC ShowWindow, DI = AddPhotoReviewShared với PixelBufferImageCodec + dịch vụ Win32, bề mặt D2D, khung đầu -> bỏ cloak, ảnh đầu vẽ ngay lượt gán, reconcile journal sau khoá + sau khi hiện). ShellMainWindow nối ViewportController + GpuImageCache với MainViewModel (chưa input/overlay/menu: WP-21..26 cắm bằng partial + một dòng RegisterFeatures). Ảnh đầu R2R: median 542 ms (517-581, 8 vòng, file 11,7 MB thứ 1300 của F4) so với bản WPF cùng điều kiện 818-1005 ms; cửa sổ thấy được ~330 ms từ lúc tạo tiến trình (KHÔNG đạt 150 ms của thẻ: xem mục 3). Lệch thẻ: giai đoạn A+B gộp một PR (chưa có toolbar/menu/overlay/chuột/phím), Occluded tính là đã present, SwapChainFrameClock chưa dùng (TimerFrameClock), thư mục-picker native chưa nối.
---

# NOWPF-WP20 - composition root + startup shell (2026-10-12)

Thẻ: WP-20 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); hợp đồng C-16 (phần shell) và C-18 trong [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md).
Tiền đề đã merge: WP-09 (App.Shared + `AddPhotoReviewShared`), WP-14 (cửa sổ/dispatcher/vòng thông điệp), WP-15 (renderer D2D), WP-16
(ViewportController), WP-19a (dialog/clipboard). App WPF (`PhotoReview.App`) **không đổi**; exe Win32 là sản phẩm song song, không thay
liên kết `.jpg` (NE-2 = a, prefix single-instance riêng NE-6). Không cài toolchain, không Native AOT.

## 1. Đã làm

| File (`src/PhotoReview.Shell.Win32`) | Nội dung |
|---|---|
| `Program.cs` | `[STAThread] Main` -> `ShellStartup.Run(args)`. Không WPF `Application`. `BootstrapWindow` (khung WP-01) bị xoá; `--smoke` chạy đúng đường thật. |
| `Startup/ShellStartup.cs` | Trình tự khởi động (mục 2), vòng thông điệp, dọn theo thứ tự ngược khởi tạo (như `App.Dispose`), mã thoát. Seam để test: prefix instance, override DI, mốc perf, WARP, journal giả, cloak, hook cửa sổ sẵn sàng. |
| `Startup/ShellLaunch.cs` | Đối số: `LaunchArguments.Normalize`, file đầu tiên tồn tại > thư mục, thư mục khoá = thư mục của file (R2-F-08), `--smoke`. `ShellInstanceNames.Prefix = "PhotoReviewShell"` (NE-6). |
| `Startup/ShellCompositionRoot.cs` | C-16: `AddPhotoReviewShared(Codec = PixelBufferImageCodec.Instance, WpfFallbackDecoder = null)` + `IUiScheduler/IUiDispatcher` (= `Win32UiDispatcher`), `IClipboardService`, `IFolderPicker`, `IDialogService` (= `Win32DialogService`, cửa sổ phụ qua `ISecondaryWindowPort` nếu WP-19b đăng ký), `IPresentationSinkFactory`. `RegisterFeatures` = điểm cắm cuối danh sách (đăng ký sau thắng). |
| `Startup/StartupCloak.cs` | Cloak DWM từ lúc có HWND tới Present đầu; hạn chót an toàn 3 s; DWM từ chối thì không đổi gì. |
| `Startup/ShellPlacement.cs` | `WindowPlacementRules` + `IsVisible` trên HWND: `SetWindowPlacement(SW_HIDE)` rồi `ShowWindow(Normal/Maximized)`; Maximized ngoài màn hình vẫn mở Maximized; lưu khi đóng (`ResolveShowCommand`). |
| `Startup/ForwardedOpenHandler.cs` | Bản Win32 của `App.OpenForwarded` + việc nhớ yêu cầu tới lúc đang khởi động (bản WPF bỏ rơi). |
| `Startup/DpiAwarenessBootstrap.cs` | Lưới an toàn PerMonitorV2 khi tiến trình không có manifest của exe. |
| `Startup/ShellPresentationSink.cs` | Hub + sink + factory tối thiểu: ảnh hiện tại (`PixelBuffer`) và mốc `Rendered/Presented/RenderedFrame` (phát ngay nếu ảnh đã nằm trong khung đã Present). WP-21 thay bằng `Win32PresentationSink`. |
| `Startup/ShellWarmup.cs` | Làm nóng parser config (P-1) và đồ hoạ (thiết bị D3D offscreen 1x1 trên pool) từ đầu tiến trình. |
| `ShellMainWindow.cs`, `ShellMainWindow.Frame.cs` | Sở hữu `ShellWindow` + `D2DRenderSurface` + `GpuImageCache` + `ViewportController`; thay binding XAML (`ContentFromViewer`, `UpdateFitSize`, hộp decode), đóng hoãn khi file action đang chạy (R7-7), `CloseSession`, vẽ canvas `#101010` + ảnh theo layout. |
| `Hosting/ShellWindow.cs` (sửa nhỏ) | `ShellWindowOptions.SizeIsOuter` (1200x800 của XAML là cỡ NGOÀI) và `Show(int showCommand)`. |
| `Shell.Interop/Win32/StartupAdditions.cs` | CHỈ THÊM: `SetProcessDpiAwarenessContext`, `AreDpiAwarenessContextsEqual`, `IsIconic`, `SetForegroundWindow`. |
| `PhotoReview.Shell.Win32.csproj` | `PublishReadyToRun` (như bản WPF). |

## 2. Trình tự và mốc perf (C-18)

`appStartup` -> DPI + làm nóng (pool) -> dispatcher/vòng thông điệp -> DI `servicesBuilt` -> prefetch Explorer + placement (pool) ->
config + ngôn ngữ trên pool (`settingsFileLoaded`, `languageLoaded`) -> `settingsLoaded` -> `InstanceScope` + forward `instanceLock` ->
(plan decode sớm xong) `earlyDecodeStarted` -> cửa sổ ẩn + cloak + placement (`placementRestoredBeforeShow`) -> `windowShown` ->
`MainViewModel` + bề mặt D2D (`d3dDeviceReady`, `swapChainReady`) -> `mainWindowConstructed` -> khung đầu (canvas) `firstPresent` +
`windowRevealed` -> `openPathBegin`, `earlyDecodeBoxMatched`, `initialDecodeStarted` -> `Assign`, `Presented`, `RenderedFrame` ->
reconcile journal (INV-6). `instanceLock` chỉ phát khi thật sự có khoá (smoke không khoá).

Bất biến kiểm bằng test: decode sớm SAU khoá (bản bị từ chối không bao giờ decode); cloak TRƯỚC `ShowWindow`, bỏ cloak SAU Present đầu;
placement TRƯỚC `ShowWindow`; reconcile SAU khoá và SAU khi hiện, TRƯỚC việc Normal đầu tiên của vòng thông điệp; log tắt thì không tạo file
(INV-10); đóng sạch (không rò USER/GDI object, handle không tăng).

## 3. Lệch thẻ / quyết định cần lead biết

1. **Một PR cho giai đoạn A+B.** Khung tối thiểu nhưng đã chạy thật: ảnh đầu từ đối số, vẽ qua renderer D2D, đóng sạch. CHƯA có toolbar,
   menu, overlay, chuột/phím, F11, kéo-thả, thanh cuộn (WP-21..26). Hiện chưa điều hướng được ảnh bằng tay (không có input).
2. **Cửa sổ thấy được ~330 ms từ lúc tạo tiến trình (~190 ms sau `Main`), thẻ ghi <= 150 ms: CHƯA ĐẠT.** Lý do cấu trúc: chế độ instance
   (SingleWindow/PerFolder) nằm trong `config.json` nên khoá chỉ lấy được sau khi đọc config (~140 ms), và thẻ yêu cầu khoá TRƯỚC cửa sổ để bản
   thứ hai không nháy cửa sổ. Phương án (b): dựng + present canvas trong lúc config đang đọc nhưng chỉ bỏ cloak sau khoá (~-55 ms, đủ rẻ);
   (c): bỏ cloak ngay sau Present đầu rồi mới lấy khoá (đạt ~150 ms nhưng bản thứ hai nháy một cửa sổ tối vài trăm ms, Explorer chọn N file thì
   N lần). Mặc định giữ (a) hiện tại. **CHƯA XÁC NHẬN** - cần chủ dự án chọn.
3. **Occluded tính là đã present.** DXGI trả `DXGI_STATUS_OCCLUDED` khi bị che/thu nhỏ và LUÔN trên desktop không tương tác (runner ẩn).
   Không tính thì cloak chờ hạn chót 3 s và mọi test cửa sổ đứng im. Khi hết che, `WM_PAINT` -> `Invalidate` vẽ lại.
4. **`TimerFrameClock`, chưa `SwapChainFrameClock`.** `ShellWindow` nhận đồng hồ lúc tạo (trước swap chain). Khung đầu và ảnh đầu vẽ đồng bộ
   (`RenderNow`), các khung sau theo nhịp timer neo vblank. Chuyển sang waitable của swap chain là việc của WP-21 khi bật animation.
5. **Chọn thư mục native chưa nối**: `UnwiredShellFolderDialog` (WP-19a) vẫn trả null; COM `IFileOpenDialog` đã có ở Shell.Interop. Việc của
   WP-23/24 (nút/menu Open folder).
6. **Ba biến môi trường chẩn đoán** (không phải cờ sản phẩm, NO-WPF-EXEC-PLAN 7.1): `PHOTOREVIEW_SHELL_WARP=1`,
   `PHOTOREVIEW_SHELL_NOWARM=1` (đo A/B làm nóng đồ hoạ), `PHOTOREVIEW_SHELL_INSTANCE_PREFIX` (test tiến trình không forward nhầm cho cửa sổ
   đang dùng).
7. **Hợp đồng v1**: không đổi chữ ký (`ShellStartupContext` được dựng và giữ ở `ShellStartup.Context`; mọi kiểu mới đều `internal`).
8. **Placement đa màn hình khác DPI** (WM_DPICHANGED trong lúc `SetWindowPlacement`) giữ như WP-14; việc của WP-25 (cần máy hai monitor).

## 4. Kiểm chứng

- Build `PhotoReview.slnx` Release: 0 cảnh báo / 0 lỗi.
- `Shell.Tests` (unit, không HWND): `ShellLaunchTests` (9), `StartupCloakTests` (7), `ShellPlacementTests` (20), `DpiAwarenessBootstrapTests` (4),
  `ForwardedOpenHandlerTests` (6), `ShellPresentationHubTests` (6), `ShellCompositionRootTests` (4), `ShellSharedGraphTests` (1: JPEG qua đồ thị
  dùng chung ra `PixelBuffer`).
- `Shell.Integration.Tests` (`ShellStartup` THẬT trong tiến trình test, HWND trên desktop ẩn, WARP, dữ liệu tạm): ảnh đầu + thứ tự mốc, decode
  sớm sau khoá, cloak, placement Normal/Maximized (áp trước show, lưu khi đóng), reconcile (thứ tự + hộp thoại Recovery), forward bản thứ hai
  (thoát 0, không dựng cửa sổ, bản đầu mở file được forward), log tắt không file, smoke, mất thiết bị rồi vẽ lại, đóng sạch không rò.
- Tiến trình thật: `PhotoReview.exe --smoke` (đã có từ WP-01) và probe R2R (mục 5).
- Đột biến thủ công: mục 6.

## 5. Số đo ảnh đầu (chi tiết: [perf/2026-10-12-wp20-startup-shell.md](../perf/2026-10-12-wp20-startup-shell.md))

PLACEHOLDER-PERF

## 6. Đột biến

PLACEHOLDER-MUTATION

## 7. Việc còn lại / bàn giao cho WP-21..26

- WP-21: đăng ký `IPresentationSinkFactory` mới trong `RegisterFeatures` (một dòng cuối); `ShellMainWindow.Frame.cs` thay `DrawContent`
  (crossfade, compare, `GpuImageCache.Prefetch/Retain`); `ShellPresentationHub.NoteFramePresented(containedImage)` giữ mốc `RenderedFrame`.
- WP-22: handler thông điệp qua `ShellWindow.AddMessageHandler`; `ViewportControllerOptions.DragMetricsPixels/CursorScreenPixel`.
- WP-23: overlay vẽ sau `DrawContent` (cùng khung, trong `Present()`); toolbar cần `FolderPicker` native (mục 3.5).
- WP-25: F11 (`ShellPlacement.Save(fullscreenRestoreState)` đã nhận trạng thái trước F11), `WM_DPICHANGED` đa màn hình.
- WP-19b: đăng ký `ISecondaryWindowPort` (singleton) là đủ để `Win32DialogService` mở cửa sổ phụ.
- Publish R2R cục bộ để đo: `dotnet publish src/PhotoReview.Shell.Win32 -c Release -r win-x64 --self-contained false`.
