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

/// <summary>WP-23: tooltip tạo muộn (sau khung đầu) không mất vùng đã đăng ký và không làm sập khi tạo lỗi.</summary>
[Trait("Category", "HotPath")]
public sealed class DeferredTooltipsTests
{
    private static OverlayTooltip Tool(string id) => new(id, new RectD(1, 2, 3, 4), "chữ " + id);

    [Fact]
    public void NothingIsCreatedUntilThePostedActionRuns_ThenThePendingListIsApplied()
    {
        var created = new FakeTooltips();
        Action? posted = null;
        int creates = 0;
        var deferred = new DeferredTooltips(() =>
        {
            creates++;
            return created;
        }, a => posted = a);

        Assert.Equal(0, creates);
        deferred.Sync([Tool("a")], 1.25);
        deferred.Sync([Tool("a"), Tool("b")], 1.5); // bản gần nhất thắng
        deferred.RelayMouse(0x0200, 0, 0);
        Assert.Equal(0, created.SyncCount);
        Assert.Empty(created.Relayed);

        posted!();
        Assert.Equal(1, creates);
        Assert.True(deferred.IsCreated);
        Assert.Equal(["a", "b"], created.Tools.Select(t => t.Id));
        Assert.Equal(1.5, created.Dpi);

        deferred.Sync([Tool("c")], 2);
        Assert.Equal(["c"], created.Tools.Select(t => t.Id));
        deferred.RelayMouse(0x0200, 0, 0);
        Assert.Single(created.Relayed);

        posted!(); // chạy lại không tạo thêm
        Assert.Equal(1, creates);
    }

    [Fact]
    public void DisposedBeforeCreation_NeverCreates_DisposeReleasesTheControl()
    {
        Action? posted = null;
        int creates = 0;
        var early = new DeferredTooltips(() =>
        {
            creates++;
            return new FakeTooltips();
        }, a => posted = a);
        early.Dispose();
        posted!();
        Assert.Equal(0, creates);
        early.Sync([Tool("a")], 1);

        var created = new FakeTooltips();
        Action? post2 = null;
        var late = new DeferredTooltips(() => created, a => post2 = a);
        post2!();
        late.Dispose();
        Assert.True(created.Disposed);
        late.Dispose();
    }

    [Fact]
    public void CreationFailure_LeavesNoTooltipsWithoutThrowing()
    {
        Action? posted = null;
        var deferred = new DeferredTooltips(() => throw new InvalidOperationException("no comctl"), a => posted = a);
        deferred.Sync([Tool("a")], 1);
        posted!();
        Assert.False(deferred.IsCreated);
        deferred.Sync([Tool("b")], 1);
        deferred.RelayMouse(0x0200, 0, 0);
    }
}

/// <summary>Cửa sổ giả cho <see cref="OverlayMessageHandler"/>: chỉ DPI có nghĩa, các hành động chỉ ghi lại.</summary>
internal sealed class FakeShellWindow : PhotoReview.Shell.Win32.Hosting.IShellWindow
{
    public nint Hwnd => 0;

    public double DpiScale { get; set; } = 1;

    public SizeD ClientSizeDip => new(1000, 700);

    public bool IsActive => true;

    public bool IsLoaded => true;

    public bool HasPointerCapture { get; private set; }

    public event EventHandler? ClientSizeChanged { add { } remove { } }

    public event EventHandler? DpiChanged { add { } remove { } }

    public event EventHandler? Activated { add { } remove { } }

    public event EventHandler? Deactivated { add { } remove { } }

    public event EventHandler<System.ComponentModel.CancelEventArgs>? Closing { add { } remove { } }

    public event EventHandler? Closed { add { } remove { } }

    public void AddMessageHandler(PhotoReview.Shell.Win32.Hosting.IWindowMessageHandler handler)
    {
    }

    public void Invalidate()
    {
    }

    public void SetTitle(string title)
    {
    }

    public void SetCursor(PhotoReview.Shell.Win32.Hosting.ShellCursor cursor)
    {
    }

    public void CapturePointer() => HasPointerCapture = true;

    public void ReleasePointer() => HasPointerCapture = false;

    public PointD ScreenToClientDip(PointD screenPixel) => new(screenPixel.X / DpiScale, screenPixel.Y / DpiScale);

    public void Close()
    {
    }
}

/// <summary>
/// WP-23 (mutation gaps): các giá trị mà test cũ chỉ so với chính hằng số / dung sai golden 2 DIP, toạ độ DPI của handler thông
/// điệp, và các điểm quan sát mà đột biến thủ công không bị bắt. Dùng <see cref="OverlayRig"/> (đồng hồ giả, chữ đo tất định).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class OverlayMutationGapTests
{
    private static OverlayRig NewRig(Action<PhotoReview.Core.Settings.AppSettings>? tweak = null)
    {
        var rig = new OverlayRig();
        tweak?.Invoke(rig.Settings);
        rig.Update(m => m with { HasImages = true });
        return rig;
    }

    [Fact]
    public void Toolbar_BoxAndButtonSpacingMatchTheXamlExactly()
    {
        using var rig = new OverlayRig();
        ToolbarPanel bar = rig.Overlay.Toolbar;
        Assert.Equal(8, bar.Bounds.X, 3);
        Assert.Equal(8, bar.Bounds.Y, 3);
        Assert.Equal(40, bar.Bounds.Height, 3); // 32 (nút ⋮) + Padding 4 + 4
        Assert.Equal(14, bar.OpenFolder.Bounds.X, 3); // Margin 8 + Padding 6
        Assert.Equal(12, bar.Tools.Bounds.Y, 3);
        Assert.Equal(32, bar.Tools.Bounds.Height, 3);
        Assert.Equal(34, bar.Tools.Bounds.Width, 3);
        Assert.Equal(5, bar.Settings.Bounds.X - (bar.OpenFolder.Bounds.X + bar.OpenFolder.Bounds.Width), 3);
        Assert.Equal(5, bar.Fit.Bounds.X - (bar.Settings.Bounds.X + bar.Settings.Bounds.Width), 3);
        Assert.Equal(5, bar.Tools.Bounds.X - (bar.Fit.Bounds.X + bar.Fit.Bounds.Width), 3);
        Assert.Equal(bar.Bounds.X + bar.Bounds.Width - 6, bar.Tools.Bounds.X + bar.Tools.Bounds.Width, 3); // Padding phải 6
    }

    [Fact]
    public void FolderInfoPanel_IsCappedAt640Dip_AndTheExifLineSitsTwoDipBelowTheStatusLine()
    {
        using var rig = new OverlayRig();
        rig.Update(m => m with
        {
            FolderInfoVisible = true,
            FolderInfoText = new string('x', 400),
            StatusPanelVisible = true,
            FileInfoVisible = true,
            StatusText = "a.jpg",
            ExifVisible = true,
            ExifText = "ISO 100",
        });
        rig.Overlay.Arrange(new SizeD(1000, 700), 1);
        // FakeTextRenderer cắt "…" theo ký tự 6 DIP: floor((640 - 12) / 6) x 6 + 12 = 636 (đột biến 600 cho 600).
        Assert.Equal(636, rig.Overlay.FolderInfo.Bounds.Width, 3);

        StatusPanel status = rig.Overlay.Status;
        Assert.Equal(2, status.Exif.Bounds.Y - (status.Status.Bounds.Y + status.Status.Bounds.Height), 3);
    }

    [Fact]
    public void Toolbar_HotZoneIs24DipRegardlessOfTheConstant()
    {
        using OverlayRig rig = NewRig();
        RectD box = rig.ToolbarBounds;
        rig.MoveTo(new PointD(box.X + box.Width + 24, 20));
        rig.Advance(5000);
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);

        rig.MoveTo(new PointD(box.X + box.Width + 24.5, 20));
        rig.Advance(1500);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);

        rig.MoveTo(new PointD(box.X - 24, box.Y - 24));
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
        rig.MoveTo(new PointD(box.X - 24.5, box.Y - 24));
        rig.Advance(1500);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    [Fact]
    public void Toolbar_ZeroDelayStillWaitsForALaterFrame_NotTheFrameAtTheSameInstant()
    {
        using OverlayRig rig = NewRig(s => s.ToolbarAutoHideDelayMs = 0);
        rig.MoveTo(OverlayRig.FarAway);
        rig.Clock.Tick(rig.Now); // khung ở đúng thời điểm đặt hẹn: WPF Math.Max(1, delay) -> chưa ẩn
        Assert.True(rig.Overlay.Toolbar.IsHitTestVisible);
        rig.Advance(1);
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
    }

    [Fact]
    public void InfoOverlay_StopsItsIdleTimer_WhenSomethingStartsKeepingItVisible()
    {
        using OverlayRig rig = NewRig();
        int running = rig.Clock.SubscriberCount;
        Assert.True(running >= 1); // đang đếm lui để ẩn
        rig.Update(m => m with { CompareVisible = true });
        Assert.True(rig.Clock.SubscriberCount < running, "vòng khung phải nghỉ khi không còn gì để ẩn");
    }

    [Fact]
    public void Wheel_IsConsumedOverAVisiblePanel_AndPassedThroughElsewhere()
    {
        using OverlayRig rig = NewRig();
        rig.Update(m => m with { ZoomIndicatorVisible = true, ZoomIndicatorText = "100%" });
        rig.Overlay.Arrange(new SizeD(1000, 700), 1);
        Assert.True(rig.Overlay.Wheel(OverlayRig.Center(rig.Overlay.ZoomIndicator)));
        Assert.False(rig.Overlay.Wheel(new PointD(500, 350)));
    }

    [Fact]
    public void Tooltips_ToolbarOffersNoneAsSoonAsItStopsReceivingTheMouse_EvenWhileStillFading()
    {
        using OverlayRig rig = NewRig();
        rig.MoveTo(OverlayRig.FarAway);
        rig.Advance(1500); // hết giờ: click xuyên ngay, opacity còn 1 ở khung đầu
        Assert.False(rig.Overlay.Toolbar.IsHitTestVisible);
        Assert.True(rig.Overlay.Toolbar.Opacity > 0f);

        rig.Overlay.Render(new RecordingDrawContext(), new SizeD(1000, 700), 1);
        Assert.Empty(rig.Tooltips!.Tools);
    }

    [Fact]
    public void Tab_AutoRepeatDoesNotMoveTheFocus()
    {
        using OverlayRig rig = NewRig();
        Assert.True(rig.Overlay.KeyDown(0x09, false, false));
        Assert.Same(rig.Overlay.Toolbar.OpenFolder, rig.Overlay.Toolbar.FocusedButton);
        Assert.True(rig.Overlay.KeyDown(0x09, false, isAutoRepeat: true)); // giữ Tab: không chạy tiếp qua các nút
        Assert.Same(rig.Overlay.Toolbar.OpenFolder, rig.Overlay.Toolbar.FocusedButton);
    }

    [Fact]
    public void MessageHandler_ConvertsPhysicalPixelsToDipsWithTheWindowDpi()
    {
        using OverlayRig rig = NewRig();
        var window = new FakeShellWindow { DpiScale = 2 };
        var handler = new OverlayMessageHandler(rig.Overlay, window, rig.Tooltips);
        PointD fit = OverlayRig.Center(rig.Overlay.Toolbar.Fit);
        nint lParam = (nint)(((int)Math.Round(fit.Y * 2) << 16) | ((int)Math.Round(fit.X * 2) & 0xFFFF));

        Assert.False(handler.TryHandle(new PhotoReview.Shell.Win32.Hosting.WindowMessage(0, PhotoReview.Shell.Interop.WindowMessages.WmMouseMove, 0, lParam), out _));
        Assert.True(rig.Overlay.Toolbar.Fit.IsHot);
        Assert.False(rig.Overlay.Toolbar.Settings.IsHot);

        Assert.True(handler.TryHandle(new PhotoReview.Shell.Win32.Hosting.WindowMessage(0, PhotoReview.Shell.Interop.WindowMessages.WmLButtonDown, 0, lParam), out _));
        Assert.True(handler.TryHandle(new PhotoReview.Shell.Win32.Hosting.WindowMessage(0, PhotoReview.Shell.Interop.WindowMessages.WmLButtonUp, 0, lParam), out _));
        Assert.Equal(["Fit"], rig.Commands.Calls);
    }
}