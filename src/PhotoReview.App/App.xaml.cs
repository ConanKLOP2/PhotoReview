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
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;

namespace PhotoReview.App;

public partial class App : System.Windows.Application, IDisposable
{
    private InstanceLock? _instanceLock;
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
        services.AddSingleton<ILog>(_ => FileLog.Default);

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

        // 3. Journal & File Actions
        services.AddSingleton<OperationJournal>(sp => new OperationJournal(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>()));
        services.AddSingleton<RecoveryRetryService>(sp => new RecoveryRetryService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>()));
        services.AddSingleton<FileHashService>(sp => new FileHashService(
            sp.GetRequiredService<SettingsStore>().Current.UseSourceBytesCache
                ? sp.GetRequiredService<SourceBytesCache>()
                : null));
        services.AddSingleton<FileActionService>(sp => new FileActionService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IRecycleBin>()));
        services.AddSingleton<UndoService>(sp => new UndoService(
            sp.GetRequiredService<OperationJournal>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IRecycleBin>(),
            sp.GetRequiredService<FileActionService>()));

        // 4. Diagnostics & Metrics
        services.AddSingleton<ReviewMetrics>();

        // 5. Platform Services
        services.AddSingleton<IExplorerOrderProvider, ExplorerOrderService>();
        services.AddSingleton<IProgressiveExplorerOrderProvider, ExplorerOrderProviderAdapter>();
        services.AddSingleton<IRecycleBin>(_ => WindowsRecycleBin.Instance);
        services.AddSingleton<IMemoryProbe>(_ => PhysicalMemory.Instance);
        services.AddSingleton<INaturalComparer>(_ => WindowsNaturalComparer.Instance);
        services.AddSingleton<IKeyNameValidator, WpfKeyNameValidator>();
        services.AddSingleton<IUiScheduler>(_ => new DispatcherUiScheduler(Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
        services.AddSingleton<IDialogService, PhotoReview.App.Services.WpfDialogService>();

        // 6. Imaging & Decoding
        services.AddSingleton<IImageDecoderFactory>(sp => new ImageDecoderFactory(sp.GetService<ILog>(), sp.GetService<ReviewMetrics>()));
        services.AddSingleton<ThumbnailCache>(sp => new ThumbnailCache(
            persistNewThumbnails: false,
            log: sp.GetService<ILog>(),
            sourceBytesCache: sp.GetRequiredService<SettingsStore>().Current.UseSourceBytesCache
                ? sp.GetRequiredService<SourceBytesCache>()
                : null));
        services.AddSingleton<SourceBytesCache>(sp => new SourceBytesCache(
            sp.GetRequiredService<SettingsStore>().Current.SourceBytesCapacityBytes));

        services.AddSingleton<PreviewStateContext>();
        services.AddSingleton<PreviewImageService>(sp =>
        {
            var ctx = sp.GetRequiredService<PreviewStateContext>();
            var settingsStore = sp.GetRequiredService<SettingsStore>();
            ctx.CurrentBackend = () => settingsStore.Current.DecoderBackend;
            return new PreviewImageService(
                sp.GetRequiredService<ReviewMetrics>(),
                () => ctx.IsOriginalLoadingMode(),
                () => ctx.TargetDecodeWidth(),
                capacityBytes: settingsStore.Current.ImageCacheCapacityBytes,
                decoderFactory: sp.GetRequiredService<IImageDecoderFactory>(),
                currentBackend: () => ctx.CurrentBackend(),
                log: sp.GetService<ILog>(),
                sourceBytesCache: settingsStore.Current.UseSourceBytesCache
                    ? sp.GetRequiredService<SourceBytesCache>()
                    : null);
        });

        services.AddSingleton<Func<Func<string[]>, Func<long>, PreloadScheduler>>(sp =>
            (getFiles, getTotalBytes) =>
            {
                var settingsStore = sp.GetRequiredService<SettingsStore>();
                return new PreloadScheduler(
                sp.GetRequiredService<PreviewImageService>(),
                sp.GetRequiredService<ReviewMetrics>(),
                getFiles,
                getTotalBytes,
                fullFolderRamThresholdBytes: sp.GetRequiredService<SettingsStore>().Current.ImageCacheCapacityBytes,
                memoryLoadLimit: sp.GetRequiredService<SettingsStore>().Current.PreloadMemoryLoadLimit,
                memoryProbe: sp.GetRequiredService<IMemoryProbe>(),
                workerCountOverride: sp.GetRequiredService<SettingsStore>().Current.PreloadWorkerCount,
                log: sp.GetService<ILog>(),
                prefetchSourceBytes: settingsStore.Current.UseSourceBytesCache
                    ? (path, token) => Task.Run(() => sp.GetRequiredService<SourceBytesCache>().GetOrRead(path), token)
                    : null);
            });

        // 7. ViewModels & Coordinators
        services.AddTransient<PhotoReview.App.ViewModels.ViewerState>();
        services.AddTransient<PhotoReview.App.ViewModels.CompareViewModel>();
        services.AddTransient<PhotoReview.Core.Catalog.ReviewCatalog>();
        services.AddTransient<PhotoReview.Core.Catalog.GenerationClock>();
        services.AddTransient<PhotoReview.App.ViewModels.MainViewModel>(sp =>
        {
            var catalog = sp.GetRequiredService<PhotoReview.Core.Catalog.ReviewCatalog>();
            var clock = sp.GetRequiredService<PhotoReview.Core.Catalog.GenerationClock>();
            var viewer = sp.GetRequiredService<PhotoReview.App.ViewModels.ViewerState>();
            var compare = sp.GetRequiredService<PhotoReview.App.ViewModels.CompareViewModel>();
            var settingsStore = sp.GetRequiredService<SettingsStore>();
            var sessionStore = sp.GetRequiredService<SessionStore>();
            var sessionWriter = sp.GetRequiredService<SessionWriter>();
            var fs = sp.GetRequiredService<IFileSystem>();
            var actions = sp.GetRequiredService<FileActionService>();
            var undo = sp.GetRequiredService<UndoService>();
            var dialog = sp.GetRequiredService<IDialogService>();
            var hash = sp.GetRequiredService<FileHashService>();
            var preview = sp.GetRequiredService<PreviewImageService>();
            var thumbs = sp.GetRequiredService<ThumbnailCache>();
            var natural = sp.GetRequiredService<INaturalComparer>();
            var explorerOrder = sp.GetRequiredService<IProgressiveExplorerOrderProvider>();
            var schedulerFactory = sp.GetRequiredService<Func<Func<string[]>, Func<long>, PreloadScheduler>>();

            var sourceSizeGate = new object();
            HashSet<string> sizedPaths = new(StringComparer.OrdinalIgnoreCase);
            long cachedTotalSourceBytes = 0;
            long GetTotalSourceBytes()
            {
                var snapshot = catalog.Snapshot();
                lock (sourceSizeGate)
                {
                    if (sizedPaths.Count == snapshot.Length && sizedPaths.SetEquals(snapshot))
                        return cachedTotalSourceBytes;

                    cachedTotalSourceBytes = snapshot.Sum(path =>
                    {
                        try { return fs.GetFileStat(path)?.Length ?? 0L; }
                        catch (IOException) { return 0L; }
                        catch (UnauthorizedAccessException) { return 0L; }
                    });
                    sizedPaths = new HashSet<string>(snapshot, StringComparer.OrdinalIgnoreCase);
                    return cachedTotalSourceBytes;
                }
            }

            var preloadScheduler = schedulerFactory(
                catalog.Snapshot,
                GetTotalSourceBytes);
            var preloadController = new PhotoReview.App.Coordinators.PreloadControllerAdapter(() => preloadScheduler);

            PhotoReview.App.ViewModels.MainViewModel? vm = null;
            var sink = new PhotoReview.App.Services.WpfPresentationSink(
                onSetCurrentImage: _ => vm?.NotifyPresentationChanged(),
                onSetStatusText: _ => vm?.NotifyPresentationChanged(),
                onApplyInitialViewMode: () => vm?.Viewer.ApplyInitialViewMode(settingsStore.Current.InitialViewMode, 0, 0));

            var presenter = new PhotoReview.App.Coordinators.ImagePresenter(
                catalog, clock, preview, thumbs, preloadController, compare, hash,
                sp.GetRequiredService<ReviewMetrics>(),
                () => settingsStore.Current, sessionStore, sink, fs, getSession: () => vm?.Session,
                sessionWriter: sessionWriter);

            var coordinator = new PhotoReview.App.Coordinators.FolderLoadCoordinator(
                catalog, clock, explorerOrder, fs, sessionStore, settingsStore,
                new ForwardingFolderSink(() => vm!), sessionWriter);

            return vm = new PhotoReview.App.ViewModels.MainViewModel(
                catalog, clock, coordinator, presenter, viewer, compare, settingsStore, sessionStore,
                fs, actions, undo, dialog, preloadController, natural, hashService: hash, previewService: preview, thumbnailCache: thumbs, sessionWriter: sessionWriter,
                uiScheduler: sp.GetRequiredService<IUiScheduler>());
        });

        // 8. Window
        services.AddTransient<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IProgressiveExplorerOrderProvider>()));
    }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        PhotoReview.App.Services.WpfKeyNameValidator.WireUp();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        var store = _services.GetRequiredService<SettingsStore>();
        store.Changed += (_, settings) => AppLog.Enabled = settings.LoggingEnabled;
        var appSettings = store.Load();
        AppLog.Enabled = appSettings.LoggingEnabled;
        if (AppLog.Enabled) AppLog.Info($"Startup args={string.Join(" | ", e.Args)}");
        // D05: PHOTOREVIEW_DIAG_* variables change app behavior for measurement purposes, so their
        // presence must be visible in the log even when logging is otherwise disabled -- same reasoning
        // as AppSettings.LogStartupErrorForced.
        if (DiagOptions.AnyEnabled) LogDiagModeForced();
        _perfListener = PerfCsvListener.TryStartFromEnvironment();
        if (_perfListener is not null) AppLog.Info("Perf trace enabled (PHOTOREVIEW_PERF_TRACE)");
        if (_perfListener is not null)
        {
            // D04 perf: dispatcher hooks and the DIAG flag summary exist only while the CSV listener runs.
            _perfHooks = PerfDispatcherHooks.Attach(Dispatcher);
            PerfDispatcherHooks.TraceDiagMode();
        }
        DispatcherUnhandledException += (_, a) => { AppLog.Error("Dispatcher exception", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppLog.Error("AppDomain exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Error("Unobserved task exception", a.Exception); a.SetObserved(); };
        Exit += (_, _) => Dispose();
        var initial = e.Args.FirstOrDefault(arg => File.Exists(arg));
        var initialFolder = e.Args.FirstOrDefault(arg => Directory.Exists(arg));
        var lockFolder = initial is not null ? Path.GetDirectoryName(initial) : initialFolder;
        _instanceLock = new InstanceLock(lockFolder);
        if (!_instanceLock.IsOwner)
        {
            System.Windows.MessageBox.Show("Folder này đang được mở trong một Photo Review khác.", "Photo Review", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceLock.Dispose();
            _instanceLock = null;
            Shutdown();
            return;
        }
        var window = _services.GetRequiredService<MainWindow>();
        window.InitializeWithInitialPath(initial ?? initialFolder);
        MainWindow = window;
        window.Show();
    }

    internal static void LogStartupErrorForced(string message, Exception ex)
    {
        var wasEnabled = AppLog.Enabled;
        AppLog.Enabled = true;
        AppLog.Error(message, ex);
        AppLog.Flush();
        AppLog.Enabled = wasEnabled;
    }

    public void Dispose()
    {
        _services?.GetService<SessionWriter>()?.Flush();
        AppLog.Shutdown();
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

    /// <summary>
    /// D04 perf: times every dispatcher operation via <see cref="Dispatcher.Hooks"/> and emits
    /// <c>DispatcherLongOp</c> for those running longer than 16 ms. Only attached while the perf CSV
    /// listener is active. Start timestamps live in a ConditionalWeakTable keyed by the operation, so
    /// an operation that never completes (or is aborted from another thread) cannot leak an entry.
    /// Durations are inclusive: an operation that pumps nested frames (Dispatcher.Yield/ShowDialog)
    /// also counts the nested operations' time.
    /// </summary>
    private sealed class PerfDispatcherHooks
    {
        private const double LongOpThresholdMs = 16;
        // DispatcherOperation keeps its callback in a private field; reading it is best effort and only
        // happens for operations that already exceeded the threshold.
        private static readonly FieldInfo? MethodField =
            typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly DispatcherHooks _hooks;
        private readonly ConditionalWeakTable<DispatcherOperation, StrongBox<long>> _starts = new();

        private PerfDispatcherHooks(DispatcherHooks hooks) => _hooks = hooks;

        public static PerfDispatcherHooks? Attach(Dispatcher dispatcher)
        {
            try
            {
                var hooks = new PerfDispatcherHooks(dispatcher.Hooks);
                hooks._hooks.OperationStarted += hooks.OnStarted;
                hooks._hooks.OperationCompleted += hooks.OnCompleted;
                hooks._hooks.OperationAborted += hooks.OnAborted;
                return hooks;
            }
            catch (Exception ex)
            {
                AppLog.Error("Perf dispatcher hooks unavailable", ex);
                return null;
            }
        }

        public void Detach()
        {
            _hooks.OperationStarted -= OnStarted;
            _hooks.OperationCompleted -= OnCompleted;
            _hooks.OperationAborted -= OnAborted;
        }

        private void OnStarted(object? sender, DispatcherHookEventArgs e)
            => _starts.AddOrUpdate(e.Operation, new StrongBox<long>(Stopwatch.GetTimestamp()));

        private void OnAborted(object? sender, DispatcherHookEventArgs e) => _starts.Remove(e.Operation);

        private void OnCompleted(object? sender, DispatcherHookEventArgs e)
        {
            var operation = e.Operation;
            if (!_starts.TryGetValue(operation, out var start)) return;
            _starts.Remove(operation);
            var ms = PhotoReviewPerf.Ms(start.Value);
            if (ms <= LongOpThresholdMs) return;
            var priority = operation.Priority.ToString();
            PhotoReviewPerf.Log.DispatcherLongOp(ms, priority, DescribeOperation(operation) ?? priority);
        }

        private static string? DescribeOperation(DispatcherOperation operation)
        {
            try
            {
                if (MethodField?.GetValue(operation) is not Delegate callback) return null;
                var method = callback.Method;
                return method.DeclaringType is { } type ? $"{type.Name}.{method.Name}" : method.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Emits DiagMode once with every PHOTOREVIEW_DIAG_* variable, if any is set.</summary>
        public static void TraceDiagMode()
        {
            var flags = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && name.StartsWith("PHOTOREVIEW_DIAG_", StringComparison.OrdinalIgnoreCase))
                    flags.Add($"{name.ToUpperInvariant()}={entry.Value}");
            }
            if (flags.Count == 0) return;
            flags.Sort(StringComparer.Ordinal);
            PhotoReviewPerf.Log.DiagMode(string.Join(";", flags));
        }
    }
}
