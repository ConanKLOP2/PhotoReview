using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Localization;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Windowing;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Instance;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Platform.Windows;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>Tuỳ chọn khởi động; mặc định = chạy thật. Test thay từng seam (không đụng dữ liệu người dùng).</summary>
internal sealed record ShellStartupOptions
{
    /// <summary>
    /// Prefix mutex/pipe của instance (NE-6). Mặc định <see cref="ShellInstanceNames.Prefix"/>; biến môi trường
    /// <c>PHOTOREVIEW_SHELL_INSTANCE_PREFIX</c> đổi nó (chẩn đoán/test tiến trình, để không forward nhầm cho cửa sổ đang dùng).
    /// </summary>
    public string InstanceNamePrefix { get; init; } = InstancePrefixFromEnvironment();

    /// <summary>Seam cloak DWM: (hwnd, cloaked) -> được chấp nhận. Null = <c>WindowsDwmCloak</c>.</summary>
    public Func<nint, bool, bool>? SetCloaked { get; init; }

    /// <summary>WP-25: seam F11/monitor/DWM của cửa sổ chính (test giả lập màn hình thứ hai). Null = Win32 thật.</summary>
    public ShellWindowingSeams? Windowing { get; init; }

    /// <summary>WP21-STALL: cấu hình giữ GPU ấm (test). Null = biến môi trường <c>PHOTOREVIEW_GPU_KEEPWARM*</c>, mặc định bật.</summary>
    public PhotoReview.Shell.Win32.Viewing.KeepWarmOptions? KeepWarm { get; init; }

    /// <summary>WP21-STALL: seam việc giữ ấm (test giả, không cần GPU thật). Null = thiết bị offscreen riêng trên GPU thật.</summary>
    public Func<PhotoReview.Shell.Rendering.IGpuKeepAliveWork?>? KeepWarmWork { get; init; }

    private static string InstancePrefixFromEnvironment() =>
        Environment.GetEnvironmentVariable("PHOTOREVIEW_SHELL_INSTANCE_PREFIX") is { Length: > 0 } value ? value : ShellInstanceNames.Prefix;

    /// <summary>Đăng ký thêm/đè lên đồ thị DI sau <c>AddPhotoReviewShared</c> (test: <c>IAppPaths</c> riêng, journal giả...).</summary>
    public Action<IServiceCollection>? ServiceOverrides { get; init; }

    /// <summary>Nơi ghi mốc perf (C-18). Mặc định <see cref="PhotoReviewPerf.StartupMark"/>.</summary>
    public Action<string>? Mark { get; init; }

    /// <summary>WARP thay vì GPU (<c>PHOTOREVIEW_SHELL_WARP=1</c>, RDP, test).</summary>
    public bool PreferWarp { get; init; } = Environment.GetEnvironmentVariable("PHOTOREVIEW_SHELL_WARP") == "1";

    /// <summary>Lấy focus khi hiện cửa sổ (smoke = false luôn).</summary>
    public bool Activate { get; init; } = true;

    /// <summary>Làm nóng đồ hoạ trên pool từ đầu tiến trình (tắt bằng <c>PHOTOREVIEW_SHELL_NOWARM=1</c> để đo A/B).</summary>
    public bool WarmGraphics { get; init; } = Environment.GetEnvironmentVariable("PHOTOREVIEW_SHELL_NOWARM") != "1";

    /// <summary>Gắn handler ngoại lệ toàn tiến trình (AppDomain/Task). Test in-process tắt.</summary>
    public bool InstallProcessHooks { get; init; } = true;

    /// <summary>Bật listener CSV perf theo <c>PHOTOREVIEW_PERF_TRACE</c>.</summary>
    public bool StartPerfListener { get; init; } = true;

    /// <summary>
    /// Thay việc reconcile journal (INV-6): mặc định <see cref="JournalStartupRecovery.RunAsync"/>. Test chèn journal giả để
    /// ghi thứ tự.
    /// </summary>
    public Func<IServiceProvider, Task<IReadOnlyList<JournalEntry>>>? JournalRecovery { get; init; }

    /// <summary>Gọi (UI thread) khi cửa sổ chính đã dựng xong, trước khi mở ảnh đầu. Test dùng để quan sát/đóng.</summary>
    public Action<ShellMainWindow>? OnMainWindowReady { get; init; }

    /// <summary>Hạn chót chờ khung đầu ở chế độ smoke trước khi đóng cưỡng bức (mã thoát <see cref="ShellStartup.ExitSmokeTimeout"/>).</summary>
    public TimeSpan SmokeTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Điểm vào startup của PhotoReview.exe (NO-WPF-EXEC-PLAN C-16/C-18, thẻ WP-20): thay <c>App.StartupCoreAsync</c> bằng vòng
/// thông điệp Win32, không có WPF <c>Application</c>. Trình tự (mốc perf cùng tên bản WPF):
/// <list type="number">
/// <item><c>appStartup</c>; DPI PerMonitorV2; làm nóng parser config + đồ hoạ trên pool; <see cref="ShellLaunch"/>.</item>
/// <item>Dispatcher + <see cref="MessageLoop"/> của UI thread; mọi bước sau chạy như một việc async trên UI thread.</item>
/// <item>DI (<c>servicesBuilt</c>); prefetch Explorer; prefetch placement; đọc config + ngôn ngữ trên pool
/// (<c>settingsFileLoaded</c>, <c>languageLoaded</c>) rồi <c>settingsLoaded</c>.</item>
/// <item><see cref="InstanceScope"/> (prefix NE-6) + forward (<c>instanceLock</c>): bản thứ hai chuyển đường dẫn và thoát 0.</item>
/// <item>Decode sớm (<c>earlyDecodeStarted</c>) CHỈ SAU khi giữ khoá: bản bị từ chối không bao giờ decode.</item>
/// <item>Cửa sổ ẩn + cloak + placement trước <c>ShowWindow</c> (<c>placementRestoredBeforeShow</c>, <c>windowShown</c>),
/// view model + bề mặt D2D (<c>d3dDeviceReady</c>, <c>mainWindowConstructed</c>), khung đầu (<c>firstPresent</c>,
/// <c>windowRevealed</c>), ảnh đầu (<c>Assign</c>, <c>Presented</c>, <c>RenderedFrame</c>).</item>
/// <item>Reconcile journal (INV-6) SAU khoá và SAU khi cửa sổ hiện, TRƯỚC khi vòng thông điệp nhận input đầu tiên.</item>
/// </list>
/// </summary>
internal sealed class ShellStartup : IDisposable
{
    /// <summary>Mã thoát: khởi động hỏng (đã hiện hộp thoại lỗi).</summary>
    internal const int ExitStartupFailed = 1;

    /// <summary>Mã thoát smoke: không có khung đầu trong <see cref="ShellStartupOptions.SmokeTimeout"/>.</summary>
    internal const int ExitSmokeTimeout = 7;

    /// <summary>Explorer order prefetch bắt đầu sớm ~1 s trước khi folder load hỏi; tải vẫn tự chặn bằng timeout 2 s của nó.</summary>
    private static readonly TimeSpan ExplorerPrefetchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Explorer chạy N tiến trình cho N file chọn: chúng forward trong một loạt ngắn và gộp thành một lần mở.</summary>
    private static readonly TimeSpan ForwardCoalesceWindow = TimeSpan.FromMilliseconds(750);

    private const int MaxLoopFaults = 50;

    private readonly string[] _args;
    private readonly ShellLaunch _launch;
    private readonly ShellStartupOptions _options;
    private readonly Action<string> _mark;
    private readonly JsonWindowPlacementStore _placementStore = new();
    private Win32UiDispatcher? _dispatcher;
    private MessageLoop? _loop;
    private ServiceProvider? _services;
    private InstanceScope? _instanceScope;
    private ForwardedOpenCoalescer? _coalescer;
    private ForwardedOpenHandler? _forwardHandler;
    private ShellMainWindow? _main;
    private StartupCloak? _cloak;
    private TimerFrameClock? _frameClock;
    private PerfCsvListener? _perf;
    private bool _hasPendingForward;
    private string? _pendingForwardPath;
    private int _exitCode;
    private int? _forcedExit;
    private long _startTimestamp;
    private ShellStartup(string[] args, ShellLaunch launch, ShellStartupOptions options)
    {
        _args = args;
        _launch = launch;
        _options = options;
        _mark = options.Mark ?? PhotoReviewPerf.StartupMark;
    }

    /// <summary>Chạy đến khi cửa sổ chính đóng; trả mã thoát. Gọi trên thread STA chính (nó sở hữu dispatcher + vòng lặp).</summary>
    public static int Run(string[] args, ShellStartupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var opts = options ?? new ShellStartupOptions();
        var launch = ShellLaunch.Parse(args);
        using var startup = new ShellStartup(args, launch, opts);
        return startup.RunLoop();
    }

    public void Dispose() => Cleanup();

    private int RunLoop()
    {
        // perf(startup): listener CSV chỉ phụ thuộc môi trường nên chạy trước; mốc msSinceProcessStart bao cả phần sau.
        _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(); // vào Main (không đọc StartTime của tiến trình: chậm)
        _perf = _options.StartPerfListener ? PerfCsvListener.TryStartFromEnvironment() : null;
        _mark("appStartup");
        DpiAwarenessBootstrap.EnsurePerMonitorV2();
        _ = ShellWarmup.WarmSettingsParser();
        if (_options.WarmGraphics)
        {
            _ = ShellWarmup.WarmGraphics();
        }

        using var dispatcher = new Win32UiDispatcher(FileLog.Default);
        using var syncContext = Win32UiSynchronizationContext.Install(dispatcher);
        _dispatcher = dispatcher;
        _loop = new MessageLoop(dispatcher);
        dispatcher.Post(() => _ = RunStartupGuardedAsync());

        var faults = 0;
        while (true)
        {
            try
            {
                _exitCode = _loop.Run();
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Như DispatcherUnhandledException.Handled = true của bản WPF: ghi (kể cả khi log tắt) rồi chạy tiếp, nhưng
                // một lỗi lặp vô hạn thì thoát thay vì quay mãi.
                LogUnhandledForced("UI loop exception", ex);
                if (++faults >= MaxLoopFaults)
                {
                    _exitCode = ExitStartupFailed;
                    break;
                }
            }
        }

        if (_forcedExit is { } forced)
        {
            _exitCode = forced;
        }

        Cleanup();
        return _exitCode;
    }

    private async Task RunStartupGuardedAsync()
    {
        // R2-F-04: một lỗi ở đây trước kia để lại tiến trình không cửa sổ giữ mutex. Bắt, báo, thoát sạch.
        try
        {
            await StartupCoreAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailStartup(ex);
        }
    }

    private void FailStartup(Exception ex)
    {
        try
        {
            AppLog.ErrorForced("Startup failed", ex);
        }
        catch
        {
            // Ghi log không được chặn việc thoát.
        }

        try
        {
            // Dịch vụ có thể chính là thứ hỏng: không phụ thuộc DI để báo lỗi.
            var dialogs = _services?.GetService<IDialogService>();
            if (dialogs is null)
            {
                var picker = new Dialogs.FolderPicker(Dialogs.UnwiredShellFolderDialog.Instance, static () => 0);
                dialogs = new Dialogs.Win32DialogService(static () => 0, picker, new Lazy<Dialogs.ISecondaryWindowPort?>(() => null));
            }

            dialogs.ShowError(Tr.AppTitle, Tr.AppStartupFailed(ex.Message));
        }
        catch
        {
            // Không có UI: vẫn thoát bên dưới.
        }

        _forcedExit = ExitStartupFailed; // Window.Closed (khi Dispose cửa sổ) Quit(0) trước: mã lỗi phải thắng
        _main?.Dispose();
        _main = null;
        MessageLoop.Quit(ExitStartupFailed);
    }

    private async Task StartupCoreAsync()
    {
        var dispatcher = _dispatcher!;
        var hub = new ShellPresentationHub();
        var host = new ShellHostServices(dispatcher, () => _main?.Hwnd ?? 0);
        var services = ShellCompositionRoot.Build(host, hub, _options.ServiceOverrides);
        _services = services;
        _mark("servicesBuilt");

        var log = services.GetRequiredService<ILog>();
        var paths = services.GetRequiredService<IAppPaths>();

        // perf(startup): thứ tự xem của Explorer là phần chậm nhất khi mở một ảnh (~1-2 s COM xuyên tiến trình cho thư mục lớn):
        // bắt đầu ngay, song song với settings/cửa sổ; folder load nối vào truy vấn này thay vì tự chạy.
        if (!string.IsNullOrEmpty(_launch.LaunchFolder))
        {
            services.GetRequiredService<IExplorerOrderProvider>().Prefetch(_launch.LaunchFolder, ExplorerPrefetchTimeout);
        }

        // P-1: placement đọc trên pool ngay; cửa sổ áp nó trước khi hiện và decode sớm dự đoán viewport từ nó.
        var placementFile = paths.WindowPlacementFile;
        var placementLoad = _launch.Smoke
            ? Task.FromResult<WindowPlacementData?>(null)
            : _placementStore.Prefetch(placementFile);
        Task<EarlyDecodePlan?>? earlyDecode = null;

        var store = services.GetRequiredService<SettingsStore>();
        store.Changed += (_, settings) => AppLog.Enabled = settings.LoggingEnabled || DiagOptions.ForceLog;
        var localization = services.GetRequiredService<LocalizationService>();
        localization.Mode = LocalizationService.ParseMode(_args);
        var initial = _launch.InitialFile;
        var settingsLoad = Task.Run(() =>
        {
            var loaded = store.Load();
            _mark("settingsFileLoaded");
            // P-1: dịch vụ preview (probe decoder, cache) dựng và viewport dự đoán song song với ngôn ngữ và cửa sổ; chính
            // decode chỉ bắt đầu khi instance này đã giữ khoá.
            if (initial is not null)
            {
                earlyDecode = Task.Run(() => PrepareEarlyDecodeAsync(services, placementLoad));
            }

            var localizer = localization.Load(loaded.UiLanguage);
            _mark("languageLoaded");
            return (Settings: loaded, Localizer: localizer);
        });
        // C-16: đầu vào của startup shell (cho gói tính năng và chẩn đoán): đối số, settings, placement, mốc bắt đầu tiến trình.
        Context = new ShellStartupContext(_launch.Arguments, _launch.InitialPath, _launch.LaunchFolder,
            ReadSettingsAsync(settingsLoad), placementLoad,
            _startTimestamp);
        var (appSettings, localizer) = await settingsLoad;
        localization.Apply(appSettings.UiLanguage, localizer);
        _mark("settingsLoaded");
        AppLog.Enabled = appSettings.LoggingEnabled || DiagOptions.ForceLog;
        if (AppLog.Enabled)
        {
            AppLog.Info($"Startup (shell) args={string.Join(" | ", _args)}");
        }

        if (DiagOptions.AnyEnabled)
        {
            AppLog.WriteForced(() => AppLog.Error($"DIAG MODE: {DiagOptions.Describe()}"));
        }

        if (_options.InstallProcessHooks)
        {
            // R2-F-12: đây là báo cáo crash nên được ghi (và flush) kể cả khi log tắt.
            AppDomain.CurrentDomain.UnhandledException += (_, a) => LogUnhandledForced("AppDomain exception", a.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, a) =>
            {
                LogUnhandledForced("Unobserved task exception", a.Exception);
                a.SetObserved();
            };
        }

        // Q-R18: chế độ instance lấy từ settings vừa đọc, tức TRƯỚC mọi khoá; đổi trong Settings áp dụng ở lần chạy sau.
        _coalescer = new ForwardedOpenCoalescer(path => dispatcher.Post(() => OnForwardedOpen(path)), ForwardCoalesceWindow, log);
        if (!_launch.Smoke)
        {
            _instanceScope = new InstanceScope(appSettings.InstanceMode, _coalescer.Submit, log, _options.InstanceNamePrefix,
                allowServerForeground: true);
            if (!_instanceScope.TryAcquire(_launch.LaunchFolder))
            {
                // Q-R10: giao yêu cầu cho instance đang giữ thư mục này (SingleWindow: cả app) thay vì báo lỗi; không có
                // đường dẫn thì chủ chỉ được đưa ra trước. Không ai trả lời trong timeout (mutex mồ côi) thì báo như trước.
                var forwarded = await SecondInstanceHandoff.TryForwardAsync(
                    _instanceScope.CreateClient(_launch.LaunchFolder), _launch.ExistingPaths, SecondInstanceHandoff.DefaultTimeout, log);
                if (!forwarded)
                {
                    services.GetRequiredService<IDialogService>().ShowMessage(
                        Tr.AppTitle,
                        _instanceScope.Mode == PhotoReview.Core.Model.InstanceMode.PerFolder
                            ? Tr.FolderAlreadyOpenInOtherInstance
                            : Tr.AppAlreadyRunningNoResponse);
                }

                _exitCode = 0;
                MessageLoop.Quit(0);
                return;
            }
        }

        if (_instanceScope is not null)
        {
            _mark("instanceLock");
        }

        if (earlyDecode is not null && initial is not null)
        {
            StartEarlyDecode(services, earlyDecode, initial, appSettings);
        }

        CreateMainWindow(services, hub, paths, placementFile, store, log);
        var main = _main!;
        _options.OnMainWindowReady?.Invoke(main);

        // Mở ảnh đầu: lỗi vào log, không phá khởi động.
        var open = main.OpenInitialAsync(_launch.InitialPath, isFile: _launch.InitialFile is not null);
        _ = ObserveAsync(open, "Open initial path failed", log);

        if (_hasPendingForward)
        {
            // Một bản thứ hai forward trong lúc ta còn đang khởi động: xử lý ngay khi cửa sổ sẵn sàng (bản WPF bỏ rơi).
            _hasPendingForward = false;
            var path = _pendingForwardPath;
            _pendingForwardPath = null;
            OnForwardedOpen(path);
        }

        if (!_launch.Smoke)
        {
            _ = RecoverJournalAsync(services, main);
        }

        if (store.LastLoadRepairs.Count > 0)
        {
            services.GetRequiredService<IDialogService>().ShowMessage(Tr.AppTitle, SettingsLoadRepairText.Build(store.LastLoadRepairs));
        }
    }

    private void CreateMainWindow(ServiceProvider services, ShellPresentationHub hub, IAppPaths paths, string placementFile,
        SettingsStore store, ILog log)
    {
        _ = paths;
        var dispatcher = _dispatcher!;
        var activate = _options.Activate && !_launch.Smoke;
        var displayClock = services.GetRequiredService<IDisplayClock>();
        _frameClock = new TimerFrameClock(displayClock);
        _loop!.AddFrameClock(_frameClock);
        var window = new ShellWindow(
            new ShellWindowOptions(Tr.AppTitle, ShellMainWindow.DefaultWidthDip, ShellMainWindow.DefaultHeightDip, SizeIsOuter: true),
            _frameClock);
        window.Closed += (_, _) => MessageLoop.Quit(0);

        // perf/startup-first-image: không bao giờ thấy bề mặt chưa vẽ (trắng) và không bao giờ thấy cửa sổ 1200x800 rồi mới nhảy
        // tới placement: cloak từ lúc có HWND, placement áp khi cửa sổ chưa hiện, rồi mới ShowWindow.
        _cloak = new StartupCloak(dispatcher, onRevealed: () => _mark("windowRevealed"), setCloaked: _options.SetCloaked);
        _cloak.Hide(window.Hwnd);
        WindowShowState? state = null;
        if (!_launch.Smoke)
        {
            state = ShellPlacement.ApplyBeforeShow(window.Hwnd, ReadPlacement(placementFile, log), _placementStore.IsVisible,
                NativeWindowPlacement.Instance, log);
            if (state is not null)
            {
                _mark("placementRestoredBeforeShow");
            }
        }

        window.Show(ShellPlacement.ShowCommandFor(state, activate));
        _mark("windowShown");

        var viewModel = services.GetRequiredService<MainViewModel>();
        viewModel.FolderOwnership = _instanceScope;
        // Xác minh thủ công/agent: PHOTOREVIEW_DIAG_INSTANCE_LABEL đánh dấu thanh tiêu đề để không nhầm với cửa sổ thật.
        if (DiagOptions.InstanceLabel is { } label)
        {
            viewModel.InstanceLabel = label;
        }

        _main = new ShellMainWindow(window, dispatcher, _frameClock, viewModel, services.GetRequiredService<ViewportSizeSource>(), hub,
            _cloak, _placementStore, NativeWindowPlacement.Instance, displayClock, log, _mark,
            new ShellMainWindowOptions(_options.PreferWarp, _launch.Smoke ? null : placementFile, activate,
                SettingsStore: store, ZoomPrompt: services.GetService<IZoomPromptService>(), Windowing: _options.Windowing,
                KeepWarm: _options.KeepWarm, KeepWarmWork: _options.KeepWarmWork));
        _forwardHandler = new ForwardedOpenHandler(_main, log);
        _main.AttachRenderer();
        _main.AttachOverlay(services, _frameClock); // WP-23: HUD có mặt từ khung đầu
        _mark("mainWindowConstructed");

        // Khung đầu NGAY (canvas): cửa sổ được bỏ cloak sau Present đầu thay vì chờ ảnh (thẻ: cửa sổ thấy được <= 150 ms).
        _main.RenderNow();

        if (_launch.Smoke)
        {
            ArmSmokeExit(_main);
        }
    }

    /// <summary>Smoke: đóng ngay sau khung có ảnh (hoặc khung đầu nếu không có ảnh để mở); quá hạn chót thì đóng với mã riêng.</summary>
    private void ArmSmokeExit(ShellMainWindow main)
    {
        var closing = 0;
        void CloseOnce(int exitCode)
        {
            if (Interlocked.Exchange(ref closing, 1) != 0)
            {
                return;
            }

            _forcedExit = exitCode;
            _dispatcher!.Post(() =>
            {
                if (!main.Window.IsDestroyed)
                {
                    main.Window.Dispose();
                }

                MessageLoop.Quit(exitCode);
            });
        }

        if (_launch.InitialPath is null)
        {
            main.FirstPresented += (_, _) => CloseOnce(0);
            if (main.FirstPresentDone)
            {
                CloseOnce(0);
            }
        }
        else
        {
            main.FirstImagePresentedEvent += (_, _) => CloseOnce(0);
        }

        var timeout = new Timer(_ => CloseOnce(ExitSmokeTimeout), null, _options.SmokeTimeout, Timeout.InfiniteTimeSpan);
        main.Window.Closed += (_, _) => timeout.Dispose();
    }

    private WindowPlacementData? ReadPlacement(string placementFile, ILog log)
    {
        try
        {
            return _placementStore.TakePrefetched(placementFile) ?? JsonWindowPlacementStore.ReadRaw(placementFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            log.Error("Could not read the window placement", ex);
            return null;
        }
    }

    /// <summary>P-1: những gì decode sớm của ảnh dòng lệnh cần, chuẩn bị trên pool trong lúc settings/cửa sổ nạp.</summary>
    private sealed record EarlyDecodePlan(PreviewImageService Previews, DecodeBox Box);

    private static async Task<AppSettings> ReadSettingsAsync(Task<(AppSettings Settings, Localizer Localizer)> load) => (await load).Settings;

    /// <summary>Đầu vào startup (C-16): đặt sau khi dịch vụ dựng xong, trước khi chờ settings.</summary>
    internal ShellStartupContext? Context { get; private set; }

    private static async Task<EarlyDecodePlan?> PrepareEarlyDecodeAsync(IServiceProvider services, Task<WindowPlacementData?> placementLoad)
    {
        try
        {
            // Chạy trong Task.Run (không có sync context): phần tiếp tục ở lại pool.
            var placement = await placementLoad;
            var box = InitialViewportPredictor.PredictDecodeBox(placement, ShellMainWindow.DefaultWidthDip,
                ShellMainWindow.DefaultHeightDip, ShellMainWindow.PreviewQualityMultiplier);
            if (box is null)
            {
                return null;
            }

            return new EarlyDecodePlan(services.GetRequiredService<PreviewImageService>(), box.Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Không bao giờ làm hỏng khởi động: cửa sổ tự bắt đầu decode khi hiện.
            AppLog.Error("Early decode preparation failed", ex);
            return null;
        }
    }

    private void StartEarlyDecode(IServiceProvider services, Task<EarlyDecodePlan?> plan, string path, AppSettings settings)
    {
        var viewport = services.GetRequiredService<ViewportSizeSource>();
        // Task.Run: phần tiếp tục chạy trên pool ngay khi kế hoạch sẵn sàng (không xếp sau UI thread đang dựng cửa sổ).
        _ = Task.Run(async () =>
        {
            if (await plan is not { } p)
            {
                return;
            }

            viewport.StartupPrediction = p.Box;
            if (InitialImagePrewarm.Start(p.Previews, settings, path, p.Box) is not null)
            {
                _mark("earlyDecodeStarted");
            }
        });
    }

    /// <summary>
    /// INV-6 / ADR 0003: reconcile các thao tác journal dở dang. Chạy SAU khoá instance và SAU khi cửa sổ hiện; bản thân việc
    /// chạy trên pool nên không làm chậm ảnh đầu. Thao tác phải đánh dấu Failed được báo một lần kèm đề nghị mở Recovery.
    /// </summary>
    private async Task RecoverJournalAsync(IServiceProvider services, ShellMainWindow main)
    {
        try
        {
            var failed = _options.JournalRecovery is { } custom
                ? await custom(services)
                : await JournalStartupRecovery.RunAsync(
                    services.GetRequiredService<OperationJournal>(), services.GetRequiredService<IClock>(), services.GetRequiredService<ILog>());
            if (failed.Count == 0)
            {
                return;
            }

            var dialogs = services.GetRequiredService<IDialogService>();
            if (dialogs.ShowConfirmation(Tr.DialogStartupRecoveryFailedTitle, Tr.DialogStartupRecoveryFailedMessage(failed.Count)))
            {
                main.ViewModel.ShowRecovery();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("Startup journal recovery failed", ex);
        }
    }

    /// <summary>Chạy trên UI thread (được đăng qua <see cref="IUiScheduler"/>): không đường dẫn = chỉ đưa cửa sổ ra trước.</summary>
    private void OnForwardedOpen(string? path)
    {
        if (_forwardHandler is null)
        {
            // Cửa sổ chưa dựng xong: nhớ yêu cầu (đường dẫn đầu tiên thắng) để áp ngay sau đó.
            if (!_hasPendingForward)
            {
                _hasPendingForward = true;
                _pendingForwardPath = path;
            }
            else
            {
                _pendingForwardPath ??= path;
            }

            return;
        }

        _ = ObserveAsync(_forwardHandler.HandleAsync(path), "Forwarded open failed", _services?.GetService<ILog>());
    }

    private static async Task ObserveAsync(Task task, string message, ILog? log)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Error(message, ex);
        }
    }

    private static void LogUnhandledForced(string message, object? exceptionObject)
    {
        try
        {
            AppLog.ErrorForced(message, exceptionObject as Exception ?? new InvalidOperationException(exceptionObject?.ToString() ?? "unknown exception object"));
        }
        catch
        {
            // Một logger hỏng không được biến lỗi đã xử lý thành lỗi khác.
        }
    }

    /// <summary>
    /// Dọn theo thứ tự ngược khởi tạo (như <c>App.Dispose</c>): ngừng lắng nghe pipe -> cửa sổ + bề mặt -> container (SessionWriter
    /// chờ flush tối đa 2 s, cache) -> log -> khoá instance -> listener perf.
    /// </summary>
    private void Cleanup()
    {
        _instanceScope?.StopListening();
        Interlocked.Exchange(ref _coalescer, null)?.Dispose();
        Interlocked.Exchange(ref _main, null)?.Dispose();
        _cloak?.Dispose();
        _frameClock?.Dispose();
        // APP-01: container giải phóng mọi singleton nó tạo; thứ đăng ký theo giá trị (FileLog.Default) do AppLog.Shutdown lo.
        Interlocked.Exchange(ref _services, null)?.Dispose();
        AppLog.Shutdown(); // sau pipe/coalescer để cảnh báo tắt của chúng còn vào log
        Interlocked.Exchange(ref _instanceScope, null)?.Dispose();
        Interlocked.Exchange(ref _perf, null)?.Dispose();
    }
}
