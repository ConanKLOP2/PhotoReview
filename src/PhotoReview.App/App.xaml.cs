using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Diagnostics;
using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;
using PhotoReview.Core.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.App;

public partial class App : System.Windows.Application, IDisposable
{
    private InstanceScope? _instanceScope;
    private ForwardedOpenCoalescer? _forwardCoalescer;
    private PerfCsvListener? _perfListener;
    private PerfDispatcherHooks? _perfHooks;
    private IServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services not initialized.");

    /// <summary>
    /// P-1: runs before WPF's Application constructor (~70 ms: dispatcher, theme), the earliest app code of the process,
    /// so the settings parser's one-time cost is paid on the thread pool while the runtime and WPF start.
    /// </summary>
    static App() => _ = PhotoReview.App.Services.StartupWarmup.WarmSettingsParser();

    public static void ConfigureServices(IServiceCollection services)
    {
        // WP-09 (C-16): everything that does not depend on WPF is registered by the shared composition (App.Shared); the WPF app
        // only supplies what is WPF-specific below and the main window.
        // 1. Host services of the WPF app (resolved by the shared graph: view model composition root, settings window, ...).
        services.AddSingleton<IUiScheduler>(_ => new DispatcherUiScheduler(Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
        services.AddSingleton<IDialogService, PhotoReview.App.Services.WpfDialogService>();
        services.AddSingleton<PhotoReview.App.Coordinators.IFolderPicker, PhotoReview.App.Services.WpfFolderPicker>();
        services.AddSingleton<IClipboardService, PhotoReview.App.Services.WpfClipboardService>();
        services.AddSingleton<IPresentationSinkFactory, PhotoReview.App.Services.WpfPresentationSinkFactory>();

        // 2. The shared graph: codec = WPF bitmap codec, INV-12 fallback decoder = the WPF decoder, over the one source reader.
        services.AddPhotoReviewShared(new SharedServiceOptions(
            WpfBitmapSourceCodec.Instance,
            WpfFallbackDecoder: sp => new WpfBitmapImageDecoder(sp.GetRequiredService<ISourceReader>()),
            ThumbnailDecoder: sp => new WpfBitmapImageDecoder(sp.GetRequiredService<ISourceReader>())));

        // 3. Window
        services.AddTransient<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<PhotoReview.App.Services.ViewportSizeSource>(),
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IDisplayClock>()));
    }

    /// <summary>Test seam (null in production): extra registrations layered over the production graph in <see cref="StartupCoreAsync"/>.</summary>
    internal static Action<IServiceCollection>? StartupServiceOverrides { get; set; }

    /// <summary>Test seam (the production prefix unless a test sets one): mutex/pipe name prefix of the startup <see cref="InstanceScope"/>.</summary>
    internal static string StartupInstanceNamePrefix { get; set; } = InstanceKeys.DefaultPrefix;

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
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

        _services = Composition.AppHost.BuildServices(StartupServiceOverrides);
        PhotoReviewPerf.StartupMark("servicesBuilt");

        var args = LaunchArguments.Normalize(e.Args); // Explorer's "Browse with PhotoReview" passes a folder (see LaunchArguments)
        var initial = args.FirstOrDefault(arg => File.Exists(arg));
        var initialFolder = args.FirstOrDefault(arg => Directory.Exists(arg));
        var launchFolder = initial is not null ? Path.GetDirectoryName(Path.GetFullPath(initial)) : initialFolder; // R2-F-08: a relative file argument has an empty directory name
        // perf(startup): Explorer's view order is the slowest part of opening a photo (~1-2 s of
        // cross-process COM for a large folder). Start it now, in parallel with settings, window
        // construction and Show(); the folder load joins this query instead of starting its own.
        if (!string.IsNullOrEmpty(launchFolder))
            _services.GetRequiredService<IExplorerOrderProvider>().Prefetch(launchFolder, ExplorerPrefetchTimeout);
        // P-1: the saved placement is read on the pool now; Show() restores it from this read, and the launch file's
        // decode box is predicted from it (below).
        var placementLoad = WindowPlacementService.Prefetch(_services.GetRequiredService<IAppPaths>().WindowPlacementFile);
        Task<EarlyDecodePlan?>? earlyDecode = null;

        var store = _services.GetRequiredService<SettingsStore>();
        store.Changed += (_, settings) => AppLog.Enabled = settings.LoggingEnabled || DiagOptions.ForceLog;
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
            PhotoReviewPerf.StartupMark("settingsFileLoaded");
            // P-1: the preview service (decoder probes, caches) is built and the viewport predicted in parallel with
            // the language load and the main window; the decode itself starts once this instance owns its lock.
            if (initial is not null) earlyDecode = Task.Run(() => PrepareEarlyDecodeAsync(placementLoad));
            var localizer = localization.Load(loaded.UiLanguage);
            PhotoReviewPerf.StartupMark("languageLoaded");
            return (Settings: loaded, Localizer: localizer);
        });
        PhotoReview.App.Services.StartupWarmup.ConnectRenderThread();
        PhotoReviewPerf.StartupMark("renderThreadConnected");
        var (appSettings, localizer) = await settingsLoad;
        localization.Apply(appSettings.UiLanguage, localizer);
        PhotoReviewPerf.StartupMark("settingsLoaded");
        AppLog.Enabled = appSettings.LoggingEnabled || DiagOptions.ForceLog;
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
        // Q-R18: the instance mode comes from the settings loaded above, i.e. before any lock is taken; a change made in
        // Settings therefore applies at the next start. SingleWindow = one app-wide lock + pipe; PerFolder = the lock and
        // pipe of the launch folder, which then follow the folder the window shows (see InstanceScope).
        var uiScheduler = _services.GetRequiredService<IUiScheduler>();
        _forwardCoalescer = new ForwardedOpenCoalescer(
            path => uiScheduler.Post(() => OpenForwarded(path)), ForwardCoalesceWindow, _services.GetRequiredService<ILog>());
        _instanceScope = new InstanceScope(
            appSettings.InstanceMode, _forwardCoalescer.Submit, _services.GetRequiredService<ILog>(), StartupInstanceNamePrefix, allowServerForeground: true);
        // Listen right away (the scope starts the pipe with the lock): a second launch made while this one is still
        // starting waits for the pipe (up to its timeout).
        if (!_instanceScope.TryAcquire(launchFolder))
        {
            // Q-R10: hand the request to the instance that already owns this folder (or, SingleWindow, the app) instead
            // of showing an error; with no path the owner just comes to the front. No answer within the timeout (stale
            // mutex) keeps the previous behaviour.
            var existing = args.Where(a => File.Exists(a) || Directory.Exists(a)).Select(Path.GetFullPath).ToList();
            var forwarded = await SecondInstanceHandoff.TryForwardAsync(
                _instanceScope.CreateClient(launchFolder), existing, SecondInstanceHandoff.DefaultTimeout, _services.GetRequiredService<ILog>());
            if (!forwarded)
            {
                _services.GetRequiredService<IDialogService>().ShowMessage(
                    PhotoReview.Core.Localization.Tr.AppTitle,
                    _instanceScope.Mode == PhotoReview.Core.Model.InstanceMode.PerFolder
                        ? PhotoReview.Core.Localization.Tr.FolderAlreadyOpenInOtherInstance
                        : PhotoReview.Core.Localization.Tr.AppAlreadyRunningNoResponse);
            }
            Interlocked.Exchange(ref _instanceScope, null)?.Dispose();
            Interlocked.Exchange(ref _forwardCoalescer, null)?.Dispose();
            Shutdown(0);
            return;
        }
        PhotoReviewPerf.StartupMark("instanceLock");
        if (earlyDecode is not null && initial is not null) StartEarlyDecode(earlyDecode, initial, appSettings);
        var window = _services.GetRequiredService<MainWindow>();
        window.ViewModel.FolderOwnership = _instanceScope;
        // Manual/agent verification convenience: set PHOTOREVIEW_DIAG_INSTANCE_LABEL (e.g. "AGENT CHECK")
        // before launching the built exe directly to mark the title bar, so it is never mistaken for the
        // user's real everyday window. Zero effect when unset (the normal launch).
        if (DiagOptions.InstanceLabel is { } instanceLabel) window.ViewModel.InstanceLabel = instanceLabel;
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
                PhotoReview.Core.Settings.SettingsLoadRepairText.Build(store.LastLoadRepairs));
        }
    }

    /// <summary>P-1: what the launch file's early decode needs, prepared on the thread pool while settings and XAML load.</summary>
    private sealed record EarlyDecodePlan(PreviewImageService Previews, DecodeBox Box);

    /// <summary>
    /// P-1 (thread pool, right after config.json is read): builds the preview service (decoder probes and caches, work
    /// the main window's view model would otherwise do on the UI thread) and predicts the decode box of the window's
    /// first layout from its saved placement. Null when no prediction is possible: the decode then starts when the
    /// window is shown (MainWindow.StartInitialDecode), as before.
    /// </summary>
    private async Task<EarlyDecodePlan?> PrepareEarlyDecodeAsync(Task<PhotoReview.App.Windowing.WindowPlacementData?> placementLoad)
    {
        try
        {
            // Runs inside Task.Run (no synchronization context): the continuation stays on the pool.
            var placement = await placementLoad;
            var box = PhotoReview.App.Services.InitialViewportPredictor.PredictDecodeBox(placement,
                PhotoReview.App.MainWindow.DefaultWindowWidth, PhotoReview.App.MainWindow.DefaultWindowHeight, PhotoReview.App.MainWindow.PreviewQualityMultiplier);
            if (box is null || _services is not { } services) return null;
            return new EarlyDecodePlan(services.GetRequiredService<PreviewImageService>(), box.Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never fails the startup: the window starts the decode itself when it is shown.
            AppLog.Error("Early decode preparation failed", ex);
            return null;
        }
    }

    /// <summary>P-1 (UI thread, after the instance lock): starts the launch file's decode once its plan is ready.</summary>
    private void StartEarlyDecode(Task<EarlyDecodePlan?> plan, string path, AppSettings settings)
    {
        var viewport = _services!.GetRequiredService<PhotoReview.App.Services.ViewportSizeSource>();
        // Task.Run: the await continues on the pool the moment the plan is ready (not queued behind the UI thread's XAML load).
        _ = Task.Run(async () =>
        {
            if (await plan is not { } p) return;
            viewport.StartupPrediction = p.Box;
            if (PhotoReview.App.Services.InitialImagePrewarm.Start(p.Previews, settings, path, p.Box) is not null)
                PhotoReviewPerf.StartupMark("earlyDecodeStarted");
        });
    }

    /// <summary>
    /// INV-6 / ADR 0003 (review r7): reconcile pending journal operations. Runs after the instance lock and after the
    /// window is shown; the journal work itself is on the thread pool (<see cref="JournalStartupRecovery"/>), so the
    /// first image is not delayed. Operations it had to mark Failed are reported once, with an offer to open the
    /// Recovery window. Decision P03 (2026-09-27): this no longer seeds the Undo stack from journal history - Undo
    /// is limited to the current session.
    /// </summary>
    private async Task RecoverJournalAsync(MainWindow window)
    {
        // Fire-and-forget from StartupCoreAsync: a fault here would otherwise surface only via UnobservedTaskException
        // after a GC, long after the cause.
        try
        {
            var services = _services!;
            var failed = await JournalStartupRecovery.RunAsync(
                services.GetRequiredService<OperationJournal>(),
                services.GetRequiredService<IClock>(),
                services.GetRequiredService<ILog>());
            if (failed.Count == 0) return;
            var dialogs = services.GetRequiredService<IDialogService>();
            if (dialogs.ShowConfirmation(PhotoReview.Core.Localization.Tr.DialogStartupRecoveryFailedTitle,
                    PhotoReview.Core.Localization.Tr.DialogStartupRecoveryFailedMessage(failed.Count)))
                window.ViewModel.ShowRecovery();
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup journal recovery failed", ex);
        }
    }

    /// <summary>Explorer's N launches forward within a short burst; they collapse into one open of the first path.</summary>
    private static readonly TimeSpan ForwardCoalesceWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>Runs on the UI thread (posted through <see cref="IUiScheduler"/>). No path means "just bring to front".</summary>
    private void OpenForwarded(string? path)
    {
        if (MainWindow is not MainWindow window || window.IsClosingOrClosed) return; // a request that raced the shutdown
        try
        {
            // R7-5: SC_RESTORE brings back the pre-minimize state (Maximized / fullscreen), unlike forcing Normal.
            if (window.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(window);
            window.Activate();
        }
        catch (InvalidOperationException ex)
        {
            // The window went away between the check and the call: nothing to bring to front, nothing to open into.
            AppLog.Warn($"Forwarded launch: could not activate the main window: {ex.Message}");
            return;
        }
        if (path is null) return;
        var open = window.OpenPathAsync(path);
        _ = open.ContinueWith(
            t => AppLog.Error("Forwarded open failed", t.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Budget of the startup Explorer prefetch. It starts ~1 s before the folder load asks for it, and
    /// the load still bounds its own wait by its 2 s timeout, so this is 2 s plus that head start.
    /// </summary>
    private static readonly TimeSpan ExplorerPrefetchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The "force logging on, write, flush, restore" sequence lives in <see cref="AppLog.WriteForced"/> (App.Shared, one lock for
    /// every caller: the shared composition logs its own startup errors through it).
    /// </summary>
    private static void WriteForced(Action write) => AppLog.WriteForced(write);

    internal static void LogStartupErrorForced(string message, Exception ex) => AppLog.ErrorForced(message, ex);

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
        _instanceScope?.StopListening();
        Interlocked.Exchange(ref _forwardCoalescer, null)?.Dispose();
        // APP-01 ownership: the container disposes every singleton it created (SessionWriter with its bounded 2 s flush
        // (Q-R5), ExplorerOrderService, caches). Instances registered by value (FileLog.Default) are NOT disposed by it;
        // AppLog.Shutdown below owns those. Nothing else disposes a container-owned service, so there is no double-dispose.
        (Interlocked.Exchange(ref _services, null) as IDisposable)?.Dispose();
        AppLog.Shutdown(); // after the forward server/coalescer so their shutdown warnings still reach the log
        Interlocked.Exchange(ref _instanceScope, null)?.Dispose();
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
    private static void LogDiagModeForced() => WriteForced(() => AppLog.Error($"DIAG MODE: {DiagOptions.Describe()}"));

}
