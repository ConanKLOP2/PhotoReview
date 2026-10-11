using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Raw;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit.Abstractions;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Soak / leak tests: the real production graph (<see cref="AppHost.BuildServices"/> -> <see cref="MainWindow"/>,
/// shown off-screen, no OS input) is driven thousands of times over a generated folder of mid-size JPEGs while the
/// process is sampled (managed heap after a forced collection, working set, private bytes, handles, GDI/USER objects,
/// threads, cache bytes, live decoded images). Each test asserts a plateau (growth over the measured window below a
/// threshold with margin) and that the caches stay inside their configured budgets, and prints the measured series.
/// Heavy: Category=Slow, one folder of 200 files of 3000x2000 generated once per class run in a temp dir.
/// </summary>
[Trait("Category", "UI")]
[Trait("Category", "Slow")]
[Trait("Category", "Integration")]
[Collection("GlobalState")]
public sealed class SoakLeakTests(SoakFolderFixture folder, ITestOutputHelper output) : IClassFixture<SoakFolderFixture>
{
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(100);

    [Fact(DisplayName = "Soak: 3000 Next/Previous/Jump navigations over 200 JPEGs plateau and keep caches within budget")]
    public async Task Navigation_Soak_Plateaus()
    {
        using var dataRoot = new DataRootFixture();
        var series = new List<SoakSample>();
        SoakSession? session = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                session = SoakSession.Create(folder.Dir);
                await session.OpenAsync(folder.Dir);
                var vm = session.Window.ViewModel;
                var rng = new Random(12345);
                var count = vm.TotalFiles;
                Assert.Equal(SoakFolderFixture.FileCount, count);

                // Warm-up: one pass over the folder so the cache is full and evicting, then settle.
                for (var i = 0; i < 260; i++) await StepForwardAsync(vm);
                await session.SettleAsync();
                series.Add(session.Sample("warm-up"));

                const int rounds = 12;
                for (var round = 1; round <= rounds; round++)
                {
                    for (var i = 0; i < 100; i++) await StepForwardAsync(vm);
                    for (var i = 0; i < 60; i++) await vm.PreviousAsync();
                    for (var i = 0; i < 40; i++) await session.Window.ShowImageAsync(rng.Next(count));
                    await vm.FirstAsync();
                    await vm.LastAsync();
                    // Rapid key-repeat shape: several navigations queued without awaiting each one.
                    var burst = new List<Task>();
                    for (var i = 0; i < 24; i++) burst.Add(i % 3 == 2 ? vm.PreviousAsync() : vm.NextAsync());
                    await Task.WhenAll(burst);
                    await session.SettleAsync();
                    series.Add(session.Sample($"round {round} (+{round * 250} navs)"));
                }
            }, HostTimeout);
            Report("navigation soak", series);
            SoakAsserts.AssertWithinBudgets(series);
            SoakAsserts.AssertPlateau(series, SoakLimits.Navigation);
        }
        finally
        {
            await CloseAsync(session);
        }
    }

    [Fact(DisplayName = "Soak: open-folder cycles (big folder <-> small folder) plateau")]
    public async Task OpenFolder_Soak_Plateaus()
    {
        await RunCycleSoakAsync("open-folder cycles", async (session, _) =>
        {
            var vm = session.Window.ViewModel;
            for (var i = 0; i < 4; i++)
            {
                await session.OpenAsync(folder.SmallDir);
                for (var n = 0; n < 8; n++) await StepForwardAsync(vm);
                await session.OpenAsync(folder.Dir);
                for (var n = 0; n < 8; n++) await StepForwardAsync(vm);
            }
        });
        Assert.Equal(SoakFolderFixture.SmallCount, Directory.GetFiles(folder.SmallDir).Length);
    }

    [Fact(DisplayName = "Soak: compare toggle cycles over a real pair plateau")]
    public async Task Compare_Soak_Plateaus()
    {
        var toggles = 0;
        await RunCycleSoakAsync("compare cycles", async (session, _) =>
        {
            var vm = session.Window.ViewModel;
            await session.Window.ShowImageAsync(SoakFolderFixture.PairIndex(vm));
            for (var i = 0; i < 12; i++)
            {
                vm.ToggleCompare();
                await vm.CompareToggleTask;
                vm.ToggleCompare();
                await vm.CompareToggleTask;
                toggles += 2;
            }
        });
        Assert.Equal(8 * 24, toggles);
    }

    [Fact(DisplayName = "Soak: move + undo cycles on real temp files plateau and restore every file")]
    public async Task MoveUndo_Soak_Plateaus()
    {
        var moved = 0;
        using var scratch = new TempRoot("soak-undo");
        await RunCycleSoakAsync("move+undo cycles", async (session, destination) =>
        {
            var vm = session.Window.ViewModel;
            for (var i = 0; i < 8; i++)
            {
                await session.Window.ShowImageAsync(i * 3);
                var before = vm.TotalFiles;
                await vm.RunActionAsync(0);
                await vm.WhenFileActionIdleAsync();
                Assert.Equal(before - 1, vm.TotalFiles);
                moved++;
                await vm.UndoAsync();
                await vm.WhenFileActionIdleAsync();
                Assert.Equal(before, vm.TotalFiles);
            }
        }, scratch.Dir("sorted"));
        Assert.Empty(Directory.GetFiles(scratch.Combine("sorted")));
        output.WriteLine($"move+undo cycles completed: {moved}");
    }

    private async Task RunCycleSoakAsync(string name, Func<SoakSession, string, Task> oneRound, string? destination = null)
    {
        using var dataRoot = new DataRootFixture();
        var series = new List<SoakSample>();
        SoakSession? session = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                session = SoakSession.Create(folder.Dir);
                await session.OpenAsync(folder.Dir);
                var vm = session.Window.ViewModel;
                if (destination is not null) session.InstallMoveAction(destination);
                // Two full passes: the cache must have turned over completely (decoder-built images replaced by the
                // disk-cache-restored ones, which changes the managed/native split) before the baseline is taken.
                for (var i = 0; i < 420; i++) await StepForwardAsync(vm);
                await session.SettleAsync();
                series.Add(session.Sample("warm-up"));
                for (var round = 1; round <= 8; round++)
                {
                    await oneRound(session, destination ?? "");
                    await session.SettleAsync();
                    series.Add(session.Sample($"round {round}"));
                }
            }, HostTimeout);
            Assert.Equal(SoakFolderFixture.FileCount, Directory.GetFiles(folder.Dir).Length);
            Report(name, series);
            SoakAsserts.AssertWithinBudgets(series);
            SoakAsserts.AssertPlateau(series, SoakLimits.Cycles);
        }
        finally
        {
            await CloseAsync(session);
        }
    }

    [Fact(DisplayName = "Soak: 12 MainWindow create/open/navigate/close cycles leave nothing alive and plateau")]
    public async Task WindowLifecycle_Soak_ReleasesEverything()
    {
        using var dataRoot = new DataRootFixture();
        var series = new List<SoakSample>();
        var survivors = new List<string>();
        await StaTestHost.RunAsync(async () =>
        {
            const int cycles = 12;
            var probe = new SoakSession.Probe();
            for (var cycle = 0; cycle <= cycles; cycle++)
            {
                var weak = await RunOneWindowAsync(folder.SmallDir);
                await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(250));
                SoakSession.ForceFullGc();
                var alive = weak.Where(w => w.Value.TryGetTarget(out _)).Select(w => w.Key).ToList();
                if (cycle > 0) survivors.AddRange(alive.Select(a => $"cycle {cycle}: {a}"));
                series.Add(SoakSample.Take($"cycle {cycle}", null, null, null, probe));
            }
        }, HostTimeout);
        Report("window lifecycle soak", series);
        // Every cycle's window, view model, preview service and preload scheduler must be collectable after Close().
        Assert.True(survivors.Count == 0, "Objects still alive after Close() + forced GC: " + string.Join("; ", survivors));
        SoakAsserts.AssertPlateau(series, SoakLimits.WindowLifecycle);

        async Task<Dictionary<string, WeakReference<object>>> RunOneWindowAsync(string dir)
        {
            var session = SoakSession.Create(dir);
            await session.OpenAsync(dir);
            for (var i = 0; i < 12; i++) await StepForwardAsync(session.Window.ViewModel);
            await session.SettleAsync();
            var weak = session.WeakHandles();
            await session.CloseAsync();
            return weak;
        }
    }

    private static async Task StepForwardAsync(PhotoReview.App.ViewModels.MainViewModel vm)
    {
        if (vm.CanNavigateNext) await vm.NextAsync();
        else await vm.FirstAsync();
    }


    private static async Task CloseAsync(SoakSession? session)
    {
        if (session is null) return;
        await StaTestHost.RunAsync(() => session.CloseAsync());
    }

    private void Report(string name, IReadOnlyList<SoakSample> series)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"=== {name} ===");
        text.AppendLine(SoakSample.Header);
        foreach (var s in series) text.AppendLine(s.ToRow());
        output.WriteLine(text.ToString());
        // Also on the console so a bare `dotnet test --logger "console;verbosity=detailed"` run shows the series.
        Console.WriteLine(text.ToString());
    }
}

/// <summary>Thresholds per scenario, derived from measured series (see the PR description) with a >= 2x margin.</summary>
internal sealed record SoakLimits(long ManagedBytes, long PrivateBytes, long WorkingSetBytes, int Handles, int GdiObjects, int UserObjects, int Threads, int LiveImages)
{
    // Measured growth (median of last 3 minus first 3 post-warm-up samples): managed ~5 MB, private/working set <= ~25 MB,
    // handles/threads/USER <= +8 (they fall as often as they rise), GDI 0, live images 0. Limits are >= 2x those.
    public static readonly SoakLimits Navigation = new(
        ManagedBytes: 48L << 20, PrivateBytes: 150L << 20, WorkingSetBytes: 150L << 20, Handles: 40, GdiObjects: 10, UserObjects: 15, Threads: 10, LiveImages: 12);

    public static readonly SoakLimits Cycles = new(
        ManagedBytes: 48L << 20, PrivateBytes: 150L << 20, WorkingSetBytes: 150L << 20, Handles: 40, GdiObjects: 10, UserObjects: 15, Threads: 10, LiveImages: 12);

    public static readonly SoakLimits WindowLifecycle = new(
        ManagedBytes: 64L << 20, PrivateBytes: 300L << 20, WorkingSetBytes: 300L << 20, Handles: 100, GdiObjects: 60, UserObjects: 30, Threads: 20, LiveImages: 12);
}

internal static class SoakAsserts
{
    /// <summary>Median of the last three samples minus the median of the first three after warm-up (robust to one noisy sample).</summary>
    private static T Growth<T>(IReadOnlyList<SoakSample> series, Func<SoakSample, T> pick, Func<T, T, T> subtract) where T : IComparable<T>
    {
        var values = series.Skip(1).Select(pick).ToList(); // series[0] is the warm-up sample
        var head = Median(values.Take(3).ToList());
        var tail = Median(values.Skip(Math.Max(0, values.Count - 3)).ToList());
        return subtract(tail, head);

        static T Median(List<T> list)
        {
            list.Sort();
            return list[list.Count / 2];
        }
    }

    public static void AssertPlateau(IReadOnlyList<SoakSample> series, SoakLimits limits)
    {
        Assert.True(series.Count >= 6, $"need at least 6 samples, got {series.Count}");
        var failures = new List<string>();
        void Check<T>(string name, Func<SoakSample, T> pick, Func<T, T, T> sub, T limit) where T : IComparable<T>
        {
            var growth = Growth(series, pick, sub);
            if (growth.CompareTo(limit) > 0) failures.Add($"{name} grew by {growth} (limit {limit})");
        }

        Check("managed heap bytes", s => s.ManagedBytes, (a, b) => a - b, limits.ManagedBytes);
        Check("private bytes", s => s.PrivateBytes, (a, b) => a - b, limits.PrivateBytes);
        Check("working set bytes", s => s.WorkingSetBytes, (a, b) => a - b, limits.WorkingSetBytes);
        Check("handles", s => s.Handles, (a, b) => a - b, limits.Handles);
        Check("GDI objects", s => s.GdiObjects, (a, b) => a - b, limits.GdiObjects);
        Check("USER objects", s => s.UserObjects, (a, b) => a - b, limits.UserObjects);
        Check("threads", s => s.Threads, (a, b) => a - b, limits.Threads);
        Check("live decoded images", s => s.LiveImages, (a, b) => a - b, limits.LiveImages);
        Assert.True(failures.Count == 0, "Growth beyond the plateau threshold (suspected leak): " + string.Join("; ", failures));
    }

    public static void AssertWithinBudgets(IReadOnlyList<SoakSample> series)
    {
        foreach (var s in series)
        {
            Assert.True(s.CacheCapacityBytes > 0, "preview cache capacity unknown");
            Assert.True(s.CacheBytes <= s.CacheCapacityBytes,
                $"{s.Label}: preview cache holds {s.CacheBytes} bytes, over its {s.CacheCapacityBytes} budget");
            Assert.True(s.SourceBytes <= s.SourceCapacityBytes,
                $"{s.Label}: source-bytes cache holds {s.SourceBytes} bytes, over its {s.SourceCapacityBytes} budget");
            // The cache budget counts decoded pixels only; the process also holds managed copies and a baseline. Measured steady
            // state: private bytes ~1.25x the budget at a 1.3 GB budget. A 1.6x + 256 MiB ceiling catches a cache that stops evicting.
            Assert.True(s.PrivateBytes <= s.CacheCapacityBytes * 8 / 5 + (256L << 20),
                $"{s.Label}: private bytes {s.PrivateBytes} far above the {s.CacheCapacityBytes} cache budget");
            // Decoded images alive after a forced GC are the cache's entries plus a few the view/presenter/preload hold.
            Assert.True(s.LiveImages <= s.CacheCount + 12,
                $"{s.Label}: {s.LiveImages} decoded images alive but only {s.CacheCount} cached (decoded images leaked outside the cache)");
        }
    }
}

internal sealed record SoakSample(
    string Label, long ManagedBytes, long WorkingSetBytes, long PrivateBytes, int Handles, int GdiObjects, int UserObjects, int Threads,
    long CacheBytes, long CacheCapacityBytes, int CacheCount, long SourceBytes, long SourceCapacityBytes, int LiveImages, long TotalDecoded)
{
    public const string Header = "label | managedMB | wsMB | privMB | handles | gdi | user | threads | cacheMB/budgetMB (n) | srcMB/budgetMB | liveImages | decodedTotal";

    public string ToRow() => string.Create(CultureInfo.InvariantCulture,
        $"{Label} | {Mb(ManagedBytes)} | {Mb(WorkingSetBytes)} | {Mb(PrivateBytes)} | {Handles} | {GdiObjects} | {UserObjects} | {Threads} | {Mb(CacheBytes)}/{Mb(CacheCapacityBytes)} ({CacheCount}) | {Mb(SourceBytes)}/{Mb(SourceCapacityBytes)} | {LiveImages} | {TotalDecoded}");

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

    internal static SoakSample Take(string label, PhotoReview.Imaging.Caching.PreviewImageService? preview,
        PhotoReview.Imaging.Caching.SourceBytesCache? sourceBytes, SoakDecodeTracker? tracker, SoakSession.Probe probe)
    {
        SoakSession.ForceFullGc();
        var managed = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        var gdi = (int)SoakSession.GetGuiResources(process.Handle, 0);
        var user = (int)SoakSession.GetGuiResources(process.Handle, 1);
        _ = probe;
        return new SoakSample(label, managed, process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, gdi, user, process.Threads.Count,
            preview?.CacheBytes ?? 0, preview?.CapacityBytes ?? 1, preview?.CacheCount ?? 0,
            sourceBytes?.CurrentSize ?? 0, sourceBytes?.CapacityBytes ?? 0, tracker?.LiveCount() ?? 0, tracker?.Total ?? 0);
    }
}

/// <summary>Counts decoded images that are still reachable (weak references, checked after a forced GC).</summary>
internal sealed class SoakDecodeTracker
{
    private readonly object _gate = new();
    private readonly List<WeakReference<PhotoReview.Imaging.Decoding.IDecodedImage>> _images = [];
    private long _total;

    public long Total => Interlocked.Read(ref _total);

    public void Add(PhotoReview.Imaging.Decoding.IDecodedImage image)
    {
        Interlocked.Increment(ref _total);
        lock (_gate) _images.Add(new WeakReference<PhotoReview.Imaging.Decoding.IDecodedImage>(image));
    }

    public int LiveCount()
    {
        lock (_gate)
        {
            _images.RemoveAll(w => !w.TryGetTarget(out _));
            return _images.Count;
        }
    }
}

internal sealed class SoakTrackingDecoder(PhotoReview.Imaging.Decoding.IImageDecoder inner, SoakDecodeTracker tracker) : PhotoReview.Imaging.Decoding.IImageDecoder
{
    public PhotoReview.Imaging.Decoding.IDecodedImage Decode(PhotoReview.Imaging.Decoding.DecodeRequest request)
    {
        var image = inner.Decode(request);
        tracker.Add(image);
        return image;
    }

    public PhotoReview.Imaging.Decoding.ImageInfo ReadInfo(string path) => inner.ReadInfo(path);
}

/// <summary>One production window + DI graph with the preview cache squeezed so the soak actually evicts.</summary>
internal sealed partial class SoakSession
{
    internal sealed class Probe;

    [LibraryImport("user32.dll")]
    internal static partial uint GetGuiResources(nint hProcess, uint flags);

    private ServiceProvider _sp = null!;
    private SoakDecodeTracker _tracker = null!;
    private bool _closed;

    public MainWindow Window { get; private set; } = null!;
    public PhotoReview.Imaging.Caching.PreviewImageService Preview { get; private set; } = null!;
    private PhotoReview.Imaging.Caching.SourceBytesCache? SourceBytes { get; set; }

    public static SoakSession Create(string initialFolder)
    {
        _ = initialFolder;
        var tracker = new SoakDecodeTracker();
        var sp = AppHost.BuildServices(services =>
        {
            // Never the real Recycle Bin (these tests never delete; Move+Undo only).
            services.AddSingleton(ThrowingRecycleBin.Instance);
            // The production factory (App.ConfigureServices), plus a weak-reference tracker around every decode.
            services.AddSingleton<IImageDecoderFactory>(p =>
            {
                var sourceReader = p.GetRequiredService<ISourceReader>();
                var settingsStore = p.GetRequiredService<SettingsStore>();
                var sourceBytesCache = p.GetRequiredService<SourceBytesCachePolicy>().Cache;
                return new ImageDecoderFactory(
                    DecoderProviders.Create(sourceReader,
                        () => settingsStore.Current.RawSupportEnabled || settingsStore.Current.DecoderBackend == DecoderBackend.LibRaw),
                    p.GetService<ILog>(),
                    p.GetService<PhotoReview.Core.Diagnostics.ReviewMetrics>(),
                    (_, standardDecoder) => new SoakTrackingDecoder(
                        new FormatRoutingDecoder(
                            standardDecoder,
                            ServiceFactories.CreateRawDecoder(standardDecoder, sourceReader, sourceBytesCache, WpfBitmapSourceCodec.Instance),
                            () => settingsStore.Current.RawSupportEnabled),
                        tracker));
            });
        });

        // Before anything reads the budgets: a small RAM share, clamped up by the app to the preload-window floor
        // (window images x a fixed minimum box x 4 bytes). The default window (41 images) puts that floor at ~1.3 GB, which a
        // machine with a small screen (CI runner: smaller decode box, ~4 MB per preview) never fills with 200 photos, so the
        // soak silently stopped exercising eviction. A small window (6 images) makes the budget a few hundred MB on any
        // screen/RAM, so the 200-photo folder always overflows the cache and the soak always evicts.
        var store = sp.GetRequiredService<SettingsStore>();
        var settings = store.Current;
        settings.ImageCacheRamPercent = 1;
        settings.PreloadForwardCount = 4;
        settings.PreloadBackwardCount = 1;
        store.Save(settings);

        var window = sp.GetRequiredService<MainWindow>();
        window.ViewModel.InstanceLabel = TestAppHost.TestInstanceLabel;
        window.SuppressWindowPlacement();
        window.Closed += (_, _) => sp.Dispose();
        // Off-screen, not activated, not in the taskbar: no OS input is ever sent.
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.Width = 1920;
        window.Height = 1080;
        window.Show();
        window.InitializeWithInitialPath(null);
        return new SoakSession
        {
            _sp = sp,
            _tracker = tracker,
            Window = window,
            Preview = sp.GetRequiredService<PhotoReview.Imaging.Caching.PreviewImageService>(),
            SourceBytes = sp.GetRequiredService<SourceBytesCachePolicy>().Cache,
        };
    }

    public async Task OpenAsync(string folder)
    {
        // The folder reopens at the position the session remembers, which can be a compare pair (no single CurrentImage),
        // so "presented" is the metrics counter moving, not CurrentImage being set.
        var before = Window.Metrics.Snapshot().PresentedImages;
        await Window.LoadFolderAsync(folder);
        Assert.True(
            await StaTestHost.WaitForAsync(() =>
                Window.ViewModel.TotalFiles > 0
                && string.Equals(Path.GetDirectoryName(Window.Files[0]), folder, StringComparison.OrdinalIgnoreCase)
                && Window.Metrics.Snapshot().PresentedImages > before, TimeSpan.FromSeconds(30)),
            $"folder never presented. StatusText={Window.StatusText.Text}");
    }

    /// <summary>Lets the background preload finish so samples are taken at rest (bounded; never required to be idle).</summary>
    public async Task SettleAsync()
    {
        var idle = Window.ViewModel.PreloadController;
        await StaTestHost.WaitForAsync(() => idle?.IsIdle ?? true, TimeSpan.FromSeconds(20));
        await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(150));
    }

    public SoakSample Sample(string label) => SoakSample.Take(label, Preview, SourceBytes, _tracker, new Probe());

    public void InstallMoveAction(string destination)
    {
        var store = _sp.GetRequiredService<SettingsStore>();
        var settings = store.Current;
        settings.Actions =
        [
            new ReviewAction { Name = "Soak", Shortcut = "F3", Operation = FileOperationType.Move, Destination = destination, Confirm = false },
        ];
        store.Save(settings);
    }

    public Dictionary<string, WeakReference<object>> WeakHandles() => new()
    {
        ["MainWindow"] = new WeakReference<object>(Window),
        ["MainViewModel"] = new WeakReference<object>(Window.ViewModel),
        ["PreviewImageService"] = new WeakReference<object>(Preview),
        ["PreloadController"] = new WeakReference<object>((object?)Window.ViewModel.PreloadController ?? Preview),
    };

    public Task CloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        _closed = true;
        // A never-activated window can refuse Close(); failing the test over cleanup is not wanted.
        try { Window.Close(); } catch (InvalidOperationException) { }
        return Task.CompletedTask;
    }

    internal static void ForceFullGc()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}

/// <summary>Generates the 200-file folder once (a few distinct noisy 3000x2000 JPEGs, copied under many names).</summary>
public sealed class SoakFolderFixture : IAsyncLifetime, IDisposable
{
    public const int FileCount = 200;
    public const int SmallCount = 24;
    private TempRoot? _root;

    public string Dir { get; private set; } = "";
    public string SmallDir { get; private set; } = "";

    /// <summary>Index (in the catalog's current order) of the first "Q*.jpg" that has a numbered twin.</summary>
    internal static int PairIndex(PhotoReview.App.ViewModels.MainViewModel vm)
    {
        for (var i = 0; i < vm.Catalog.Count; i++)
        {
            if (Path.GetFileName(vm.Catalog.PathAt(i)).StartsWith('Q') && vm.Presenter.HasComparePair(vm.Catalog.PathAt(i))) return i;
        }
        throw new InvalidOperationException("no compare pair in the soak folder");
    }

    public async Task InitializeAsync()
    {
        _root = new TempRoot("soak-photos");
        Dir = _root.Dir("big");
        SmallDir = _root.Dir("small");
        byte[][] bases = null!;
        await StaTestHost.RunAsync(() =>
        {
            bases = Enumerable.Range(0, 6).Select(seed => MakeJpeg(3000, 2000, seed)).ToArray();
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(60));

        // 190 plain files + 5 compare pairs (Q01.jpg and "Q01 (1).jpg") = 200 files.
        for (var i = 0; i < FileCount - 10; i++)
            File.WriteAllBytes(Path.Combine(Dir, $"P{i:000}.jpg"), bases[i % bases.Length]);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllBytes(Path.Combine(Dir, $"Q{i:00}.jpg"), bases[i % bases.Length]);
            File.WriteAllBytes(Path.Combine(Dir, $"Q{i:00} (1).jpg"), bases[(i + 1) % bases.Length]);
        }
        for (var i = 0; i < SmallCount; i++)
            File.WriteAllBytes(Path.Combine(SmallDir, $"S{i:00}.jpg"), bases[i % bases.Length]);
        if (Directory.GetFiles(Dir).Length != FileCount) throw new InvalidOperationException("fixture generation produced the wrong file count");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _root?.Dispose();

    private static byte[] MakeJpeg(int width, int height, int seed)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var rng = new Random(seed * 7919 + 1);
        var noise = new byte[4096];
        rng.NextBytes(noise);
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var o = row + x * 4;
                var n = noise[(x * 31 + y * 17) & 4095] >> 3; // mild texture so the JPEG is ~1-2 MB, not 50 KB
                pixels[o] = (byte)(((x + seed * 90) >> 4) + n);
                pixels[o + 1] = (byte)((y >> 3) + n);
                pixels[o + 2] = (byte)(((x ^ y) >> 4) + seed * 20 + n);
                pixels[o + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
