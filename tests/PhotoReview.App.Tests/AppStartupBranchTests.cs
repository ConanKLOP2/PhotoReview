using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests;

/// <summary>
/// Branch coverage of <c>App.App_Startup</c>, <c>App.FailStartup</c> and <c>App.StartupCoreAsync</c>. The WPF
/// <see cref="Application"/> is a process singleton that other tests already own, so the private methods run on an
/// uninitialised <see cref="App"/> (same trick as <c>AppForwardedOpenTests</c>) whose dispatcher field is pointed at a
/// private STA dispatcher thread. Services, the mutex/pipe prefix and the data root are isolated per test through the
/// <c>StartupServiceOverrides</c>/<c>StartupInstanceNamePrefix</c> seams and <see cref="DataRootFixture"/>.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class AppStartupBranchTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static readonly MethodInfo StartupCore =
        typeof(App).GetMethod("StartupCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo AppStartup =
        typeof(App).GetMethod("App_Startup", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo FailStartup =
        typeof(App).GetMethod("FailStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo DispatcherField =
        typeof(DispatcherObject).GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T Field<T>(App app, string name) =>
        (T)typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = [];
        public List<(string Title, string Message)> Errors { get; } = [];
        public Action<string>? OnMessage { get; set; }
        public bool ThrowOnError { get; set; }

        public bool ShowConfirmation(string title, string message) => false;

        public void ShowMessage(string title, string message)
        {
            Messages.Add((title, message));
            OnMessage?.Invoke(message);
        }

        public void ShowError(string title, string message)
        {
            if (ThrowOnError) throw new InvalidOperationException("dialog unavailable");
            Errors.Add((title, message));
        }

        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class RecordingExplorerOrder : IExplorerOrderProvider
    {
        public List<(string Folder, TimeSpan Timeout)> Prefetches { get; } = [];

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
            string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExplorerViewSnapshot(
                folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, "test fake", DateTime.UtcNow));

        public Exception? PrefetchFault { get; set; }

        public void Prefetch(string folder, TimeSpan timeout)
        {
            Prefetches.Add((folder, timeout));
            if (PrefetchFault is not null) throw PrefetchFault;
        }

        public void Dispose() { }
    }

    /// <summary>Everything one startup run needs: isolated data root, private log, unique instance names, fakes and a UI thread.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly DataRootFixture _data = new();
        private readonly Thread _thread;
        private readonly string?[] _envBackup;
        private readonly Dispatcher? _applicationDispatcher;
        private readonly Application? _plainApplication;
        private readonly Localizer _localizer = Localizer.Current;
        private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        private readonly object? _shuttingDown = typeof(Application).GetField("_isShuttingDown", AnyNonPublic)!.GetValue(null);
        private readonly string _cwd = Environment.CurrentDirectory;
        public static readonly string[] EnvNames =
        [
            "PHOTOREVIEW_PERF_TRACE", "PHOTOREVIEW_DIAG_FORCE_LOG", "PHOTOREVIEW_DIAG_INSTANCE_LABEL",
            "PHOTOREVIEW_DIAG_PREREAD", "PHOTOREVIEW_DIAG_PRELOAD_WORKERS", "PHOTOREVIEW_DIAG_DISABLE_DISKCACHE",
        ];

        public Rig()
        {
            _envBackup = EnvNames.Select(Environment.GetEnvironmentVariable).ToArray();
            foreach (var name in EnvNames) Environment.SetEnvironmentVariable(name, null);
            DiagOptions.ResetForTests();
            Paths = AppPaths.FromEnvironment();
            Log = new FileLog(Paths);
            AppLog.Instance = Log;
            AppLog.Enabled = false;
            Prefix = "PRTest" + Guid.NewGuid().ToString("N");
            App.StartupInstanceNamePrefix = Prefix;
            App.StartupServiceOverrides = services =>
            {
                services.AddSingleton<IDialogService>(Dialogs);
                services.AddSingleton<IExplorerOrderProvider>(Explorer);
                services.AddSingleton<IUiScheduler>(_ => new PhotoReview.App.Services.DispatcherUiScheduler(Ui));
            };

            EnsureApplication();
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                using var guard = Win32DialogGuard.InstallOnCurrentThread();
                Guard = guard;
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Ui = dispatcher!;
            // Application.Shutdown on the uninitialised App queues ShutdownCallback, whose execution would tear down process-wide
            // WPF state (resource packages, Application.Current) that every later test needs. Observe the request (exit code, flag)
            // but never let the teardown itself run.
            Ui.Hooks.OperationPosted += (_, e) =>
            {
                var method = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(e.Operation) as Delegate;
                if (method?.Method.Name == "ShutdownCallback") e.Operation.Abort();
            };
            // The process-wide plain Application belongs to a thread that is long gone (or to another test's dispatcher): any
            // Application.Current.Dispatcher.Invoke from the main window would wait for it forever. For the length of this rig
            // the Application is bound to the rig's live dispatcher (GlobalState serialises every user of it).
            _plainApplication = Application.Current;
            _applicationDispatcher = (Dispatcher?)DispatcherField.GetValue(_plainApplication!);
            DispatcherField.SetValue(_plainApplication!, Ui);
        }

        public AppPaths Paths { get; }
        public FileLog Log { get; }
        public string Prefix { get; }
        public Dispatcher Ui { get; }
        public Win32DialogGuard? Guard { get; private set; }
        public RecordingDialogs Dialogs { get; } = new();
        public RecordingExplorerOrder Explorer { get; } = new();
        public App? App { get; private set; }

        public void WriteConfig(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConfigFile)!);
            File.WriteAllText(Paths.ConfigFile, json);
        }

        public Task<T> OnUiAsync<T>(Func<Task<T>> body) => Ui.InvokeAsync(body).Task.Unwrap().WaitAsync(Bound);

        public Task OnUiAsync(Func<Task> body) => Ui.InvokeAsync(body).Task.Unwrap().WaitAsync(Bound);

        /// <summary>An uninitialised <see cref="App"/> bound to this rig's dispatcher; must run on the UI thread.</summary>
        public App NewApp()
        {
            var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
            DispatcherField.SetValue(app, Ui);
            App = app;
            return app;
        }

        public static Task StartAsync(App app, params string[] args)
        {
            var e = (StartupEventArgs)Activator.CreateInstance(typeof(StartupEventArgs), nonPublic: true)!;
            // Args has no setter: the arguments live in the only string[] instance field of StartupEventArgs.
            typeof(StartupEventArgs).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(string[])).SetValue(e, args);
            return (Task)StartupCore.Invoke(app, [e])!;
        }

        /// <summary>
        /// The process-wide plain <see cref="Application"/> (MainWindow needs one) is created on a throwaway STA thread that never
        /// runs a dispatcher, like the other tests do: one created on this rig's dispatcher thread would be shut down with it and
        /// WPF refuses a second one in the same AppDomain.
        /// </summary>
        private static void EnsureApplication()
        {
            var thread = new Thread(() =>
            {
                if (Application.Current is null)
                {
                    try { _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; }
                    catch (InvalidOperationException) when (Application.Current is not null) { }
                }
                try
                {
                    Application.ResourceAssembly = typeof(MainWindow).Assembly;
                }
                catch
                {
                    typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                        ?.SetValue(null, typeof(MainWindow).Assembly);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        public string ReadLog()
        {
            Log.Flush();
            return File.Exists(Log.FilePath) ? File.ReadAllText(Log.FilePath) : string.Empty;
        }

        public void Dispose()
        {
           
            try
            {
                var app = App;
                if (app is not null)
                    Ui.Invoke(() =>
                    {
                        if (AppBase<Window?>(app, "_mainWindow") is { } window) window.Close();
                        app.Dispose();
                    }, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(30));
            }
            finally
            {
               
                // Shutdown(...) on the uninitialised App flags the process as shutting down and may clear Application.Current: put both
                // back (and give the plain Application its own dispatcher again) BEFORE this rig's dispatcher goes away.
                DispatcherField.SetValue(_plainApplication!, _applicationDispatcher);
                typeof(Application).GetField("_appInstance", AnyNonPublic)!.SetValue(null, _plainApplication);
                typeof(Application).GetField("_isShuttingDown", AnyNonPublic)!.SetValue(null, _shuttingDown);
                Ui.InvokeShutdown();
                _thread.Join(Bound);
                App.StartupServiceOverrides = null;
                App.StartupInstanceNamePrefix = PhotoReview.Platform.Windows.InstanceKeys.DefaultPrefix;
                AppLog.Enabled = false;
                AppLog.Instance = null!;
                Log.Dispose();
                for (var i = 0; i < EnvNames.Length; i++) Environment.SetEnvironmentVariable(EnvNames[i], _envBackup[i]);
                DiagOptions.ResetForTests();
                Environment.CurrentDirectory = _cwd;
                Localizer.SetCurrent(_localizer); // StartupCoreAsync publishes the settings' UI language process-wide
                CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
                _data.Dispose();
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private const BindingFlags AnyNonPublic = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    private static T AppBase<T>(App app, string name) =>
        (T)typeof(Application).GetField(name, AnyNonPublic)!.GetValue(app)!;

    /// <summary>Window properties are dispatcher-affine: read them on the rig's UI thread.</summary>
    private static T OnUi<T>(Rig rig, Func<T> read) => rig.Ui.Invoke(read, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(20));

    private static Window? MainWindowOf(Rig rig, App app) => AppBase<Window?>(app, "_mainWindow");

    private static void SetField(App app, string name, object? value) =>
        typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);

    private static string ConfigJson(string extra = "") =>
        "{ \"ConfigVersion\": " + AppSettings.CurrentConfigVersion + (extra.Length == 0 ? "" : ", " + extra) + " }";

    private static async Task<App> StartedAsync(Rig rig, params string[] args)
    {
        await rig.OnUiAsync(async () =>
        {
            var app = rig.NewApp();
            await Rig.StartAsync(app, args);
        });
        return rig.App!;
    }

    private static Task DisposeAppAsync(Rig rig, App app) => rig.OnUiAsync(() => { app.Dispose(); return Task.CompletedTask; });

    /// <summary>Holds a named mutex on its own thread (mutexes are thread-affine) so a later startup finds the instance lock taken.</summary>
    private sealed class MutexHolder : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _release = new();

        public MutexHolder(string name)
        {
            using var held = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(true, name);
                held.Set();
                _release.Wait();
                mutex.ReleaseMutex();
            })
            { IsBackground = true };
            _thread.Start();
            held.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(Bound);
            _release.Dispose();
        }
    }

    // ---- launch arguments --------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_FileArgument_PrefetchesItsFolderAndShowsTheMainWindowOwnedByTheInstanceScope()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");
        var file = photos.File("a.jpg", 1, 2, 3);

        var app = await StartedAsync(rig, file);

        var (folder, timeout) = Assert.Single(rig.Explorer.Prefetches);
        Assert.Equal(photos.Path, folder);
        Assert.Equal(TimeSpan.FromSeconds(3), timeout);
        var window = Assert.IsType<MainWindow>(MainWindowOf(rig, app));
        Assert.True(OnUi(rig, () => window.IsVisible));
        var scope = Field<PhotoReview.Platform.Windows.InstanceScope>(app, "_instanceScope");
        Assert.Same(scope, OnUi(rig, () => window.ViewModel.FolderOwnership));
        Assert.False(AppBase<bool>(app, "_isShuttingDown"));
    }

    [Fact]
    public async Task StartupCore_FolderArgument_PrefetchesThatFolder()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");

        await StartedAsync(rig, photos.Path);

        Assert.Equal(photos.Path, Assert.Single(rig.Explorer.Prefetches).Folder);
    }

    [Fact]
    public async Task StartupCore_FileAndFolderArguments_TheFileDecidesTheLaunchFolder()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");
        var file = photos.File(Path.Combine("sub", "a.jpg"), 1);
        var otherFolder = photos.Dir("other");

        await StartedAsync(rig, otherFolder, file);

        Assert.Equal(Path.Combine(photos.Path, "sub"), Assert.Single(rig.Explorer.Prefetches).Folder);
    }

    [Fact]
    public async Task StartupCore_RelativeFileArgument_PrefetchesTheAbsoluteFolder()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");
        photos.File("rel.jpg", 1);
        Environment.CurrentDirectory = photos.Path;

        await StartedAsync(rig, "rel.jpg");

        // R2-F-08: GetDirectoryName("rel.jpg") is empty; without GetFullPath nothing would be prefetched.
        Assert.Equal(photos.Path, Assert.Single(rig.Explorer.Prefetches).Folder);
    }

    [Fact]
    public async Task StartupCore_NoArguments_PrefetchesNothingAndStillShowsTheWindow()
    {
        using var rig = new Rig();

        var app = await StartedAsync(rig);

        Assert.Empty(rig.Explorer.Prefetches);
        Assert.IsType<MainWindow>(MainWindowOf(rig, app));
    }

    [Fact]
    public async Task StartupCore_UnknownArgument_IsIgnored()
    {
        using var rig = new Rig();

        var app = await StartedAsync(rig, Path.Combine(Path.GetTempPath(), "PhotoReview-Test-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(rig.Explorer.Prefetches);
        Assert.IsType<MainWindow>(MainWindowOf(rig, app));
    }

    // ---- settings repairs ---------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_RepairedSettings_AreReportedOnceThroughTheDialogService()
    {
        using var rig = new Rig();
        rig.WriteConfig(ConfigJson("\"ImageCacheRamPercent\": 500"));

        var app = await StartedAsync(rig);

        var (title, message) = Assert.Single(rig.Dialogs.Messages);
        Assert.Equal(Tr.AppTitle, title);
        Assert.Equal(SettingsLoadRepairText.Build(["ImageCacheRamPercent"]), message);
        Assert.IsType<MainWindow>(MainWindowOf(rig, app));
    }

    [Fact]
    public async Task StartupCore_ValidSettings_ShowNoDialog()
    {
        using var rig = new Rig();
        rig.WriteConfig(ConfigJson());

        await StartedAsync(rig);

        Assert.Empty(rig.Dialogs.Messages);
        Assert.Empty(rig.Dialogs.Errors);
    }

    // ---- localization ------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_UiLanguageSetting_IsPublishedToTheLocalizationService()
    {
        using var rig = new Rig();
        rig.WriteConfig(ConfigJson("\"UiLanguage\": \"vi\""));

        var app = await StartedAsync(rig);

        var localization = Field<IServiceProvider>(app, "_services").GetRequiredService<PhotoReview.App.Localization.LocalizationService>();
        Assert.Equal("vi", localization.RequestedLanguage);
        Assert.Equal(PhotoReview.App.Localization.TranslatorMode.None, localization.Mode);
    }

    [Fact]
    public async Task StartupCore_TranslatorArgument_SwitchesTheLocalizationMode()
    {
        using var rig = new Rig();

        var app = await StartedAsync(rig, "--i18n-keys");

        var localization = Field<IServiceProvider>(app, "_services").GetRequiredService<PhotoReview.App.Localization.LocalizationService>();
        Assert.Equal(PhotoReview.App.Localization.TranslatorMode.Keys, localization.Mode);
    }

    // ---- logging ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_LoggingEnabledInSettings_LogsTheArguments()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");
        var file = photos.File("a.jpg", 1);
        rig.WriteConfig(ConfigJson("\"LoggingEnabled\": true"));

        var app = await StartedAsync(rig, file, "--flag");
        await DisposeAppAsync(rig, app);

        Assert.Contains($"Startup args={file} | --flag", rig.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCore_LoggingDisabled_WritesNoStartupLineAndNoDiagLine()
    {
        using var rig = new Rig();
        rig.WriteConfig(ConfigJson("\"LoggingEnabled\": false"));

        var app = await StartedAsync(rig, "--flag");

        Assert.False(AppLog.Enabled);
        await DisposeAppAsync(rig, app);
        var log = rig.ReadLog();
        Assert.DoesNotContain("Startup args", log, StringComparison.Ordinal);
        Assert.DoesNotContain("DIAG MODE", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Perf trace enabled", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCore_SettingsChanged_TogglesLogging()
    {
        using var rig = new Rig();
        var app = await StartedAsync(rig);
        var store = Field<IServiceProvider>(app, "_services").GetRequiredService<SettingsStore>();

        OnUi(rig, () => { store.Save(new AppSettings { LoggingEnabled = true }); return 0; });
        Assert.True(AppLog.Enabled);
        OnUi(rig, () => { store.Save(new AppSettings { LoggingEnabled = false }); return 0; });
        Assert.False(AppLog.Enabled);
    }

    [Fact]
    public async Task StartupCore_ForceLogDiagFlag_KeepsLoggingOnAndRecordsTheDiagModeAndLabel()
    {
        using var rig = new Rig();
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DIAG_FORCE_LOG", "1");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DIAG_INSTANCE_LABEL", "AGENT CHECK");
        DiagOptions.ResetForTests();
        rig.WriteConfig(ConfigJson("\"LoggingEnabled\": false"));

        var app = await StartedAsync(rig);

        Assert.True(AppLog.Enabled);
        var window = Assert.IsType<MainWindow>(MainWindowOf(rig, app));
        Assert.Equal("AGENT CHECK", window.ViewModel.InstanceLabel);
        var store = Field<IServiceProvider>(app, "_services").GetRequiredService<SettingsStore>();
        OnUi(rig, () => { store.Save(new AppSettings { LoggingEnabled = false }); return 0; });
        Assert.True(AppLog.Enabled); // the flag survives a settings change
        await DisposeAppAsync(rig, app);
        Assert.Contains("DIAG MODE: FORCE_LOG=1;INSTANCE_LABEL=AGENT CHECK", rig.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCore_WithoutDiagLabel_LeavesTheInstanceLabelEmpty()
    {
        using var rig = new Rig();

        var app = await StartedAsync(rig);

        var window = Assert.IsType<MainWindow>(MainWindowOf(rig, app));
        Assert.Null(OnUi(rig, () => window.ViewModel.InstanceLabel));
    }

    // ---- perf trace ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_PerfTraceEnvironment_AttachesTheListenerAndDispatcherHooksUntilDispose()
    {
        using var rig = new Rig();
        using var trace = new TempRoot("perf");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", trace.Path);
        rig.WriteConfig(ConfigJson("\"LoggingEnabled\": true"));

        var app = await StartedAsync(rig);

        Assert.NotNull(Field<object?>(app, "_perfListener"));
        Assert.NotNull(Field<object?>(app, "_perfHooks"));
        Assert.NotEmpty(Directory.GetFiles(trace.Path, "perf-*.csv"));
        await DisposeAppAsync(rig, app);
        Assert.Null(Field<object?>(app, "_perfListener"));
        Assert.Contains("Perf trace enabled (PHOTOREVIEW_PERF_TRACE)", rig.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCore_WithoutPerfTraceEnvironment_StartsNoListenerAndNoHooks()
    {
        using var rig = new Rig();

        var app = await StartedAsync(rig);

        Assert.Null(Field<object?>(app, "_perfListener"));
        Assert.Null(Field<object?>(app, "_perfHooks"));
    }

    // ---- crash hooks and exit -----------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_DispatcherException_IsLoggedAsCrashReportAndMarkedHandled()
    {
        using var rig = new Rig();
        var app = await StartedAsync(rig);

        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = rig.Ui.BeginInvoke(() => throw new InvalidOperationException("boom-dispatcher"));
        _ = rig.Ui.BeginInvoke(() => after.SetResult()); // runs only when the dispatcher survived the exception above
        await after.Task.WaitAsync(Bound);

        var log = rig.ReadLog();
        Assert.Contains("Dispatcher exception", log, StringComparison.Ordinal);
        Assert.Contains("boom-dispatcher", log, StringComparison.Ordinal);
        Assert.NotNull(MainWindowOf(rig, app));
    }

    [Fact]
    public async Task StartupCore_AppDomainException_IsLoggedAsCrashReport()
    {
        using var rig = new Rig();
        await StartedAsync(rig);

        // AppDomain.UnhandledException forwards to AppContext.UnhandledException, a static field-like event.
        var field = typeof(AppContext).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(UnhandledExceptionEventHandler));
        var handlers = ((Delegate)field.GetValue(null)!).GetInvocationList()
            .Where(d => d.Method.DeclaringType?.DeclaringType == typeof(App)).ToList();
        Assert.NotEmpty(handlers);
        foreach (var handler in handlers)
            handler.DynamicInvoke(AppDomain.CurrentDomain, new UnhandledExceptionEventArgs(new InvalidOperationException("boom-domain"), false));

        var log = rig.ReadLog();
        Assert.Contains("AppDomain exception", log, StringComparison.Ordinal);
        Assert.Contains("boom-domain", log, StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFaultedTask()
    {
        var source = new TaskCompletionSource();
        source.SetException(new InvalidOperationException("boom-unobserved"));
    }

    [Fact]
    public async Task StartupCore_UnobservedTaskException_IsLoggedAsCrashReport()
    {
        using var rig = new Rig();
        await StartedAsync(rig);

        AbandonFaultedTask();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var log = rig.ReadLog();
        Assert.Contains("Unobserved task exception", log, StringComparison.Ordinal);
        Assert.Contains("boom-unobserved", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCore_ExitEvent_DisposesTheApp()
    {
        using var rig = new Rig();
        var app = await StartedAsync(rig);
        Assert.NotNull(Field<object?>(app, "_services"));

        var events = typeof(Application).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        var key = typeof(Application).GetField("EVENT_EXIT", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var handler = (Delegate)events.GetType().GetProperty("Item")!.GetValue(events, [key])!;
        await rig.OnUiAsync(() => { handler.DynamicInvoke(app, Activator.CreateInstance(typeof(ExitEventArgs), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [0], null)); return Task.CompletedTask; });

        Assert.Null(Field<object?>(app, "_services"));
        Assert.Null(Field<object?>(app, "_instanceScope"));
    }

    // ---- a second launch ----------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupCore_SecondLaunchDeliveredToTheOwner_ForwardsTheExistingPathsAndExitsSilently()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");
        var file = photos.File("a.jpg", 1);
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new PhotoReview.Platform.Windows.InstanceScope(
            InstanceMode.SingleWindow, paths => received.TrySetResult(paths), NullLog.Instance, rig.Prefix);
        Assert.True(owner.TryAcquire(null));

        var app = await StartedAsync(rig, file, Path.Combine(photos.Path, "missing.jpg"));

        Assert.Equal([file], await received.Task.WaitAsync(Bound));
        Assert.Empty(rig.Dialogs.Messages);
        Assert.Null(MainWindowOf(rig, app));
        Assert.Null(Field<object?>(app, "_instanceScope"));
        Assert.Null(Field<object?>(app, "_forwardCoalescer"));
        Assert.True(AppBase<bool>(app, "_isShuttingDown"));
        Assert.Equal(0, AppBase<int>(app, "_exitCode"));
    }

    [Fact]
    public async Task StartupCore_SecondLaunchWithoutAnswer_ShowsTheNoResponseMessageForTheSingleWindowMode()
    {
        using var rig = new Rig();
        using var stale = new MutexHolder(PhotoReview.Platform.Windows.InstanceKeys.For(InstanceMode.SingleWindow, null, rig.Prefix).MutexName);

        var app = await StartedAsync(rig);

        var (title, message) = Assert.Single(rig.Dialogs.Messages);
        Assert.Equal(Tr.AppTitle, title);
        Assert.Equal(Tr.AppAlreadyRunningNoResponse, message);
        Assert.Null(MainWindowOf(rig, app));
        Assert.Null(Field<object?>(app, "_instanceScope"));
        Assert.Null(Field<object?>(app, "_forwardCoalescer"));
        Assert.True(AppBase<bool>(app, "_isShuttingDown"));
    }

    [Fact]
    public async Task StartupCore_SecondLaunchWithoutAnswer_ShowsTheFolderOpenMessageForThePerFolderMode()
    {
        using var rig = new Rig();
        using var photos = new TempRoot("startup");
        rig.WriteConfig(ConfigJson("\"InstanceMode\": 1"));
        using var stale = new MutexHolder(PhotoReview.Platform.Windows.InstanceKeys.For(InstanceMode.PerFolder, photos.Path, rig.Prefix).MutexName);

        var app = await StartedAsync(rig, photos.Path);

        var (_, message) = Assert.Single(rig.Dialogs.Messages);
        Assert.Equal(Tr.FolderAlreadyOpenInOtherInstance, message);
        Assert.Null(MainWindowOf(rig, app));
    }

    // ---- App_Startup / FailStartup -----------------------------------------------------------------------------

    private static StartupEventArgs NewStartupArgs(params string[] args)
    {
        var e = (StartupEventArgs)Activator.CreateInstance(typeof(StartupEventArgs), nonPublic: true)!;
        typeof(StartupEventArgs).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(string[])).SetValue(e, args);
        return e;
    }

    [Fact]
    public async Task AppStartup_Success_ShowsTheWindowWithoutFailing()
    {
        using var rig = new Rig();
        rig.WriteConfig(ConfigJson("\"ImageCacheRamPercent\": 500"));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Dialogs.OnMessage = _ => done.TrySetResult();

        await rig.OnUiAsync(async () =>
        {
            var app = rig.NewApp();
            AppStartup.Invoke(app, [app, NewStartupArgs()]);
            await done.Task; // the repairs message is the last step of StartupCoreAsync
        });

        var current = rig.App!;
        Assert.IsType<MainWindow>(MainWindowOf(rig, current));
        Assert.Empty(rig.Dialogs.Errors);
        Assert.Equal(0, AppBase<int>(current, "_exitCode"));
        Assert.False(AppBase<bool>(current, "_isShuttingDown"));
    }

    [Fact]
    public async Task AppStartup_StartupFault_ReportsTheErrorLogsItAndShutsDownWithExitCodeOne()
    {
        using var rig = new Rig();
        rig.Explorer.PrefetchFault = new InvalidOperationException("boom-startup");
        using var photos = new TempRoot("startup");

        await rig.OnUiAsync(() =>
        {
            var app = rig.NewApp();
            // The Explorer prefetch of the launch folder throws after the services exist, so FailStartup reports through the registered dialog service.
            AppStartup.Invoke(app, [app, NewStartupArgs(photos.Path)]);
            return Task.CompletedTask;
        });

        var current = rig.App!;
        var (title, message) = Assert.Single(rig.Dialogs.Errors);
        Assert.Equal(Tr.AppTitle, title);
        Assert.Equal(Tr.AppStartupFailed("boom-startup"), message);
        Assert.Contains("Startup failed", rig.ReadLog(), StringComparison.Ordinal);
        Assert.Contains("boom-startup", rig.ReadLog(), StringComparison.Ordinal);
        Assert.Equal(1, AppBase<int>(current, "_exitCode"));
        Assert.True(AppBase<bool>(current, "_isShuttingDown"));
        Assert.Null(MainWindowOf(rig, current));
        Assert.Null(Field<object?>(current, "_services")); // Dispose ran
    }

    [Fact]
    public async Task FailStartup_WithServices_ShowsTheErrorThroughTheRegisteredDialogServiceAndDisposesTheApp()
    {
        using var rig = new Rig();
        await rig.OnUiAsync(() =>
        {
            var app = rig.NewApp();
            SetField(app, "_services", PhotoReview.App.Composition.AppHost.BuildServices(App.StartupServiceOverrides));
            FailStartup.Invoke(app, [new InvalidOperationException("boom-fail")]);
            return Task.CompletedTask;
        });

        var current = rig.App!;
        var (title, message) = Assert.Single(rig.Dialogs.Errors);
        Assert.Equal(Tr.AppTitle, title);
        Assert.Equal(Tr.AppStartupFailed("boom-fail"), message);
        Assert.False(rig.Guard!.HasDialogs);
        Assert.Null(Field<object?>(current, "_services")); // Dispose ran
        Assert.Equal(1, AppBase<int>(current, "_exitCode"));
        Assert.True(AppBase<bool>(current, "_isShuttingDown"));
        Assert.Contains("Startup failed", rig.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailStartup_WhenTheDialogThrows_StillDisposesAndShutsDown()
    {
        using var rig = new Rig();
        rig.Dialogs.ThrowOnError = true;
        await rig.OnUiAsync(() =>
        {
            var app = rig.NewApp();
            SetField(app, "_services", PhotoReview.App.Composition.AppHost.BuildServices(App.StartupServiceOverrides));
            FailStartup.Invoke(app, [new InvalidOperationException("boom-dialog")]);
            return Task.CompletedTask;
        });

        var current = rig.App!;
        Assert.Empty(rig.Dialogs.Errors);
        Assert.Null(Field<object?>(current, "_services"));
        Assert.Equal(1, AppBase<int>(current, "_exitCode"));
        Assert.True(AppBase<bool>(current, "_isShuttingDown"));
    }
}
