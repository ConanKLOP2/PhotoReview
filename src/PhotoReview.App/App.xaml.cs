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

        services.AddSingleton<Func<Func<CatalogEntry[]>, Func<long>, PreloadScheduler>>(sp =>
            (getEntries, getTotalBytes) =>
            {
                var settingsStore = sp.GetRequiredService<SettingsStore>();
                return new PreloadScheduler(
                sp.GetRequiredService<PreviewImageService>(),
                sp.GetRequiredService<ReviewMetrics>(),
                getEntries,
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
        services.AddTransient<PhotoReview.App.ViewModels.MainViewModel>(sp => Composition.MainViewModelCompositionRoot.Create(sp));

        // 8. Window
        services.AddTransient<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<PhotoReview.App.ViewModels.MainViewModel>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IExplorerOrderProvider>()));
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
        GC.SuppressFinalize(this);
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

}
