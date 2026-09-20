using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Core.Diagnostics;

/// <summary>
/// Shared STA host for in-process WPF drivers (<c>--ui-next-probe</c>, <c>--perf-session</c>).
/// Runs one async body on a dedicated STA thread with its own <see cref="Dispatcher"/> and fixes the
/// three problems D04 found in the old probe host:
/// <list type="number">
/// <item>MainWindow.xaml's <c>pack://application:,,,/Assets/PhotoReview.ico</c> is resolved against
/// <see cref="Application.ResourceAssembly"/>, which defaults to the entry assembly (this test exe).
/// The public setter throws once an entry assembly exists, so the host points the private backing
/// fields at the App assembly instead (see <see cref="EnsureResourceAssembly"/>).</item>
/// <item>The perf CSV listener is normally started by <c>App.App_Startup</c>; the host starts it itself
/// when <c>PHOTOREVIEW_PERF_TRACE</c> is set, and disposes it after the body finished.</item>
/// <item>The D04 dispatcher hooks (<c>App.PerfDispatcherHooks</c>, private nested) are attached via
/// reflection while the listener runs. App code is not modified.</item>
/// </list>
/// Never sends OS-level input and never activates/foregrounds a window (PERF-DIAGNOSIS-TASKS rule 4).
/// </summary>
internal static class WpfTestHost
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>Human-readable notes about how the host was set up (printed by callers that want them).</summary>
    public static List<string> Notes { get; } = [];

    public static async Task<T> RunAsync<T>(Func<Dispatcher, Task<T>> body, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                PerfCsvListener? listener = null;
                object? hooks = null;
                try
                {
                    EnsureResourceAssembly();
                    if (Application.Current is null)
                        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    else if (Application.Current.Dispatcher != dispatcher)
                        throw new InvalidOperationException("WpfTestHost supports one Application per process (already created on another thread)");
                    listener = PerfCsvListener.TryStartFromEnvironment();
                    if (listener is not null)
                    {
                        hooks = AttachPerfHooks(dispatcher);
                    }
                    completion.TrySetResult(await body(dispatcher));
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally
                {
                    try
                    {
                        foreach (var window in Application.Current?.Windows.OfType<Window>().ToArray() ?? [])
                            window.Close();
                    }
                    catch { /* best effort */ }
                    DetachPerfHooks(hooks);
                    listener?.Dispose();
                    Application.Current?.Shutdown();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try
        {
            return await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Makes pack://application:,,,/ URIs resolve against PhotoReview.App. <c>Application.ResourceAssembly</c>'s
    /// setter throws <see cref="InvalidOperationException"/> when an entry assembly exists, so we try it
    /// first and then fall back to the private static fields it would have written
    /// (<c>Application._resourceAssembly</c> and <c>BaseUriHelper.ResourceAssembly</c>).
    /// </summary>
    public static void EnsureResourceAssembly()
    {
        var appAssembly = typeof(MainWindow).Assembly;
        if (Application.ResourceAssembly == appAssembly) return;
        try
        {
            Application.ResourceAssembly = appAssembly;
            Notes.Add("ResourceAssembly: public setter");
            return;
        }
        catch (InvalidOperationException)
        {
            // Expected in an exe: the setter is only allowed when there is no entry assembly.
        }

        var field = typeof(Application).GetField("_resourceAssembly", Static)
            ?? throw new InvalidOperationException("Cannot redirect ResourceAssembly: Application._resourceAssembly not found");
        field.SetValue(null, appAssembly);
        var helper = typeof(Application).Assembly.GetType("MS.Internal.AppModel.BaseUriHelper")
            ?? typeof(UIElement).Assembly.GetType("System.Windows.Navigation.BaseUriHelper")
            ?? typeof(Application).Assembly.GetType("System.Windows.Navigation.BaseUriHelper");
        var helperProperty = helper?.GetProperty("ResourceAssembly", Static);
        helperProperty?.SetValue(null, appAssembly);
        Notes.Add($"ResourceAssembly: reflection (Application._resourceAssembly{(helperProperty is null ? "" : $" + {helper!.Name}.ResourceAssembly")})");
    }

    /// <summary>
    /// Stops MainWindow from restoring/saving the user's real window-placement.json: marks placement as
    /// already restored (Window_Loaded would otherwise call SetWindowPlacement, which can show/activate the
    /// window and move it) and unhooks the XAML Closing handler that saves placement.
    /// </summary>
    public static void SuppressWindowPlacement(MainWindow window)
    {
        typeof(MainWindow).GetField("_placementRestored", Instance)!.SetValue(window, true);
        var closing = typeof(MainWindow).GetMethod("Window_Closing", Instance)!;
        window.Closing -= closing.CreateDelegate<CancelEventHandler>(window);
    }

    private static object? AttachPerfHooks(Dispatcher dispatcher)
    {
        var type = typeof(App).GetNestedType("PerfDispatcherHooks", BindingFlags.NonPublic);
        var attach = type?.GetMethod("Attach", Static, [typeof(Dispatcher)]);
        if (attach is null)
        {
            Notes.Add("DispatcherHooks: App.PerfDispatcherHooks not reachable via reflection; DispatcherLongOp disabled");
            return null;
        }
        var hooks = attach.Invoke(null, [dispatcher]);
        type!.GetMethod("TraceDiagMode", Static, Type.EmptyTypes)?.Invoke(null, null);
        Notes.Add(hooks is null ? "DispatcherHooks: Attach returned null" : "DispatcherHooks: App.PerfDispatcherHooks attached via reflection");
        return hooks;
    }

    private static void DetachPerfHooks(object? hooks)
    {
        if (hooks is null) return;
        try { hooks.GetType().GetMethod("Detach", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(hooks, null); }
        catch { /* best effort */ }
    }
}
