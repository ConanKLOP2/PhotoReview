using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PhotoReview.App;
using Xunit;

namespace PhotoReview.Integration.Tests.Infrastructure;

/// <summary>
/// Runs a test body on a single, long-lived STA thread that owns a WPF <see cref="Application"/>
/// and a <see cref="Dispatcher"/>, so WPF windows (MainWindow) can be constructed and driven from
/// xUnit. The thread does not call <see cref="Dispatcher.Run"/>; each <see cref="RunAsync(Func{Task})"/>
/// pushes its own <see cref="DispatcherFrame"/> and pumps only for the duration of that body.
/// </summary>
public static class StaTestHost
{
    /// <summary>A hung UI body must fail the test instead of hanging the whole run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static readonly BlockingCollection<Action> Work = new();
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;

    /// <summary>The STA thread's dispatcher. Only valid inside a <see cref="RunAsync(Func{Task})"/> body.</summary>
    public static Dispatcher Dispatcher => _dispatcher ?? throw new InvalidOperationException("STA host not started.");

    public static Task RunAsync(Func<Task> body) => RunAsync(body, DefaultTimeout);

    public static Task RunAsync(Func<Task> body, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureThread();
        var outer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() => Pump(body, timeout, outer));
        return outer.Task;
    }

    private static void EnsureThread()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                // Dispatcher.PushFrame does not install one, and Application.Run is never called
                // here. Without it every `await` inside MainWindow (and inside a test body)
                // resumes on a thread-pool thread and WPF throws a cross-thread access error.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_dispatcher));
                WpfResourceLookup.RedirectToApplicationAssembly();
                if (Application.Current is null)
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.Set();
                foreach (var action in Work.GetConsumingEnumerable()) action();
            })
            {
                IsBackground = true,
                Name = "PhotoReview STA test host",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            _thread = thread;
        }
    }

    private static void Pump(Func<Task> body, TimeSpan timeout, TaskCompletionSource<object?> outer)
    {
        var dispatcher = _dispatcher!;
        var frame = new DispatcherFrame();
        var timedOut = false;
        Task? inner = null;

        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = timeout };
        timer.Tick += (_, _) => { timedOut = true; frame.Continue = false; };

        try
        {
            timer.Start();
            inner = body();
            // Completion can already have happened; the posted callback is then simply the first
            // item the frame drains, so PushFrame returns immediately instead of blocking.
            inner.ContinueWith(
                _ => dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        catch (Exception ex)
        {
            timer.Stop();
            outer.TrySetException(ex);
            return;
        }
        timer.Stop();

        if (!inner.IsCompleted)
        {
            outer.TrySetException(new TimeoutException(
                $"STA test body did not complete within {timeout.TotalSeconds:0.##}s (timedOut={timedOut})."));
            return;
        }
        if (inner.IsFaulted) outer.TrySetException(inner.Exception!.InnerExceptions);
        else if (inner.IsCanceled) outer.TrySetCanceled();
        else outer.TrySetResult(null);
    }

    /// <summary>
    /// Lets queued dispatcher work (the fire-and-forget folder load MainWindow starts in its
    /// constructor) run until <paramref name="condition"/> holds or the deadline passes.
    /// </summary>
    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        // TC09: Replace Task.Delay with dispatcher pumps; pump multiple times per iteration
        // to ensure deferred work gets a chance to execute without explicit waits.
        const int PumpsPerIteration = 3;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            for (int i = 0; i < PumpsPerIteration; i++)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }
        return true;
    }
}

/// <summary>
/// Points WPF's "pack://application:,,,/..." lookup at PhotoReview.App.
/// <para>
/// An assembly-less application pack URI resolves against <c>Application.ResourceAssembly</c>,
/// which defaults to <c>Assembly.GetEntryAssembly()</c> — here that is testhost.exe, which holds
/// none of the app's resources, so <c>MainWindow.InitializeComponent()</c> fails on
/// <c>Icon="pack://application:,,,/Assets/PhotoReview.ico"</c>. The supported setter
/// (<c>Application.ResourceAssembly = ...</c>) is refused inside the test host (it is one-shot and
/// already latched by the time any of our code runs, including a module initializer), so the
/// harness writes WPF's backing state directly. This is test-only infrastructure; the app itself
/// never needs it, and a failure here surfaces as a loud, diagnosable test failure.
/// </para>
/// </summary>
internal static class WpfResourceLookup
{
    private static bool _done;

    internal static void RedirectToApplicationAssembly()
    {
        if (_done) return;
        _done = true;

        var app = typeof(MainWindow).Assembly;
        if (TrySetPublicResourceAssembly(app)) return;

        var notes = new List<string>();
        const BindingFlags AnyStatic = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        var appField = typeof(Application).GetField("_resourceAssembly", AnyStatic);
        if (appField is null) notes.Add("Application._resourceAssembly missing");
        else appField.SetValue(null, app);

        // BaseUriHelper is what actually resolves application pack URIs. Which WPF assembly
        // declares it has moved between releases, so look it up by name.
        var baseUriHelper = FindType("MS.Internal.BaseUriHelper") ?? FindTypeBySimpleName("BaseUriHelper");
        if (baseUriHelper is null) notes.Add("MS.Internal.BaseUriHelper missing");
        else
        {
            var property = baseUriHelper.GetProperty("ResourceAssembly", AnyStatic);
            if (property?.SetMethod is not null) property.SetValue(null, app);
            else
            {
                var field = baseUriHelper.GetField("_resourceAssembly", AnyStatic);
                if (field is null) notes.Add("BaseUriHelper.ResourceAssembly not writable");
                else field.SetValue(null, app);
            }
        }

        // Drop any resource manager already bound to the old assembly.
        FindType("MS.Internal.AppModel.ResourceContainer")?.GetField("_resourceManagerWrapper", AnyStatic)?.SetValue(null, null);

        if (notes.Count > 0)
            throw new InvalidOperationException(
                "Could not point WPF resource lookup at PhotoReview.App: " + string.Join("; ", notes));
    }

    private static Type? FindTypeBySimpleName(string name)
    {
        foreach (var assembly in new[] { typeof(Application).Assembly, typeof(DependencyObject).Assembly, typeof(System.Windows.Media.Brush).Assembly })
        {
            Type?[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            foreach (var type in types)
                if (type is not null && type.Name == name) return type;
        }
        return null;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var found = assembly.GetType(fullName, throwOnError: false);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool TrySetPublicResourceAssembly(Assembly app)
    {
        try
        {
            Application.ResourceAssembly = app;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// T14a smoke test. It lives beside the harness because the task's Files list allows no new test
/// file; it is an ordinary xUnit fact and runs in the "GlobalState" collection because hosting
/// MainWindow mutates the process-wide PHOTOREVIEW_DATA_ROOT.
/// </summary>
[Collection("GlobalState")]
public sealed class StaTestHostSmokeTests
{
    [Fact]
    public async Task OpeningAFolderPresentsTheFirstImage()
    {
        using var root = new TempRoot("sta-smoke");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var expected = Path.Combine(folder, "a.png");
        foreach (var name in new[] { "a.png", "b.png", "c.png" })
            File.WriteAllBytes(Path.Combine(folder, name), Png(16, 16));

        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var hooks = new MainWindowTestHooks
                {
                    OnPresented = presented.Add,
                };
                window = new MainWindow(folder, hooks);
                var ok = await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(20));
                // StatusText carries the window's own explanation when a load fails, which is the
                // only useful diagnostic for a headless window that never presented anything.
                Assert.True(ok, $"No image was presented before the deadline. StatusText={window.StatusText.Text}");
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    // Releases the preload scheduler, thumbnail cache and Explorer STA pump this
                    // window owns. A never-shown window can refuse Close(); leaking it for the
                    // rest of the run is acceptable, failing the test over cleanup is not.
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }

        Assert.Equal(expected, Assert.Single(presented), ignoreCase: true);
    }

    /// <summary>A minimal, valid 8-bit RGBA PNG of the requested size, written by hand.</summary>
    private static byte[] Png(int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 4);
            raw[row] = 0; // filter type: none
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * 4;
                raw[p] = 0x40; raw[p + 1] = 0x80; raw[p + 2] = 0xC0; raw[p + 3] = 0xFF;
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        var payload = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) payload[i] = (byte)type[i];
        data.CopyTo(payload, 4);
        stream.Write(payload);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(payload)));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
