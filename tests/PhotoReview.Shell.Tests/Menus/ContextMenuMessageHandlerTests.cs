using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Input;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// WP-24: WM_CONTEXTMENU -> menu, qua cửa sổ giả (không HWND, không phụ thuộc kích thước màn hình hay viền vô hình của
/// GetWindowRect): toạ độ màn hình pixel có dấu, DPI, phím Menu/Shift+F10 (lParam -1), vùng non-client, nút phải KHÔNG tự mở.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ContextMenuMessageHandlerTests
{
    private static nint Pack(int x, int y) => (nint)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);

    private static (FakeShellWindow Window, FakePopupMenuHost Host, FakeContextMenuActions Actions) Create(double dpi = 1.0,
        PointD? clientOriginPixel = null)
    {
        var window = new FakeShellWindow { DpiScale = dpi, ClientSizeDip = new SizeD(1000, 600), ClientOriginPixel = clientOriginPixel ?? default };
        var actions = new FakeContextMenuActions();
        var host = new FakePopupMenuHost();
        window.AddMessageHandler(new ContextMenuMessageHandler(window, new ContextMenuController(actions, host)));
        return (window, host, actions);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void RightClickInsideTheClient_OpensTheMenu_AtTheScreenPointInDip(double dpi)
    {
        var (window, host, _) = Create(dpi, new PointD(100, 50));

        var (handled, result) = window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(400, 300));

        Assert.True(handled);
        Assert.Equal(0, result);
        Assert.Equal(1, host.ShowCount);
        Assert.Equal(400 / dpi, host.LastPoint!.Value.X, 6);
        Assert.Equal(300 / dpi, host.LastPoint!.Value.Y, 6);
    }

    [Fact]
    public void RightClickBeyondTheClientEdges_IsLeftToTheSystemMenu()
    {
        var (window, host, _) = Create(1.0, new PointD(100, 50));

        // trên thanh tiêu đề (trên gốc client) / bên trái / dưới đáy (client cao 600 DIP) / bên phải
        foreach (var (x, y) in new[] { (400, 20), (90, 300), (400, 50 + 600), (100 + 1000, 300) })
        {
            var (handled, _) = window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(x, y));
            Assert.False(handled, $"({x},{y}) là non-client");
        }

        Assert.Equal(0, host.ShowCount);
    }

    [Fact]
    public void TheClientEdges_AreInside_TopLeftInclusiveBottomRightExclusive()
    {
        var (window, host, _) = Create(1.0, new PointD(100, 50));

        Assert.True(window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(100, 50)).Handled);
        Assert.True(window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(100 + 999, 50 + 599)).Handled);
        Assert.False(window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(100 + 1000, 50 + 599)).Handled);
        Assert.Equal(2, host.ShowCount);
    }

    [Fact]
    public void ScreenCoordinatesLeftOfThePrimaryMonitor_AreSignedNotWrapped()
    {
        var (window, host, _) = Create(1.0, new PointD(-1500, 20));

        var (handled, _) = window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(-1200, 200));

        Assert.True(handled);
        Assert.Equal(new PointD(-1200, 200), host.LastPoint);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void KeyboardInvocation_OpensAtTheCentreOfTheClient(double dpi)
    {
        var (window, host, _) = Create(dpi, new PointD(100, 50));

        var (handled, _) = window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(-1, -1));

        Assert.True(handled);
        // gốc client = (100,50) px; giữa = gốc + 1000x600 DIP / 2 theo DPI; đổi sang DIP màn hình chia DPI.
        Assert.Equal((100 / dpi) + 500, host.LastPoint!.Value.X, 6);
        Assert.Equal((50 / dpi) + 300, host.LastPoint!.Value.Y, 6);
    }

    [Theory]
    [InlineData(WindowMessages.WmRButtonDown)]
    [InlineData(WindowMessages.WmRButtonUp)]
    [InlineData(WindowMessages.WmLButtonDown)]
    [InlineData(WindowMessages.WmKeyDown)]
    public void OtherMessages_NeverOpenTheMenu(uint message)
    {
        var (window, host, _) = Create();

        var (handled, _) = window.Dispatch(message, 0, Pack(400, 300));

        Assert.False(handled);
        Assert.Equal(0, host.ShowCount);
    }

    [Fact]
    public async Task ChoosingAnItem_FromTheWindowMessage_RunsItsCommand()
    {
        var (window, host, actions) = Create();
        host.Choose = "Refresh";

        window.Dispatch(WindowMessages.WmContextMenu, window.Hwnd, Pack(10, 10));
        await Task.Yield();

        Assert.Equal(["Execute:Refresh"], actions.Log);
    }

    // ---- G-MENU: golden của bản WPF qua controller ----

    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Áp các khoá của <c>settingsOverridesJson</c> lên cài đặt mặc định (cùng cách <c>GoldenWpfView.ApplySettings</c>).</summary>
    internal static AppSettings SettingsFrom(string overridesJson)
    {
        var settings = new AppSettings();
        if (string.IsNullOrWhiteSpace(overridesJson))
        {
            return settings;
        }

        using var document = JsonDocument.Parse(overridesJson);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var property = typeof(AppSettings).GetProperty(entry.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?? throw new InvalidOperationException($"Unknown AppSettings property in golden setup: {entry.Name}");
            property.SetValue(settings, JsonSerializer.Deserialize(entry.Value.GetRawText(), property.PropertyType, SettingsOptions));
        }

        return settings;
    }

    /// <summary>Dạng id của golden: dấu phân cách "Separator:N", submenu Zoom phẳng ngay sau mục ZoomSubmenu.</summary>
    internal static IEnumerable<string> GoldenIds(IReadOnlyList<MenuItemModel> items)
    {
        foreach (var item in items)
        {
            if (item.Kind == MenuItemKind.Separator)
            {
                yield return "Separator:" + item.Id["Separator.".Length..];
                continue;
            }

            yield return item.Id;
            foreach (var child in GoldenIds(item.Children))
            {
                yield return child;
            }
        }
    }

    [Fact]
    public async Task GMenu_EveryGoldenCase_MatchesTheWpfMenu_ThroughTheController()
    {
        var golden = GoldenFile.Read(GoldenFile.ContextMenu, GoldenJsonContext.Default.GoldenDocumentGoldenMenuCase);
        Assert.NotEmpty(golden.Items);

        foreach (var golden_case in golden.Items)
        {
            var photo = golden_case.Name.StartsWith("photo|", StringComparison.Ordinal);
            var actions = new FakeContextMenuActions
            {
                Settings = SettingsFrom(golden_case.SettingsOverridesJson),
                HasImage = photo,
                CanCopyPath = photo,
                EffectiveZoom = 1.0,
            };
            var host = new FakePopupMenuHost();
            var controller = new ContextMenuController(actions, host);

            await controller.ShowAsync(default);

            Assert.True(host.LastItems is not null, golden_case.Name);
            Assert.Equal(golden_case.VisibleIdsInOrder, GoldenIds(host.LastItems!).ToList());
        }
    }

    [Fact]
    public void GMenu_TheGoldenCoversAllTwelveConfigurations()
    {
        var golden = GoldenFile.Read(GoldenFile.ContextMenu, GoldenJsonContext.Default.GoldenDocumentGoldenMenuCase);

        Assert.Equal(12, golden.Items.Count);
        Assert.Contains(golden.Items, c => c.Name == "photo|legacy-flags");
        Assert.Contains(golden.Items, c => c.Name == "nophoto|all-visible");
    }
}
