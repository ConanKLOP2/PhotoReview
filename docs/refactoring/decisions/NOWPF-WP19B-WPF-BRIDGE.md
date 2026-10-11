---
id: NOWPF-WP19B
order: 730
summary: |-
  WP-19b (đợt 2 của NO-WPF-EXEC-PLAN, 2026-10-12): dời 8 cửa sổ phụ WPF + Themes + TrExtension + Converters sang library `PhotoReview.App.WpfWindows` (dùng chung với app WPF qua `SecondaryWindowFactory`; `WpfDialogService` và `MainWindow` dùng lớp mới qua DI, hành vi không đổi) và thực thi C-13b ở `PhotoReview.Shell.WpfBridge`: `Application` WPF lười (OnExplicitShutdown, không Run) + Dispatcher WPF lồng trong message loop Win32, owner = HWND shell, modal bằng `ShowDialog`, modeless (Benchmark) nhận phím nhờ `ComponentDispatcher.RaiseThreadMessage` ở `IRawMessageFilter` của `MessageLoop`; WPF chỉ nạp khi mở cửa sổ phụ đầu tiên (probe tiến trình sạch). Spike: Dispatcher WPF sống được trong vòng lặp Win32; mở lần đầu ~470-690 ms, lần sau ~100-215 ms (build chưa R2R). Hợp đồng v1.2 không đổi (thêm `ISecondaryWindowMessagePump`, ngoài danh sách khoá).
---

# NOWPF-WP19B - `App.WpfWindows` + `Shell.WpfBridge` (cửa sổ phụ WPF nạp muộn) (2026-10-12)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (C-13b, NE-5 = a), thẻ WP-19b trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Tiền đề: [NOWPF-WP09](NOWPF-WP09-APP-SHARED.md) (App.Shared), [NOWPF-WP14](NOWPF-WP14-WINDOW-LOOP.md) (MessageLoop, điểm móc `IMessageFilter`),
[NOWPF-WP19A](NOWPF-WP19A-DIALOGS.md) (`ISecondaryWindowPort`, `Win32DialogService`).

## 1. Đã làm

| Phần | Nội dung |
|---|---|
| `src/PhotoReview.App.WpfWindows` (mới, UseWPF) | `git mv`: `SettingsWindow`, `RecoveryWindow` (+`RecoveryPathPanel`, `RecoveryPresenter`), `BenchmarkWindow`, `DiagnosticsWindow`, `SkippedFilesWindow`, `BatchReviewWindow`, `ActionProfilesWindow`, `ClickZoomCustomDialog`, `Themes/*`, `TrExtension`, `ViewerStretchModeConverter`, `DarkTitleBarChrome`. Namespace giữ nguyên (`PhotoReview.App[.Services/.Localization/.Converters]`); URI tài nguyên đổi `/PhotoReview.App.WpfWindows;component/Themes/...`; `MainWindow.xaml` thêm `;assembly=PhotoReview.App.WpfWindows` cho hai xmlns. |
| `Services/SecondaryWindowFactory.cs` (WpfWindows) | Dựng cửa sổ từ `IServiceProvider` (logic cũ của `WpfDialogService`): `CreateSettings/Recovery/Diagnostics/Benchmark/SkippedFiles/BatchReview`, `PromptCustomZoom(Window|HWND)`. Chỉ tạo cửa sổ; modal/modeless và owner do bên gọi quyết định. |
| App | `WpfDialogService` = host WPF + factory (cùng hành vi: owner = MainWindow, `IDialogHost` seam giữ). `WpfZoomPromptService : IZoomPromptService` đăng ký ở `App.ConfigureServices`; `MainWindow.OpenClickZoomCustomDialogAsync` gọi `IZoomPromptService` (tham số ctor tuỳ chọn, mặc định `new WpfZoomPromptService(() => this)`). |
| `Shell.WpfBridge` | `WpfSecondaryWindowHost : ISecondaryWindowHost, ISecondaryWindowMessagePump, IDisposable` (internal) qua `SecondaryWindowHostFactory.Create(services)`; `WpfSession` (static) chứa mọi chạm WPF. `ISecondaryWindowMessagePump` (public mới): `HasModelessWindows`, `PreFilterMessage(hwnd,msg,wParam,lParam,time,x,y)`. |
| `Shell.Win32` | `Dialogs/WpfBridgeLoader.cs` (kiểu DUY NHẤT chạm WpfBridge, `: ISecondaryWindowPort, IRawMessageFilter, IDisposable`, `Create` NoInlining), `Dialogs/SecondaryWindowPorts.cs` (`CreateLazy`/`TryLoad`, bắt lỗi nạp assembly -> null), `Dialogs/Win32ZoomPromptService.cs` (`IZoomPromptService` qua cổng), `ISecondaryWindowPort.PromptCustomZoom` (thành viên thứ 7), `MessageLoop`: `IRawMessageFilter` (truyền `Msg` đủ time/pt) + `MessageFilterCount`; csproj thêm ProjectReference WpfBridge + IVT. |
| Tests | Dự án mới `tests/PhotoReview.Shell.WpfBridge.Tests` (UseWPF, desktop ẩn): mở/đóng từng cửa sổ phụ qua cầu trên UI thread thật, modeless Benchmark + phím Tab, lưu Settings -> `Changed`, probe nạp muộn (exe tự viết, tiến trình sạch). `MessageLoopFilterTests` (Shell.Integration.Tests), `Win32ZoomPromptService` (Shell.Tests), `ZoomPromptSeamTests` (Integration.Tests). Luật L-WPF (UseWPF chỉ 4 project) + hướng phụ thuộc WpfBridge/WpfWindows; AR19, XamlThemeRules, LocalizationGuard, AppThreadAffinity quét thêm `App.WpfWindows`. CI/verify-all/coverage thêm project test; `verify-release.ps1` và `ToolScriptTests` thêm `PhotoReview.App.WpfWindows.dll`. |

## 2. Spike (4 giờ đầu của gói)

Câu hỏi của kế hoạch: WPF `Dispatcher` có sống được trong vòng lặp Win32 modeless của WP-14 không, tài nguyên/theme nạp thế nào?

1. **Sống được.** `new Application { ShutdownMode = OnExplicitShutdown }` (không `Run`) trên UI thread của shell tạo Dispatcher WPF gắn thread đó. Dispatcher gửi WM_* vào cửa sổ ẩn của nó nên
   `DispatchMessage` trong `MessageLoop.Run` đủ để layout/render/timer WPF chạy. `Window.ShowDialog()` chạy vòng lặp WPF lồng riêng trên thread; trong lúc đó `Win32UiDispatcher` (WM_APP+1)
   vẫn được dispatch (đã ghi ở WP-14). Cửa sổ modal vô hiệu hoá cửa sổ chủ (WPF `EnableThreadWindows`), đóng bằng `PostMessage(WM_CLOSE)`.
2. **Phím của cửa sổ modeless**: thử nghiệm cho thấy phím thường (Tab, di chuyển focus) vẫn chạy qua WndProc của `HwndSource` ngay cả khi KHÔNG chuyển message cho
   `ComponentDispatcher` (mutation bỏ hook sống sót với test "Tab đổi focus"). Hook vẫn được giữ vì `Dispatcher.Run` luôn làm vậy và các consumer của
   `ComponentDispatcher.ThreadPreprocessMessage/ThreadFilterMessage` (nội dung interop/IKeyboardInputSink, accelerator) chỉ nhận message qua đường này. Đã hiện thực bằng
   `IRawMessageFilter` ở `MessageLoop` (cần `time`/`pt` của MSG mà `WindowMessage` của C-15 không mang; không đổi hợp đồng), chỉ gọi khi có cửa sổ modeless (`HasModelessWindows`).
   Kiểm: test Tab + quan sát `ComponentDispatcher.ThreadPreprocessMessage` thấy WM_KEYDOWN (đây là phép kiểm hợp đồng của cầu, giết mutation M01/M02).
3. **`Application.Current` gắn với thread tạo nó** và chỉ có một cho cả tiến trình (không tạo lại được sau khi shutdown): một UI thread (đúng với shell) và KHÔNG `Shutdown()` khi đóng cửa sổ cuối;
   test dùng một UI thread chung cho cả tiến trình.
4. **Tài nguyên/theme**: mỗi cửa sổ phụ tự merge `Themes/DarkControls.xaml` qua `Window.Resources` (pack URI trong assembly `App.WpfWindows`), không phụ thuộc `Application.Resources`
   (`App.xaml` rỗng) -> không cần nạp resource dictionary vào `Application`; `TrExtension` bind `LocalizationSource.Instance` (App.Shared). Không có `Application.Current.Resources[...]` trong cửa sổ phụ.
5. **Nạp muộn**: assembly cầu nạp khi đọc `Lazy.Value` đầu tiên (nhẹ, không có kiểu WPF trong chữ ký); `PresentationFramework/PresentationCore/WindowsBase/System.Xaml/App.WpfWindows`
   chỉ nạp lúc mở cửa sổ phụ đầu tiên (probe: danh sách rỗng ở `start`, `lazy-created`, `dialog-service-created`, `port-loaded`).
6. **Chi phí**: shell `PhotoReview.exe` nay có `FrameworkReference` WindowsDesktop (chuyển tiếp qua ProjectReference WpfBridge): `runtimeconfig.json` khai thêm framework, hostfxr đọc thêm
   deps.json (ảnh hưởng startup shell: đo ở WP-20/WP-29, bản WPF không bị).

## 3. Số đo

Chi tiết: [perf/2026-10-12-wp19b-wpf-bridge.md](../perf/2026-10-12-wp19b-wpf-bridge.md).

- Startup bản WPF (R2R publish, 10 vòng xen kẽ, CPU ~22 %): median 1228 -> 1252 ms = **+1,9 %** (min ngang nhau) - không hồi quy > 5 %.
- Mở cửa sổ phụ qua cầu (build chưa R2R): lần đầu 470-690 ms (mốc 600), lần sau Diagnostics 100-160 ms, Settings 170-215 ms (mốc 150: **chưa đạt** cho Settings).
- Số assembly WPF nạp trước/sau cửa sổ phụ đầu tiên (probe tiến trình sạch): 0 -> 6 (`PresentationCore`, `PresentationFramework`, `PresentationFramework-SystemXml`, `PresentationFramework.Aero2`,
  `System.Xaml`, `WindowsBase`) + `PhotoReview.App.WpfWindows`; assembly cầu nạp lúc đọc `Lazy.Value`.

## 3b. Mutation thủ công (20 đột biến, sonnet-free: script `mutate.ps1` ở scratchpad, worktree riêng, build + test desktop ẩn)

Đột biến: bỏ chuyển message cho ComponentDispatcher (M01, M02), không đếm/giảm số cửa sổ modeless (M03, M04), bỏ owner khi modal (M05) và khi zoom prompt (M16),
Benchmark thành modal (M06), `Application` không `OnExplicitShutdown` (M07), bỏ kiểm thread affinity (M08), Dispose không đóng cửa sổ (M09) / không đặt `_disposed` (M10) /
không gỡ bộ lọc (M12), nạp WPF ngay ở ctor host (M11) và `Lazy` thành eager (M15), `MessageLoop` bỏ nhánh raw (M13), `Win32ZoomPromptService` bỏ owner (M14),
`MainWindow` hỏi mức cố định (M17) / bỏ áp kết quả (M18), `WpfDialogService.ShowBenchmark` modal (M19), bỏ focus ô editor ngoài (M20).
Lượt đầu: 16/20 bị diệt; 4 sống sót không tương đương (M01, M02: Tab chạy cả khi không có hook; M17: mức hiện tại mặc định 100 trùng hằng; M20: không test factory) -> thêm
quan sát `ComponentDispatcher.ThreadPreprocessMessage`, đặt `ClickZoomPercent = 175`, test `SettingsFactory_FocusesTheExternalEditorBoxOnlyForThatTarget`; M13 lỗi build ở lượt đầu
(đột biến sửa lại). Lượt hai: 5/5 bị diệt -> 20/20.

## 4. Lệch khỏi thẻ / điểm cần nối

1. `SecondaryWindowHostFactory` chuyển sang file riêng (cùng namespace, chữ ký như C-13b); thêm `ISecondaryWindowMessagePump` public ngoài danh sách khoá (không đổi `Approved/contracts.v1.txt`).
2. `ISecondaryWindowPort` (internal, WP-19a) thêm `PromptCustomZoom`. WP-20 nối: `var ports = SecondaryWindowPorts.CreateLazy(services, messageLoop)`; `new Win32DialogService(owner, picker, ports)`;
   `new Win32ZoomPromptService(owner, ports)` đăng ký làm `IZoomPromptService`; khi thoát gọi `Dispose` của `WpfBridgeLoader` (`ports.Value as IDisposable`) trước khi huỷ cửa sổ chủ.
3. Chưa có: `IFileOpenDialog` cho `PickFolder` (WP-13b), nút "Browse" trong Benchmark vẫn dùng `OpenFolderDialog` của WPF (không đổi).
4. Hộp thoại lỗi đọc journal (Recovery, RV-A14) trong shell là `MessageBoxW` có owner = HWND shell (bản WPF giữ `MessageBox` WPF).
5. Giới hạn: một UI thread, một `Application` WPF mỗi tiến trình; host gắn với thread tạo và ném `InvalidOperationException` khi gọi từ thread khác.
