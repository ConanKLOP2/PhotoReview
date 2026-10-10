using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10: the committed golden files (<c>tests/Fixtures/golden/*.v1.json</c>) are true of the WPF build in the tree. Each test replays
/// the golden on the real WPF window (or re-records it in memory) and compares; a change of WPF behaviour (a zoom constant, the
/// Fit/pan maths, the scroll bar template, the menu rules) turns one of these red until the golden is re-recorded on purpose
/// (<c>tools/diag/record-golden.ps1</c>) and the diff is reviewed. They are the equivalence reference of the Win32 shell
/// (<c>NO-WPF-EXEC-PLAN</c> section 7.3), so a red here is never "just update the file".
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class GoldenWpfConformanceTests
{
    // WPF replayed against WPF: same process maths, so the bound is the file's JSON double round-trip, not the 0.5 DIP the Win32 port has.
    private const double GeometryTolerance = 0.001;
    private const double OverlayTolerance = 2.0;

    [Fact]
    public async Task ViewportLayout_CommittedGolden_MatchesWpf()
    {
        var committed = GoldenFile.Read(GoldenFile.ViewportLayout, GoldenJsonContext.Default.GoldenDocumentGoldenViewportCase);
        var recorded = await ViewportGoldenRecorder.RecordViewportAsync();

        Assert.True(committed.Items.Count >= 600, $"G-VIEW needs >= 600 cases, has {committed.Items.Count}.");
        Assert.Equal(committed.Items.Select(c => c.Name), recorded.Select(c => c.Name));
        foreach (var (expected, actual) in committed.Items.Zip(recorded))
        {
            AssertClose(expected.Name + " input", ToArray(expected.Input), ToArray(actual.Input));
            AssertClose(expected.Name + " layout", ToArray(expected.Expected), ToArray(actual.Expected));
            Assert.Equal(expected.Input.Stretch, actual.Input.Stretch);
            Assert.Equal(expected.Expected.HorizontalBarVisible, actual.Expected.HorizontalBarVisible);
            Assert.Equal(expected.Expected.VerticalBarVisible, actual.Expected.VerticalBarVisible);
        }
    }

    [Fact]
    public async Task InputScripts_CommittedGolden_ReplayOnWpf() =>
        Assert.True(await ReplayAsync(pendingArrowPan: false) >= 60, "G-INPUT replays at least 60 gating scripts.");

    /// <summary>
    /// The arrow-pan scripts recorded under the OLD step rule (see <see cref="InputScriptCatalog.ArrowPanPending"/>): not gating,
    /// because the pending app change makes them red on purpose. Run after the pan PR to see exactly what it changed, then re-record.
    /// </summary>
    [Fact]
    [Trait("Category", "Manual")]
    public async Task InputScripts_ArrowPanPendingRuleChange_ReplayOnWpf() =>
        Assert.True(await ReplayAsync(pendingArrowPan: true) > 0);

    private static async Task<int> ReplayAsync(bool pendingArrowPan)
    {
        var all = GoldenFile.Read(GoldenFile.InputScripts, GoldenJsonContext.Default.GoldenDocumentGoldenInputScript);
        Assert.True(all.Items.Count >= 60, $"G-INPUT needs >= 60 scripts, has {all.Items.Count}.");
        var scripts = all.Items.Where(s => s.Name.StartsWith(InputScriptCatalog.ArrowPanPending, StringComparison.Ordinal) == pendingArrowPan).ToList();
        Assert.NotEmpty(scripts);

        await GoldenWpfHost.RunEachAsync(scripts, async (view, script) =>
        {
            var actual = await InputScriptRecorder.RunAsync(view, script);
            Assert.Equal(script.Expected.Count, actual.Count);
            for (var step = 0; step < actual.Count; step++)
            {
                var label = $"{script.Name} after step {step} ({script.Steps[step].Kind} {script.Steps[step].Command ?? script.Steps[step].Key})";
                var expected = script.Expected[step];
                Assert.Equal(expected.IsFit, actual[step].IsFit);
                Assert.Equal(expected.DisplayZoomPercent, actual[step].DisplayZoomPercent);
                AssertClose(label, ToArray(expected), ToArray(actual[step]));
            }
        });
        return scripts.Count;
    }

    [Fact]
    public void InputScripts_CatalogStepsAreTheCommittedOnes()
    {
        var committed = GoldenFile.Read(GoldenFile.InputScripts, GoldenJsonContext.Default.GoldenDocumentGoldenInputScript);
        var catalog = InputScriptCatalog.Build();

        Assert.Equal(catalog.Select(s => s.Name), committed.Items.Select(s => s.Name));
        foreach (var (fromCatalog, fromFile) in catalog.Zip(committed.Items))
        {
            Assert.Equal(fromCatalog.Setup, fromFile.Setup);
            Assert.Equal(fromCatalog.Steps, fromFile.Steps);
        }
    }

    [Fact]
    public async Task ContextMenu_CommittedGolden_MatchesWpf()
    {
        var committed = GoldenFile.Read(GoldenFile.ContextMenu, GoldenJsonContext.Default.GoldenDocumentGoldenMenuCase);
        Assert.True(committed.Items.Count >= 12, $"G-MENU needs >= 12 cases, has {committed.Items.Count}.");
        var recorded = await MenuGoldenRecorder.RecordAsync();

        Assert.Equal(committed.Items.Select(c => c.Name), recorded.Select(c => c.Name));
        foreach (var (expected, actual) in committed.Items.Zip(recorded))
        {
            Assert.Equal(expected.SettingsOverridesJson, actual.SettingsOverridesJson);
            Assert.Equal(expected.VisibleIdsInOrder, actual.VisibleIdsInOrder);
        }
    }

    [Fact]
    public async Task OverlayBoxes_CommittedGolden_MatchWpf()
    {
        var committed = GoldenFile.Read(GoldenFile.OverlayBoxes, GoldenJsonContext.Default.GoldenDocumentGoldenOverlayBox);
        var recorded = await OverlayGoldenRecorder.RecordAsync();

        Assert.Equal(committed.Items.Select(b => b.Name), recorded.Select(b => b.Name));
        foreach (var (expected, actual) in committed.Items.Zip(recorded))
        {
            AssertClose(expected.Name, [expected.X, expected.Y, expected.Width, expected.Height], [actual.X, actual.Y, actual.Width, actual.Height], OverlayTolerance);
        }
    }

    /// <summary>
    /// The scripted harness (real controllers + scripted clock/mouse) behaves as the real, shown MainWindow handling real routed key
    /// events: the same keys give the same zoom and offsets. This is what ties the golden to "the app" and not only to the harness.
    /// </summary>
    [Fact]
    public async Task ScriptedHarness_MatchesTheRealShownWindow_ForKeyboardZoomAndFit()
    {
        using var root = new TempRoot("golden-real-window");
        using var dataRoot = new DataRootFixture();
        var file = WriteJpeg(Path.Combine(root.Path, "photo.jpg"), 3000, 2000);
        string[] keys = ["Add", "Add", "D1", "Add", "F"];
        MainWindow? window = null;
        var real = new List<GoldenCheckpoint>();
        var setup = default(GoldenSetup);

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var presented = new List<string>();
                window = TestAppHost.CreateMainWindow(file, new TestHostHooks { OnPresented = presented.Add, DisablePreload = true });
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1300;
                window.Height = 800;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.Show();
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(30)), "Image never presented");
                Assert.True(await StaTestHost.WaitForAsync(() => window.ViewModel.Viewer.FitZoom > 0 && window.ViewModel.Viewer.IsFit, TimeSpan.FromSeconds(10)), "Fit never settled");
                await Idle(window);

                var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
                setup = new GoldenSetup(window.ImageScroll.ActualWidth, window.ImageScroll.ActualHeight, dpi, 3000, 2000, "{}");
                var viewer = window.ViewModel.Viewer;
                foreach (var key in keys)
                {
                    var before = (viewer.Zoom, viewer.IsFit, window.ImageScroll.HorizontalOffset, window.ImageScroll.VerticalOffset, window.ImageScroll.ExtentWidth);
                    Press(window, Enum.Parse<Key>(key));
                    Assert.True(await StaTestHost.WaitForAsync(
                        () => (viewer.Zoom, viewer.IsFit, window.ImageScroll.HorizontalOffset, window.ImageScroll.VerticalOffset, window.ImageScroll.ExtentWidth) != before,
                        TimeSpan.FromSeconds(10)), $"Key {key} changed nothing.");
                    await Idle(window);
                    real.Add(new GoldenCheckpoint(real.Count, viewer.Zoom, viewer.IsFit, window.ImageScroll.HorizontalOffset, window.ImageScroll.VerticalOffset,
                        window.ImageScroll.ExtentWidth, window.ImageScroll.ExtentHeight, window.ImageScroll.ViewportWidth, window.ImageScroll.ViewportHeight,
                        viewer.DisplayZoomPercent));
                }
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }

        var steps = keys.Select((key, index) => new GoldenInputStep("key", 0, 0, 0, key, null, 40 * (index + 1), null)).ToList();
        var script = new GoldenInputScript("real-window-cross-check", setup!, steps, []);
        IReadOnlyList<GoldenCheckpoint>? scripted = null;
        await GoldenWpfHost.RunEachAsync([script], async (view, s) => scripted = await InputScriptRecorder.RunAsync(view, s));

        Assert.NotNull(scripted);
        Assert.Equal(real.Count, scripted!.Count);
        for (var i = 0; i < real.Count; i++)
        {
            Assert.Equal(real[i].IsFit, scripted[i].IsFit);
            AssertClose($"key {keys[i]} (real window vs scripted harness)", ToArray(real[i]), ToArray(scripted[i]), 0.5);
        }
    }

    private static Task Idle(MainWindow window) =>
        window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void Press(MainWindow window, Key key)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = PresentationSource.FromVisual(window)
            ?? HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The hosted MainWindow has no PresentationSource to raise key input from.");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static string WriteJpeg(string path, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)(x ^ y);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static double[] ToArray(ViewportInputDto d) =>
        [d.ClientWidth, d.ClientHeight, d.ScrollBarThickness, d.ImageWidth, d.ImageHeight, d.MaxImageWidth, d.MaxImageHeight, d.BitmapWidth, d.BitmapHeight];

    private static double[] ToArray(ViewportLayoutDto d) =>
        [d.ViewportWidth, d.ViewportHeight, d.ExtentWidth, d.ExtentHeight, d.ImageX, d.ImageY, d.ImageWidth, d.ImageHeight, d.MaxHorizontalOffset, d.MaxVerticalOffset];

    private static double[] ToArray(GoldenCheckpoint c) =>
        [c.Zoom, c.HorizontalOffset, c.VerticalOffset, c.ExtentWidth, c.ExtentHeight, c.ViewportWidth, c.ViewportHeight];

    private static void AssertClose(string label, double[] expected, double[] actual, double tolerance = GeometryTolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var same = (double.IsNaN(expected[i]) && double.IsNaN(actual[i]))
                || (double.IsInfinity(expected[i]) && expected[i] == actual[i])
                || Math.Abs(expected[i] - actual[i]) <= tolerance;
            Assert.True(same, $"{label}: field #{i} expected {expected[i]} but WPF gives {actual[i]} (tolerance {tolerance}). The golden no longer describes the WPF build.");
        }
    }
}
