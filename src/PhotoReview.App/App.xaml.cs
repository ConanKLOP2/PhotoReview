using System.Windows;
using System.IO;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using PhotoReview.App.Diagnostics;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;

namespace PhotoReview.App;

public partial class App : System.Windows.Application
{
    private static readonly IAppPaths AppPaths = PhotoReview.Core.AppPaths.FromEnvironment();
    private static readonly IFileSystem FileSystem = new PhysicalFileSystem();
    private static readonly ILog Log = FileLog.Default;
    private static readonly SettingsStore Store = new(AppPaths, FileSystem, Log, LogStartupErrorForced);

    private InstanceLock? _instanceLock;
    private PerfCsvListener? _perfListener;
    private PerfDispatcherHooks? _perfHooks;
    private void App_Startup(object sender, StartupEventArgs e)
    {
        PhotoReview.App.Services.WpfKeyNameValidator.WireUp();
        Store.Changed += (_, settings) => AppLog.Enabled = settings.LoggingEnabled;
        var appSettings = Store.Load();
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
        Exit += (_, _) => { AppLog.Shutdown(); _instanceLock?.Dispose(); _perfHooks?.Detach(); _perfListener?.Dispose(); };
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
        var window = new MainWindow(initial ?? initialFolder, Store);
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
