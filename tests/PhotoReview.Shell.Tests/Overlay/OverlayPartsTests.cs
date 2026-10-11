using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>WP-23: hẹn giờ theo đồng hồ khung (thay DispatcherTimer của auto-hide).</summary>
[Trait("Category", "HotPath")]
public sealed class FrameDelayTimerTests
{
    private static (FrameDelayTimer Timer, FakeFrameClock Clock, Func<int> Fired, Action<long> SetNow) Create()
    {
        var clock = new FakeFrameClock();
        long now = 0;
        int fired = 0;
        var timer = new FrameDelayTimer(clock, () => fired++, () => now, 1000);
        return (timer, clock, () => fired, v => now = v);
    }

    [Fact]
    public void Fires_OnTheFirstFrameAtOrAfterTheDeadline_ExactlyOnce()
    {
        (FrameDelayTimer timer, FakeFrameClock clock, Func<int> fired, Action<long> setNow) = Create();
        setNow(100);
        timer.Start(TimeSpan.FromMilliseconds(500));
        Assert.True(timer.IsRunning);
        clock.Tick(599);
        Assert.Equal(0, fired());
        clock.Tick(600);
        Assert.Equal(1, fired());
        clock.Tick(900);
        Assert.Equal(1, fired());
        Assert.False(timer.IsRunning);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void Start_WhileRunning_ReplacesTheDeadline()
    {
        (FrameDelayTimer timer, FakeFrameClock clock, Func<int> fired, Action<long> setNow) = Create();
        setNow(0);
        timer.Start(TimeSpan.FromMilliseconds(500));
        setNow(400);
        timer.Start(TimeSpan.FromMilliseconds(500));
        clock.Tick(500);
        Assert.Equal(0, fired());
        clock.Tick(900);
        Assert.Equal(1, fired());
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void Stop_CancelsAndReleasesTheClock_AndStartRequestsAFrame()
    {
        (FrameDelayTimer timer, FakeFrameClock clock, Func<int> fired, _) = Create();
        timer.Start(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, clock.RequestFrameCount);
        Assert.Equal(1, clock.SubscriberCount);
        timer.Stop();
        Assert.Equal(0, clock.SubscriberCount);
        clock.Tick(1_000_000);
        Assert.Equal(0, fired());
        timer.Stop(); // idempotent
    }

    [Fact]
    public void CallbackMayRestartTheTimer()
    {
        var clock = new FakeFrameClock();
        long now = 0;
        FrameDelayTimer? timer = null;
        int fired = 0;
        timer = new FrameDelayTimer(clock, () =>
        {
            fired++;
            if (fired < 3)
            {
                timer!.Start(TimeSpan.FromMilliseconds(100));
            }
        }, () => now, 1000);
        timer.Start(TimeSpan.FromMilliseconds(100));
        for (int t = 100; t <= 400; t += 100)
        {
            now = t;
            clock.Tick(t);
        }

        Assert.Equal(3, fired);
    }

    [Fact]
    public void NegativeDelayFiresOnTheNextFrame_DisposedTimerRefusesToStart()
    {
        (FrameDelayTimer timer, FakeFrameClock clock, Func<int> fired, _) = Create();
        timer.Start(TimeSpan.FromMilliseconds(-5));
        clock.Tick(0);
        Assert.Equal(1, fired());
        timer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => timer.Start(TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameDelayTimer(clock, () => { }, null, 0));
    }
}

/// <summary>WP-23: lớp chồng (Grid một ô), ToolsMenu, model.</summary>
[Trait("Category", "HotPath")]
public sealed class OverlayPartsTests
{
    private static readonly OverlayLayoutContext Context = new(new SizeD(400, 300), 1, new FakeTextRenderer());

    [Fact]
    public void Layer_ChildrenOverlapInTheWholeCell_EachAlignedByItself()
    {
        var layer = new LayerElement("L");
        var topLeft = new PanelElement("A")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Left,
            VerticalAlignment = OverlayVerticalAlignment.Top,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
        }.Add(new TextElement("a") { Text = "xx", Style = new TextStyle("Segoe UI", 10) });
        var bottomRight = new PanelElement("B")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Right,
            VerticalAlignment = OverlayVerticalAlignment.Bottom,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
        }.Add(new TextElement("b") { Text = "yyyy", Style = new TextStyle("Segoe UI", 10) });
        layer.Add(topLeft).Add(bottomRight);
        layer.Arrange(Context);

        Assert.Equal(new RectD(0, 0, 400, 300), layer.Bounds);
        Assert.Equal(8, topLeft.Bounds.X);
        Assert.Equal(8, topLeft.Bounds.Y);
        Assert.Equal(400 - 8, bottomRight.Bounds.X + bottomRight.Bounds.Width);
        Assert.Equal(300 - 8, bottomRight.Bounds.Y + bottomRight.Bounds.Height);
        Assert.Same(layer.Find("b"), layer.Find("b"));
        Assert.NotNull(layer.Find("A"));
    }

    [Fact]
    public void Layer_EmptySpaceIsClickThrough_ChildrenHit_HiddenLayerIsNot()
    {
        var layer = new LayerElement("L");
        var panel = new PanelElement("A") { HorizontalAlignment = OverlayHorizontalAlignment.Left, VerticalAlignment = OverlayVerticalAlignment.Top }
            .Add(new TextElement("a") { Text = "xxxx", Style = new TextStyle("Segoe UI", 10) });
        layer.Add(panel);
        layer.Arrange(Context);

        Assert.Null(layer.FindAt(new PointD(300, 200)));
        Assert.Same(panel, layer.FindAt(new PointD(2, 2)));

        layer.IsHitTestVisible = false;
        Assert.Null(layer.FindAt(new PointD(2, 2)));
        layer.IsHitTestVisible = true;
        layer.Opacity = 0;
        Assert.Null(layer.FindAt(new PointD(2, 2)));
        layer.Opacity = 1;
        layer.IsVisible = false;
        Assert.Null(layer.FindAt(new PointD(2, 2)));
    }

    [Fact]
    public void Layer_RenderAppliesItsOpacityToChildren_AndReleasesLayouts()
    {
        var text = new FakeTextRenderer();
        var context = new OverlayLayoutContext(new SizeD(400, 300), 1, text);
        var layer = new LayerElement("L");
        layer.Add(new PanelElement("A") { HorizontalAlignment = OverlayHorizontalAlignment.Left, VerticalAlignment = OverlayVerticalAlignment.Top }
            .Add(new TextElement("a") { Text = "hello", Style = new TextStyle("Segoe UI", 10) }));
        layer.Opacity = 0.5f;
        layer.Arrange(context);
        var dc = new RecordingDrawContext();
        layer.Render(dc);
        Assert.Equal(0.5f, dc.Texts.Single().Opacity);
        Assert.Equal(0.5f, dc.Fills.Single().Opacity);

        layer.ReleaseResources();
        Assert.Equal(0, text.Live);
    }

    [Fact]
    public void ToolsMenu_BuildGivesTheSixToolsItemsInXamlOrder()
    {
        IReadOnlyList<MenuItemModel> items = ToolsMenu.Build(hasImages: true);
        Assert.Equal(
            ["Tools.Recovery", "Tools.Diagnostics", "Tools.Benchmark", "Tools.ClearCache", "Tools.RemoveNumberedDuplicates", "Tools.RemoveOriginalDuplicates"],
            items.Select(i => i.Id));
        Assert.All(items, i => Assert.True(i.IsEnabled));
    }

    [Fact]
    public void ToolsMenu_ExecuteIgnoresUnknownAndNullIds()
    {
        var commands = new FakeOverlayCommands();
        Assert.Null(ToolsMenu.Execute("Tools.Nope", commands, true));
        Assert.Null(ToolsMenu.Execute(null, commands, true));
        Assert.Empty(commands.Calls);
        Assert.Throws<ArgumentNullException>(() => { _ = ToolsMenu.Execute("Tools.Recovery", null!, true); });
    }

    [Fact]
    public void Button_FixedSizeWinsOverContent_AndFocusAndCheckedChangeTheChrome()
    {
        var text = new FakeTextRenderer();
        var context = new OverlayLayoutContext(new SizeD(400, 300), 1, text);
        var button = new ButtonElement("T")
        {
            Content = "xxxxxxxxxxxxxxxxxxxxxxxx",
            Style = new TextStyle("Segoe UI", 18),
            FixedWidth = 34,
            FixedHeight = 32,
            HorizontalAlignment = OverlayHorizontalAlignment.Left,
            VerticalAlignment = OverlayVerticalAlignment.Top,
        };
        button.Arrange(context);
        Assert.Equal(34, button.Bounds.Width);
        Assert.Equal(32, button.Bounds.Height);

        var plain = new RecordingDrawContext();
        button.Render(plain);
        Assert.Equal(DarkPalette.Button, plain.Fills.Single().Color);

        button.IsChecked = true;
        var pressed = new RecordingDrawContext();
        button.Render(pressed);
        Assert.Equal(DarkPalette.ButtonPressed, pressed.Fills.Single().Color);
    }

    [Fact]
    public void Model_DefaultsAreHidden()
    {
        var model = new OverlayModel();
        Assert.False(model.HasImages);
        Assert.False(model.StatusPanelVisible);
        Assert.False(model.FolderInfoVisible);
        Assert.False(model.ZoomIndicatorVisible);
        Assert.False(model.CapturePairVisible);
        Assert.Equal(12, model.FontSize);
        Assert.Equal(11, model.ExifFontSize);
    }
}
