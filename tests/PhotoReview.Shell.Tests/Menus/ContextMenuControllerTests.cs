using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>Host menu giả: ghi mô hình và điểm neo, trả id cấu hình sẵn (hoặc chạy <see cref="OnShow"/>).</summary>
internal sealed class FakePopupMenuHost : IPopupMenuHost
{
    public string? Choose { get; set; }

    public int ShowCount { get; private set; }

    public IReadOnlyList<MenuItemModel>? LastItems { get; private set; }

    public PointD? LastPoint { get; private set; }

    public Func<string?>? OnShow { get; set; }

    public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip)
    {
        ShowCount++;
        LastItems = items;
        LastPoint = screenPointDip;
        return OnShow is { } hook ? hook() : Choose;
    }
}

/// <summary>Cửa sổ giả của controller: ghi từng lời gọi thành một dòng trong <see cref="Log"/>.</summary>
internal sealed class FakeContextMenuActions : IContextMenuActions
{
    public AppSettings Settings { get; set; } = new();

    public bool HasImage { get; set; } = true;

    public bool CanCopyPath { get; set; } = true;

    public bool CanUndo { get; set; } = true;

    public bool CanOpenInExternalEditor { get; set; } = true;

    public bool IsCompareVisible { get; set; }

    public double? EffectiveZoom { get; set; } = 1.0;

    public List<string> Log { get; } = [];

    public Exception? Failure { get; set; }

    public Task ExecuteAsync(ReviewCommand command) => Record("Execute:" + command.Type);

    public Task FitAsync() => Record("Fit");

    public Task ZoomToConfiguredLevelAsync() => Record("ZoomToConfiguredLevel");

    public Task ApplyZoomPresetAsync(int percent) => Record("Preset:" + percent);

    public Task CustomZoomAsync() => Record("CustomZoom");

    public void SetZoomAlsoSetsClickLevel(bool value) => Log.Add("AlsoSet:" + value);

    public void SetClickZoomPercent(int percent) => Log.Add("SetClickLevel:" + percent);

    public void OpenInExternalEditor() => RecordSync("ExternalEditor");

    public void OpenInExternalEditor(string editorId) => RecordSync("ExternalEditor:" + editorId);

    public void AddExternalEditor() => RecordSync("AddEditor");

    public void ChooseDefaultExternalEditor() => RecordSync("SetDefaultEditor");

    public void CopyFileName() => RecordSync("CopyFileName");

    public void CopyFullPathname() => RecordSync("CopyFullPathname");

    public void ShowSettings() => RecordSync("ShowSettings");

    public void ReportActionFailed(string actionName, Exception exception) => Log.Add($"failed:{actionName}:{exception.GetType().Name}");

    private Task Record(string entry)
    {
        Log.Add(entry);
        return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    private void RecordSync(string entry)
    {
        Log.Add(entry);
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}

/// <summary>
/// WP-24 (NO-WPF-EXEC-PLAN-WP): controller của menu chuột phải qua seam <see cref="IPopupMenuHost"/> giả chọn id. Port logic của
/// <c>ContextMenuRedesignTests</c>, <c>ZoomContextMenuTests</c>, <c>ClickZoomCustomDialogTests</c> (WPF dùng cửa sổ thật; ở đây
/// kiểm chính các handler *_Click tương ứng qua id).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ContextMenuControllerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static (ContextMenuController Controller, FakeContextMenuActions Actions, FakePopupMenuHost Host) Create(
        AppSettings? settings = null)
    {
        var actions = new FakeContextMenuActions { Settings = settings ?? new AppSettings() };
        var host = new FakePopupMenuHost();
        return (new ContextMenuController(actions, host), actions, host);
    }

    private static AppSettings NothingHidden()
    {
        var settings = new AppSettings();
        ContextMenuItems.ApplyHidden(settings, []);
        return settings;
    }

    [Fact]
    public async Task Show_WhenTheHostThrows_ReportsAMenuFailureInsteadOfThrowing_AndCanReopen()
    {
        var (controller, actions, host) = Create();
        host.OnShow = () => throw new InvalidOperationException("boom");

        await controller.ShowAsync(new PointD(1, 1));

        Assert.Equal(["failed:Menu:InvalidOperationException"], actions.Log);
        Assert.False(controller.IsOpen);
        host.OnShow = () => null;
        await controller.ShowAsync(new PointD(1, 1));
        Assert.Equal(2, host.ShowCount);
    }

    [Theory]
    [InlineData("Undo", "Execute:Undo")]
    [InlineData("MoveToRecycleBin", "Execute:Recycle")]
    [InlineData("Fit", "Fit")]
    [InlineData("ZoomToLevel", "ZoomToConfiguredLevel")]
    [InlineData("ZoomFitWidth", "Execute:FitWidth")]
    [InlineData("ZoomFitWidth2", "Execute:FitWidth2")]
    [InlineData("ZoomFitHeight", "Execute:FitHeight")]
    [InlineData("Refresh", "Execute:Refresh")]
    [InlineData("OpenFolder", "Execute:OpenFolder")]
    [InlineData("NextFolder", "Execute:NextFolder")]
    [InlineData("PreviousFolder", "Execute:PreviousFolder")]
    [InlineData("ExternalEditor", "ExternalEditor")]
    [InlineData("RenameFile", "Execute:RenameFile")]
    [InlineData("CopyFileName", "CopyFileName")]
    [InlineData("CopyFullPath", "CopyFullPathname")]
    [InlineData("Settings", "ShowSettings")]
    [InlineData("Zoom.Custom", "CustomZoom")]
    [InlineData("Zoom.Preset.50", "Preset:50")]
    [InlineData("Zoom.Preset.70", "Preset:70")]
    [InlineData("Zoom.Preset.100", "Preset:100")]
    [InlineData("Zoom.Preset.150", "Preset:150")]
    [InlineData("Zoom.Preset.200", "Preset:200")]
    [InlineData("Zoom.Preset.300", "Preset:300")]
    [InlineData("Zoom.Preset.400", "Preset:400")]
    public async Task ChoosingAnItem_RunsExactlyTheMatchingCommand(string id, string expected)
    {
        var (controller, actions, host) = Create(NothingHidden());
        host.Choose = id;

        await controller.ShowAsync(new PointD(10, 20));

        Assert.Equal([expected], actions.Log);
    }

    [Fact]
    public void BuildModel_AllItemsVisible_CostIsReported_NotAsserted()
    {
        // Số đo (không khẳng định thời gian: runner CI chậm/nhiễu): chi phí dựng mô hình đầy đủ mỗi lần mở menu.
        var (controller, _, _) = Create(NothingHidden());
        _ = controller.BuildModel(); // khởi động JIT
        const int runs = 2000;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var count = 0;
        for (var i = 0; i < runs; i++)
        {
            count = controller.BuildModel().Count;
        }

        var micros = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000 / runs;
        output.WriteLine($"BuildModel (tat ca muc hien): {micros:F1} us/lan, {count} muc cap tren");
        Assert.True(count >= 15);
    }

    [Fact]
    public async Task Cancelling_RunsNothing()
    {
        var (controller, actions, host) = Create();
        host.Choose = null;

        await controller.ShowAsync(new PointD(1, 1));

        Assert.Equal(1, host.ShowCount);
        Assert.Empty(actions.Log);
    }

    [Fact]
    public async Task Show_PassesTheAnchorPointToTheHostUnchanged()
    {
        var (controller, _, host) = Create();

        await controller.ShowAsync(new PointD(123.5, 456.25));

        Assert.Equal(new PointD(123.5, 456.25), host.LastPoint);
    }

    [Fact]
    public async Task Show_BuildsTheModelFromTheSettingsAtOpenTime_NotAtConstruction()
    {
        var (controller, actions, host) = Create();

        await controller.ShowAsync(default);
        var first = host.LastItems!.Select(i => i.Id).ToList();
        actions.Settings = NothingHidden();
        await controller.ShowAsync(default);
        var second = host.LastItems!.Select(i => i.Id).ToList();

        Assert.DoesNotContain("MoveToRecycleBin", first);
        Assert.Contains("MoveToRecycleBin", second);
        Assert.Contains("ZoomSubmenu", second);
    }

    [Fact]
    public async Task Show_ReadsClickZoomPercentLive_ForTheZoomToLevelTextAndThePresetCheck()
    {
        var settings = NothingHidden();
        settings.ClickZoomPercent = 200;
        var (controller, actions, host) = Create(settings);

        await controller.ShowAsync(default);
        var zoomMenu = host.LastItems!.Single(i => i.Id == "ZoomSubmenu");
        Assert.True(zoomMenu.Children.Single(c => c.Id == "Zoom.Preset.200").IsChecked);
        Assert.False(zoomMenu.Children.Single(c => c.Id == "Zoom.Preset.100").IsChecked);

        actions.Settings.ClickZoomPercent = 70;
        await controller.ShowAsync(default);
        zoomMenu = host.LastItems!.Single(i => i.Id == "ZoomSubmenu");
        Assert.True(zoomMenu.Children.Single(c => c.Id == "Zoom.Preset.70").IsChecked);
        Assert.False(zoomMenu.Children.Single(c => c.Id == "Zoom.Preset.200").IsChecked);
    }

    [Fact]
    public async Task Show_FeedsTheViewState_IntoTheModel_SetCurrentIsDisabledInFit_CopyNeedsAPhoto()
    {
        var (controller, actions, host) = Create(NothingHidden());
        actions.EffectiveZoom = null; // đang Fit
        actions.HasImage = false;
        actions.CanCopyPath = false;

        await controller.ShowAsync(default);

        var zoom = host.LastItems!.Single(i => i.Id == "ZoomSubmenu");
        Assert.False(zoom.Children.Single(c => c.Id == "Zoom.SetCurrentAsClickLevel").IsEnabled);
        Assert.DoesNotContain(host.LastItems!, i => i.Id is "CopyFileName" or "CopyFullPath");

        actions.EffectiveZoom = 1.5;
        actions.HasImage = true;
        actions.CanCopyPath = true;
        await controller.ShowAsync(default);

        zoom = host.LastItems!.Single(i => i.Id == "ZoomSubmenu");
        Assert.True(zoom.Children.Single(c => c.Id == "Zoom.SetCurrentAsClickLevel").IsEnabled);
        Assert.Contains(host.LastItems!, i => i.Id == "CopyFileName");
    }

    [Fact]
    public async Task Show_HasImageButNoPath_HidesCopy()
    {
        var (controller, actions, host) = Create(NothingHidden());
        actions.HasImage = true;
        actions.CanCopyPath = false;

        await controller.ShowAsync(default);

        Assert.DoesNotContain(host.LastItems!, i => i.Id == "CopyFileName");
    }

    [Fact]
    public async Task Show_UndoAndExternalEditorStayEnabled_EvenWhenTheStateSaysNo()
    {
        // Như WPF: Undo rỗng là no-op, Editor chưa cấu hình thì mở Settings; cả hai không bao giờ tắt.
        var (controller, actions, host) = Create();
        actions.CanUndo = false;
        actions.CanOpenInExternalEditor = false;

        await controller.ShowAsync(default);

        Assert.True(host.LastItems!.Single(i => i.Id == "Undo").IsEnabled);
        Assert.True(host.LastItems!.Single(i => i.Id == "ExternalEditor").IsEnabled);
    }

    [Fact]
    public async Task AlsoSetClickLevel_FlipsTheCurrentValue_BothWays()
    {
        var (controller, actions, host) = Create(NothingHidden());
        host.Choose = "Zoom.AlsoSetClickLevel";
        actions.Settings.SetZoomAlsoSetsClickLevel = true;

        await controller.ShowAsync(default);
        actions.Settings.SetZoomAlsoSetsClickLevel = false;
        await controller.ShowAsync(default);

        Assert.Equal(["AlsoSet:False", "AlsoSet:True"], actions.Log);
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.5, 150)]
    [InlineData(0.04, 10)]
    [InlineData(100.0, 800)]
    public async Task SetCurrentAsClickLevel_RoundsAndClampsTheEffectiveZoom(double zoom, int expected)
    {
        var (controller, actions, host) = Create(NothingHidden());
        host.Choose = "Zoom.SetCurrentAsClickLevel";
        actions.EffectiveZoom = zoom;

        await controller.ShowAsync(default);

        Assert.Equal([$"SetClickLevel:{expected}"], actions.Log);
    }

    [Fact]
    public async Task SetCurrentAsClickLevel_InFit_DoesNothing()
    {
        var (controller, actions, host) = Create(NothingHidden());
        host.Choose = "Zoom.SetCurrentAsClickLevel";
        actions.EffectiveZoom = null;

        await controller.ShowAsync(default);

        Assert.Empty(actions.Log);
    }

    [Theory]
    [InlineData("Separator.2")]
    [InlineData("Separator.Zoom.2")]
    [InlineData("Zoom.Preset.abc")]
    [InlineData("Zoom.Preset.")]
    [InlineData("NoSuchItem")]
    public async Task SeparatorsAndUnknownIds_RunNothing(string id)
    {
        var (controller, actions, _) = Create();

        await controller.ExecuteAsync(id);

        Assert.Empty(actions.Log);
    }

    [Fact]
    public async Task ACommandThatThrows_IsReportedWithItsName_AndNeverEscapes()
    {
        var (controller, actions, host) = Create(NothingHidden());
        host.Choose = "Zoom.Preset.150";
        actions.Failure = new InvalidOperationException("boom");

        await controller.ShowAsync(default); // không ném

        Assert.Equal(["Preset:150", "failed:" + PhotoReview.Core.Localization.Tr.MainMenuZoom + ":InvalidOperationException"], actions.Log);
    }

    [Fact]
    public async Task ACancelledCommand_IsNotAnError()
    {
        var (controller, actions, host) = Create(NothingHidden());
        host.Choose = "ZoomToLevel";
        actions.Failure = new OperationCanceledException();

        await controller.ShowAsync(default);

        Assert.Equal(["ZoomToConfiguredLevel"], actions.Log);
    }

    [Fact]
    public async Task ASynchronousFailure_OfAnItemWithoutAnAsyncBody_IsAlsoReported()
    {
        var (controller, actions, host) = Create();
        host.Choose = "CopyFileName";
        actions.Failure = new IOException("clipboard busy");

        await controller.ShowAsync(default);

        Assert.Equal(["CopyFileName", "failed::IOException"], actions.Log);
    }

    [Fact]
    public async Task OpeningWhileOpen_IsIgnored_AndIsOpenFollowsTheModalMenu()
    {
        var (controller, actions, host) = Create();
        var nestedShows = 0;
        var openDuring = false;
        host.OnShow = () =>
        {
            openDuring = controller.IsOpen;
            _ = controller.ShowAsync(default); // WM_CONTEXTMENU gửi vào trong vòng lặp menu
            nestedShows = host.ShowCount;
            return null;
        };

        Assert.False(controller.IsOpen);
        await controller.ShowAsync(default);

        Assert.True(openDuring);
        Assert.Equal(1, nestedShows);
        Assert.False(controller.IsOpen);
        Assert.Empty(actions.Log);
    }

    [Fact]
    public async Task IsOpen_IsClearedWhenTheHostThrows()
    {
        var (controller, actions, host) = Create();
        host.OnShow = () => throw new InvalidOperationException("no menu");

        await controller.ShowAsync(default); // không còn ném ra WndProc: báo "Menu error"

        Assert.False(controller.IsOpen);
        Assert.Equal(["failed:Menu:InvalidOperationException"], actions.Log);
    }

    [Fact]
    public async Task TheCommand_RunsAfterTheMenuHasClosed()
    {
        var (controller, actions, host) = Create();
        bool? openWhenCommandRan = null;
        host.Choose = "Refresh";
        actions.Log.Clear();
        var probe = new ProbeActions(actions, () => openWhenCommandRan = controller.IsOpen);
        var probed = new ContextMenuController(probe, host);

        await probed.ShowAsync(default);

        Assert.False(openWhenCommandRan);
    }

    private sealed class ProbeActions(FakeContextMenuActions inner, Action onExecute) : IContextMenuActions
    {
        public AppSettings Settings => inner.Settings;
        public bool HasImage => inner.HasImage;
        public bool CanCopyPath => inner.CanCopyPath;
        public bool CanUndo => inner.CanUndo;
        public bool CanOpenInExternalEditor => inner.CanOpenInExternalEditor;
        public bool IsCompareVisible => inner.IsCompareVisible;
        public double? EffectiveZoom => inner.EffectiveZoom;

        public Task ExecuteAsync(ReviewCommand command)
        {
            onExecute();
            return inner.ExecuteAsync(command);
        }

        public Task FitAsync() => inner.FitAsync();
        public Task ZoomToConfiguredLevelAsync() => inner.ZoomToConfiguredLevelAsync();
        public Task ApplyZoomPresetAsync(int percent) => inner.ApplyZoomPresetAsync(percent);
        public Task CustomZoomAsync() => inner.CustomZoomAsync();
        public void SetZoomAlsoSetsClickLevel(bool value) => inner.SetZoomAlsoSetsClickLevel(value);
        public void SetClickZoomPercent(int percent) => inner.SetClickZoomPercent(percent);
        public void OpenInExternalEditor() => inner.OpenInExternalEditor();
        public void CopyFileName() => inner.CopyFileName();
        public void CopyFullPathname() => inner.CopyFullPathname();
        public void ShowSettings() => inner.ShowSettings();
        public void ReportActionFailed(string actionName, Exception exception) => inner.ReportActionFailed(actionName, exception);
    }
}
