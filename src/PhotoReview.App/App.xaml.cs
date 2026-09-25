using System.Windows;
using System.IO;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Diagnostics;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;
using PhotoReview.Core.IO;
using PhotoReview.Platform.Windows;
using PhotoReview.Core.Settings;

namespace PhotoReview.App;

public partial class App : System.Windows.Application, IDisposable
{
    private InstanceLock? _instanceLock;
    private IInstanceForwardServer? _forwardServer;
    private ForwardedOpenCoalescer? _forwardCoalescer;
    private PerfCsvListener? _perfListener;
    private PerfDispatcherHooks? _perfHooks;
    private IServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services not initialized.");

    public static void ConfigureServices(IServiceCollection services)
    {
        // 1. Core Abstractions
        services.AddSingleton<IAppPaths>(_ => PhotoReview.Core.AppPaths.FromEnvironment());
        // Metadata queries are counted into ReviewMetrics (StatCount) for the diagnostics/benchmark reports.
        services.AddSingleton<IFileSystem>(sp => new CountingFileSystem(new PhysicalFileSystem(), sp.GetRequiredService<ReviewMetrics>()));
        services.AddSingleton<IClock, SystemClock>();
        // AR02c/AR02b: FileLog.Default is a process-wide static singleton whose Shutdown/Dispose
        // lifecycle is owned by the process (App.Dispose / PerfSession's own AppLog.Shutdown()), not by
        // any one composition-root's ServiceProvider. A container that disposes itself (Benchmark.Cli
        // building/disposing a fresh graph per iteration, and AR02b's per-window ServiceProvider) must
        // not also dispose this shared instance -- registering the already-constructed instance (instead
        // of a factory returning it) opts it out of container-owned disposal.
        services.AddSingleton<ILog>(FileLog.Default);

        // 2. Settings & Session
        services.AddSingleton<SettingsStore>(sp => new SettingsStore(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<ILog>(),
            LogStartupErrorForced));
        services.AddSingleton<SessionStore>(sp => new SessionStore(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<ReviewMetrics>()));
        services.AddSingleton<SessionWriter>(sp => new SessionWriter(sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<ILog>()));

        // I18N (ADR 0006): a plain file system on purpose -- catalog reads must not count in ReviewMetrics read budgets.
        services.AddSingleton<PhotoReview.App.Localization.LocalizationService>(sp => new PhotoReview.App.Localization.LocalizationService(
            sp.GetRequiredService<IAppPaths>(),
            new PhysicalFileSystem(),
            sp.GetRequiredService<ILog>()));

        // 3. Journal & File Actions
        services.AddSingleton<OperationJournal>(sp => new OperationJournal(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            () => sp.GetRequiredService<SettingsStore>().Current.JournalDurability));
        services.AddSingleton<RecoveryRetryService>(sp => new RecoveryRetryService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>()));
        services.AddSingleton<FileHashService>(sp => new FileHashService(
            sp.GetRequiredService<SourceBytesCachePolicy>().Cache));
        services.AddSingleton<FileActionService>(sp => new FileActionService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IRecycleBin>(),
            moveOverride: GetMoveOverride(sp)));
        services.AddSingleton<UndoService>(sp => new UndoService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IRecycleBin>(),
            sp.GetRequiredService<FileActionService>(),
            moveOverride: GetMoveOverride(sp)));

        // 4. Diagnostics & Metrics
        services.AddSingleton<ReviewMetrics>();

        // 5. Platform Services
        services.AddSingleton<IExplorerOrderProvider, ExplorerOrderService>();
        services.AddSingleton<IRecycleBin>(_ => WindowsRecycleBin.Instance);
        services.AddSingleton<IMemoryProbe>(_ => PhysicalMemory.Instance);
        services.AddSingleton<INaturalComparer>(_ => WindowsNaturalComparer.Instance);
        services.AddSingleton<IKeyNameValidator, WpfKeyNameValidator>();
        services.AddSingleton<IUiScheduler>(_ => new DispatcherUiScheduler(Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
        services.AddSingleton<IDialogService, PhotoReview.App.Services.WpfDialogService>();
        services.AddSingleton<PhotoReview.App.Services.ViewportSizeSource>();
        services.AddSingleton<PhotoReview.App.Services.IPresentationObserver>(_ => PhotoReview.App.Services.NullPresentationObserver.Instance);

        // 6. Imaging & Decoding
        services.AddSingleton<IImageDecoderFactory>(sp =>
        {
            var log = sp.GetService<ILog>();
            return new ImageDecoderFactory(Composition.DecoderProviders.Create(log), log, sp.GetService<ReviewMetrics>());
        });
        services.AddSingleton<ThumbnailCache>(sp => new ThumbnailCache(
            diskDirectory: sp.GetRequiredService<IAppPaths>().ThumbnailCacheDir,
            persistNewThumbnails: false,
            log: sp.GetService<ILog>(),
            sourceBytesCache: sp.GetRequiredService<SourceBytesCachePolicy>().Cache));
        services.AddSingleton<SourceBytesCachePolicy>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>().Current;
            if (!settings.UseSourceBytesCache) return new SourceBytesCachePolicy(null);
            // RAM%: the source-bytes cache must leave the preload window to the preview cache inside the user's share.
            var sourceBytes = RamBudgetPolicy.SourceBytesForPercent(
                settings.SourceBytesCapacityBytes, settings.ImageCacheRamPercent, RamBudgetPolicy.GetPhysicalMemoryBytes());
            if (sourceBytes <= 0)
            {
                sp.GetService<ILog>()?.Warn("Source-bytes cache disabled: the RAM cache share leaves no room beyond the preview preload window.");
                return new SourceBytesCachePolicy(null);
            }
            return new SourceBytesCachePolicy(new SourceBytesCache(sourceBytes));
        });

        services.AddSingleton<PreviewStateContext>();
        services.AddSingleton<PreviewImageService>(sp =>
        {
            var ctx = sp.GetRequiredService<PreviewStateContext>();
            var settingsStore = sp.GetRequiredService<SettingsStore>();
            // Also lost in T46d: without this the "Original" loading mode still decoded previews.
            ctx.IsOriginalLoadingMode = () => settingsStore.Current.LoadingMode == PhotoReview.Core.Model.LoadingMode.Original;
            ctx.CurrentBackend = () => settingsStore.Current.DecoderBackend;
            // T46d dropped the viewport-based decode width, so Preview decoded every image at full size.
            // perf(decode): the target is now a width x height box (see AdaptivePreviewPolicy).
            var viewport = sp.GetRequiredService<PhotoReview.App.Services.ViewportSizeSource>();
            ctx.TargetDecodeBox = () => viewport.TargetDecodeBox;
            return new PreviewImageService(
                sp.GetRequiredService<ReviewMetrics>(),
                () => ctx.IsOriginalLoadingMode(),
                () => ctx.TargetDecodeBox(),
                capacityBytes: settingsStore.Current.ImageCacheCapacityBytes,
                diskCacheDirectory: sp.GetRequiredService<IAppPaths>().PreviewCacheDir,
                decoderFactory: sp.GetRequiredService<IImageDecoderFactory>(),
                currentBackend: () => ctx.CurrentBackend(),
                log: sp.GetService<ILog>(),
                sourceBytesCache: sp.GetRequiredService<SourceBytesCachePolicy>().Cache,
                cacheRamPercent: settingsStore.Current.ImageCacheRamPercent);
        });

        services.AddSingleton<Func<Func<CatalogEntry[]>, Func<long>, PreloadScheduler>>(sp =>
            (getEntries, getTotalBytes) =>
            {
                var settingsStore = sp.GetRequiredService<SettingsStore>();
                var sourceBytesCache = sp.GetRequiredService<SourceBytesCachePolicy>().Cache;
                var previewService = sp.GetRequiredService<PreviewImageService>();
                return new PreloadScheduler(
                previewService,
                sp.GetRequiredService<ReviewMetrics>(),
                getEntries,
                getTotalBytes,
                fullFolderRamThresholdBytes: previewService.CapacityBytes, // effective (clamped) budget, R2-A-05
                memoryLoadLimit: sp.GetRequiredService<SettingsStore>().Current.PreloadMemoryLoadLimit,
                memoryProbe: sp.GetRequiredService<IMemoryProbe>(),
                workerCountOverride: sp.GetRequiredService<SettingsStore>().Current.PreloadWorkerCount,
                log: sp.GetService<ILog>(),
                prefetchSourceBytes: sourceBytesCache is not null
                    ? (path, token) => Task.Run(() => sourceBytesCache.GetOrRead(path), token)
                    : null);
            });

        // 7. ViewModels & Coordinators
        services.AddTransient<PhotoReview.App.ViewModels.ViewerState>();
        services.AddTransient<PhotoReview.App.ViewModels.CompareViewModel>();
        services.AddTransient<PhotoReview.Core.Catalog.ReviewCatalog>();
        services.AddTransient<PhotoReview.Core.Catalog.GenerationClock>();
        services.AddTransient<PhotoReview.App.ViewModels.MainViewModel>(sp => Composition.MainViewModelCompositionRoot.Create(sp));

        // 8. Window
        services.AddTransient<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<PhotoReview.App.Services.ViewportSizeSource>(),
            sp.GetRequiredService<IExplorerOrderProvider>()));
    }

    /// <summary>
    /// AR02a step 6 (F-move-seam): production registers no <see cref="PhotoReview.App.Coordinators.IMoveOverride"/>,
    /// so <see cref="FileActionService"/>/<see cref="UndoService"/> get a <c>null</c> override and move files for
    /// real. A DI override (test-only, from AR02b onward) can register one to intercept the move step.
    /// </summary>
    private static Func<string, string, Task>? GetMoveOverride(IServiceProvider sp)
    {
        var moveOverride = sp.GetService<PhotoReview.App.Coordinators.IMoveOverride>();
        return moveOverride is null ? null : moveOverride.MoveAsync;
    }

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        // R2-F-04: this is an async void handler installed before the unhandled-exception hooks exist, and the default
        // ShutdownMode only ends the process when the last window closes. Any fault here used to leave a windowless
        // process holding the instance mutex, so it is caught and turned into a message plus a clean shutdown.
        try
        {
            await StartupCoreAsync(e);
        }
        catch (Exception ex)
        {
            FailStartup(ex);
        }
    }

    private void FailStartup(Exception ex)
    {
        try { LogStartupErrorForced("Startup failed", ex); } catch { /* logging must not block the shutdown */ }
        try
        {
            // The service provider may itself be the thing that failed, so fall back to a fresh dialog service.
            (_services?.GetService<IDialogService>() ?? new PhotoReview.App.Services.WpfDialogService(_services!)).ShowError(
                PhotoReview.Core.Localization.Tr.AppTitle,
                PhotoReview.Core.Localization.Tr.AppStartupFailed(ex.Message));
        }
        catch { /* no UI available: still shut down below */ }
        try { Dispose(); } catch { /* release what we can; the process exits next */ }
        Shutdown(1);
    }

    private async Task StartupCoreAsync(StartupEventArgs e)
    {
        // perf(startup): the CSV listener depends on nothing but the environment, so it starts first
        // and the Startup milestones below (msSinceProcessStart) cover services/settings/window too.
        _perfListener = PerfCsvListener.TryStartFromEnvironment();
        PhotoReviewPerf.StartupMark("appStartup");
        PhotoReview.App.Services.WpfKeyNameValidator.WireUp();

        _services = Composition.AppHost.BuildServices();
        PhotoReviewPerf.StartupMark("servicesBuilt");

        var initial = e.Args.FirstOrDefault(arg => File.Exists(arg));
        var initialFolder = e.Args.FirstOrDefault(arg => Directory.Exists(arg));
        var lockFolder = initial is not null ? Path.GetDirectoryName(Path.GetFullPath(initial)) : initialFolder; // R2-F-08: a relative file argument has an empty directory name
        // perf(startup): Explorer's view order is the slowest part of opening a photo (~1-2 s of
        // cross-process COM for a large folder). Start it now, in parallel with settings, window
        // construction and Show(); the folder load joins this query instead of starting its own.
        var explorerFolder = initial is not null ? Path.GetDirectoryName(Path.GetFullPath(initial)) : initialFolder;
        if (!string.IsNullOrEmpty(explorerFolder))
            _services.GetRequiredService<IExplorerOrderProvider>().Prefetch(explorerFolder, ExplorerPrefetchTimeout);

        var store = _services.GetRequiredService<SettingsStore>();
        store.Changed += (_, settings) => AppLog.Enabled = settings.LoggingEnabled;
        // perf(startup): config.json (IO + JSON metadata, ~100 ms) is read on the thread pool while the
        // UI thread is blocked connecting to WPF's render thread (~350 ms, it would otherwise happen
        // inside MainWindow's InitializeComponent). Nothing reads the settings in between; the await
        // normally finds the load finished and continues synchronously, without a dispatcher yield.
        // I18N: the UI language (a few small JSON files) is read in the same worker hop and published below,
        // before any window exists.
        var localization = _services.GetRequiredService<PhotoReview.App.Localization.LocalizationService>();
        localization.Mode = PhotoReview.App.Localization.LocalizationService.ParseMode(e.Args);
        var settingsLoad = Task.Run(() =>
        {
            var loaded = store.Load();
            return (Settings: loaded, Localizer: localization.Load(loaded.UiLanguage));
        });
        PhotoReview.App.Services.StartupWarmup.ConnectRenderThread();
        PhotoReviewPerf.StartupMark("renderThreadConnected");
        var (appSettings, localizer) = await settingsLoad;
        localization.Apply(appSettings.UiLanguage, localizer);
        PhotoReviewPerf.StartupMark("settingsLoaded");
        AppLog.Enabled = appSettings.LoggingEnabled;
        if (AppLog.Enabled) AppLog.Info($"Startup args={string.Join(" | ", e.Args)}");
        // D05: PHOTOREVIEW_DIAG_* variables change app behavior for measurement purposes, so their
        // presence must be visible in the log even when logging is otherwise disabled -- same reasoning
        // as AppSettings.LogStartupErrorForced.
        if (DiagOptions.AnyEnabled) LogDiagModeForced();
        if (_perfListener is not null) AppLog.Info("Perf trace enabled (PHOTOREVIEW_PERF_TRACE)");
        if (_perfListener is not null)
        {
            // D04 perf: dispatcher hooks and the DIAG flag summary exist only while the CSV listener runs.
            _perfHooks = PerfDispatcherHooks.Attach(Dispatcher);
            PerfDispatcherHooks.TraceDiagMode();
        }
        // R2-F-12: these are crash reports, so they are recorded (and flushed) even while logging is off;
        // otherwise the swallowed exception, or a fatal one before the process dies, leaves no trace.
        DispatcherUnhandledException += (_, a) => { LogUnhandledForced("Dispatcher exception", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogUnhandledForced("AppDomain exception", a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { LogUnhandledForced("Unobserved task exception", a.Exception); a.SetObserved(); };
        Exit += (_, _) => Dispose();
        _instanceLock = new InstanceLock(lockFolder, _services.GetRequiredService<ILog>());
        var forwardPipeName = InstanceForwardPipe.NameFor(lockFolder);
        if (!_instanceLock.IsOwner)
        {
            // Q-R10: hand the request to the instance that already owns this folder instead of showing an error.
            // No answer within the timeout (stale mutex) keeps the previous behaviour.
            var existing = e.Args.Where(a => File.Exists(a) || Directory.Exists(a)).Select(Path.GetFullPath).ToList();
            var forwarded = await SecondInstanceHandoff.TryForwardAsync(
                new InstanceForwardClient(forwardPipeName, _services.GetRequiredService<ILog>(), allowServerForeground: true),
                existing, SecondInstanceHandoff.DefaultTimeout, _services.GetRequiredService<ILog>());
            if (!forwarded)
            {
                _services.GetRequiredService<IDialogService>().ShowMessage(PhotoReview.Core.Localization.Tr.AppTitle, PhotoReview.Core.Localization.Tr.FolderAlreadyOpenInOtherInstance);
            }
            _instanceLock.Dispose();
            _instanceLock = null;
            Shutdown(0);
            return;
        }
        // Listen right away: a second launch made while this one is still starting waits for the pipe (up to its timeout).
        var uiScheduler = _services.GetRequiredService<IUiScheduler>();
        _forwardCoalescer = new ForwardedOpenCoalescer(
            path => uiScheduler.Post(() => OpenForwarded(path)), ForwardCoalesceWindow, _services.GetRequiredService<ILog>());
        _forwardServer = new InstanceForwardServer(forwardPipeName, _forwardCoalescer.Submit, _services.GetRequiredService<ILog>());
        _forwardServer.Start();
        PhotoReviewPerf.StartupMark("instanceLock");
        var window = _services.GetRequiredService<MainWindow>();
        PhotoReviewPerf.StartupMark("mainWindowConstructed");
        window.InitializeWithInitialPath(initial ?? initialFolder);
        MainWindow = window;
        window.Show();
        PhotoReviewPerf.StartupMark("windowShown");
        _ = RecoverJournalAsync(window);
        if (store.LastLoadRepairs.Count > 0)
        {
            _services.GetRequiredService<IDialogService>().ShowMessage(
                PhotoReview.Core.Localization.Tr.AppTitle,
                PhotoReview.Core.Localization.Tr.SettingsLoadRepaired(string.Join(", ", store.LastLoadRepairs)));
        }
    }

    /// <summary>
    /// INV-6 / ADR 0003 (review r7): reconcile pending journal operations and load the Undo history. Runs after the
    /// instance lock and after the window is shown; the journal work itself is on the thread pool
    /// (<see cref="JournalStartupRecovery"/>), so the first image is not delayed. Operations it had to mark Failed are
    /// reported once, with an offer to open the Recovery window.
    /// </summary>
    private async Task RecoverJournalAsync(MainWindow window)
    {
        var services = _services!;
        var failed = await JournalStartupRecovery.RunAsync(
            services.GetRequiredService<OperationJournal>(),
            services.GetRequiredService<UndoService>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IUiScheduler>(),
            services.GetRequiredService<ILog>());
        if (failed.Count == 0) return;
        var dialogs = services.GetRequiredService<IDialogService>();
        if (dialogs.ShowConfirmation(PhotoReview.Core.Localization.Tr.DialogStartupRecoveryFailedTitle,
                PhotoReview.Core.Localization.Tr.DialogStartupRecoveryFailedMessage(failed.Count)))
            window.ViewModel.ShowRecovery();
    }

    /// <summary>
    /// Budget of the startup Explorer prefetch. It starts ~1 s before the folder load asks for it, and
    /// the load still bounds its own wait by its 2 s timeout, so this is 2 s plus that head start.
    /// </summary>
    /// <summary>Explorer's N launches forward within a short burst; they collapse into one open of the first path.</summary>
    private static readonly TimeSpan ForwardCoalesceWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>Runs on the UI thread (posted through <see cref="IUiScheduler"/>). No path means "just bring to front".</summary>
    private void OpenForwarded(string? path)
    {
        if (MainWindow is not MainWindow window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (path is null) return;
        var open = window.OpenPathAsync(path);
        open.ContinueWith(
            t => AppLog.Error("Forwarded open failed", t.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static readonly TimeSpan ExplorerPrefetchTimeout = TimeSpan.FromSeconds(3);

    internal static void LogStartupErrorForced(string message, Exception ex)
    {
        var wasEnabled = AppLog.Enabled;
        AppLog.Enabled = true;
        AppLog.Error(message, ex);
        AppLog.Flush();
        AppLog.Enabled = wasEnabled;
    }

    /// <summary>R2-F-12: records an unhandled exception even when logging is disabled and flushes before returning.</summary>
    internal static void LogUnhandledForced(string message, object? exceptionObject)
    {
        try
        {
            LogStartupErrorForced(message, exceptionObject as Exception ?? new InvalidOperationException(exceptionObject?.ToString() ?? "unknown exception object"));
        }
        catch { /* a failing logger must not turn a handled crash into another crash */ }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _services?.GetService<SessionWriter>()?.Flush();
        Interlocked.Exchange(ref _forwardServer, null)?.Dispose();
        Interlocked.Exchange(ref _forwardCoalescer, null)?.Dispose();
        AppLog.Shutdown(); // after the forward server/coalescer so their shutdown warnings still reach the log
        Interlocked.Exchange(ref _instanceLock, null)?.Dispose();
        _perfHooks?.Detach();
        _perfListener?.Dispose();
        _perfListener = null;
    }

    /// <summary>
    /// D05: DIAG mode changes app behavior for measurement purposes, so it must always be visible in
    /// the log, even when logging is off by default -- same pattern as
    /// <c>AppSettings.LogStartupErrorForced</c>: force logging on just long enough to persist this one
    /// line, flush, then restore whatever state it was in.
    /// </summary>
    private static void LogDiagModeForced()
    {
        var wasEnabled = AppLog.Enabled;
        AppLog.Enabled = true;
        AppLog.Error($"DIAG MODE: {DiagOptions.Describe()}");
        AppLog.Flush();
        AppLog.Enabled = wasEnabled;
    }

}
