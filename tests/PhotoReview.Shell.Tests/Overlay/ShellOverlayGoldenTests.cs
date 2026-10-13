using System.Globalization;
using PhotoReview.App.Input;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Viewport;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Golden;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Overlay;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OverlayLocalizationGroup
{
    /// <summary>Test đổi <c>Localizer</c> toàn tiến trình: chạy một mình, không song song với test khác.</summary>
    public const string Name = "GlobalState";
}

/// <summary>
/// WP-23 (G-OVL trên shell): cây overlay THẬT của shell (<see cref="ShellOverlay"/>: ToolbarPanel + StatusPanel +
/// FolderInfoPanel + ZoomIndicatorPanel + CapturePairBadge) với DirectWrite thật và tiếng Việt (như lúc ghi golden) khớp
/// <c>overlay-boxes.v1.json</c> +/- 2 DIP: 6 panel x 3 cửa sổ x 2 DPI x 2 cỡ chữ = 72 ca. Chuỗi cố định của recorder được đẩy
/// qua <see cref="OverlayModel"/> (cùng đường mà view model dùng). Không phụ thuộc kích thước màn hình runner.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Native")]
public sealed class ShellOverlayGoldenTests(ITestOutputHelper output)
{
    private const double Tolerance = 2.0;

    private static readonly string[] Panels =
        ["ToolbarPanel", "StatusPanel", "FolderInfoPanel", "ZoomIndicatorPanel", "CapturePairBadge"];

    private static OverlayModel RecorderModel(double font) => new()
    {
        HasImages = true,
        StatusPanelVisible = true,
        FileInfoVisible = true,
        StatusText = OverlayGoldenTests.StatusLine,
        FontSize = font,
        ExifFontSize = font - 1,
        ExifVisible = true,
        ExifText = OverlayGoldenTests.ExifLine,
        FolderInfoVisible = true,
        FolderInfoText = OverlayGoldenTests.FolderLine,
        ZoomIndicatorVisible = true,
        ZoomIndicatorText = OverlayGoldenTests.ZoomLine,
        CapturePairVisible = true,
        CapturePairText = OverlayGoldenTests.CapturePairLine,
    };

    private static ShellOverlay Build(ITextRenderer text, FakeFrameClock clock, double font)
    {
        var overlay = new ShellOverlay(new ShellOverlayOptions
        {
            Text = text,
            Clock = clock,
            Settings = () => new PhotoReview.Core.Settings.AppSettings(),
            Commands = new FakeOverlayCommands(),
            RequestRender = static () => { },
            IsWindowActive = static () => true,
        });
        overlay.Update(RecorderModel(font));
        return overlay;
    }

    [GoldenFact("overlay-boxes.v1.json")]
    public void SixShellPanelsMatchTheWpfGoldenWithinTwoDips()
    {
        using IDisposable vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        string path = GoldenFixture.Find("overlay-boxes.v1.json")!;
        IReadOnlyList<GoldenOverlayBox> golden = GoldenFixture.ReadCases<GoldenOverlayBox>(File.ReadAllText(path));
        using var renderer = new DWriteTextRenderer("vi-VN", 512);
        var failures = new List<string>();
        int checkedCases = 0;
        double worst = 0;
        foreach (var group in golden.GroupBy(b => (b.WindowWidth, b.WindowHeight, b.DpiScale, b.OverlayFontSize)))
        {
            using ShellOverlay overlay = Build(renderer, new FakeFrameClock(), group.Key.OverlayFontSize);
            overlay.Arrange(new SizeD(group.Key.WindowWidth, group.Key.WindowHeight), group.Key.DpiScale);
            foreach (GoldenOverlayBox box in group.Where(b => Panels.Contains(b.Panel)))
            {
                checkedCases++;
                RectD actual = overlay.Host.Find(box.Panel)!.Bounds;
                double d = Math.Max(
                    Math.Max(Math.Abs(actual.X - box.X), Math.Abs(actual.Y - box.Y)),
                    Math.Max(Math.Abs(actual.Width - box.Width), Math.Abs(actual.Height - box.Height)));
                worst = Math.Max(worst, d);
                if (d > Tolerance)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{box.Name}: golden ({box.X:F2},{box.Y:F2},{box.Width:F2},{box.Height:F2}) got ({actual.X:F2},{actual.Y:F2},{actual.Width:F2},{actual.Height:F2}) delta {d:F2}"));
                }
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"G-OVL (shell): {checkedCases} cases, worst delta {worst:F3} DIP (tolerance {Tolerance})"));
        Assert.Equal(60, checkedCases);
        Assert.True(failures.Count == 0, $"worst delta {worst:F2} DIP; " + string.Join("\n", failures.Take(12)));
    }

    [GoldenFact("overlay-boxes.v1.json")]
    public void ToolbarBoxMatchesTheGolden_Is210x40_AtEveryWindowSizeAndDpi()
    {
        using IDisposable vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        string path = GoldenFixture.Find("overlay-boxes.v1.json")!;
        var toolbarCases = GoldenFixture.ReadCases<GoldenOverlayBox>(File.ReadAllText(path)).Where(b => b.Panel == "ToolbarPanel").ToList();
        Assert.Equal(12, toolbarCases.Count);
        using var renderer = new DWriteTextRenderer("vi-VN", 512);
        double worst = 0;
        foreach (GoldenOverlayBox box in toolbarCases)
        {
            using ShellOverlay overlay = Build(renderer, new FakeFrameClock(), box.OverlayFontSize);
            overlay.Arrange(new SizeD(box.WindowWidth, box.WindowHeight), box.DpiScale);
            RectD actual = overlay.Toolbar.Bounds;
            worst = Math.Max(worst, Math.Max(Math.Abs(actual.Width - box.Width), Math.Abs(actual.Height - box.Height)));
            Assert.Equal(box.X, actual.X, 3);
            Assert.Equal(box.Y, actual.Y, 3);
            Assert.True(Math.Abs(actual.Width - box.Width) <= Tolerance, $"{box.Name}: width {actual.Width:F2} vs {box.Width:F2}");
            Assert.True(Math.Abs(actual.Height - box.Height) <= Tolerance, $"{box.Name}: height {actual.Height:F2} vs {box.Height:F2}");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Toolbar: worst size delta {worst:F3} DIP"));
    }

    [Fact]
    public void ToolbarKeepsItsBox_WhenOnlyTheDpiChanges()
    {
        using IDisposable vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        using var renderer = new DWriteTextRenderer("vi-VN", 512);
        using ShellOverlay overlay = Build(renderer, new FakeFrameClock(), 12);
        overlay.Arrange(new SizeD(800, 600), 1.0);
        RectD at1 = overlay.Toolbar.Bounds;
        overlay.Arrange(new SizeD(800, 600), 1.5);
        Assert.Equal(at1, overlay.Toolbar.Bounds);
    }

    [Fact]
    public void LanguageSwitch_ChangesToolbarTextTooltipsAndWidth_WithoutRebuilding()
    {
        using var renderer = new DWriteTextRenderer("en-US", 512);
        using ShellOverlay overlay = Build(renderer, new FakeFrameClock(), 12);
        string english;
        double englishWidth;
        using (TestLocalization.Use(TestLocalization.English))
        {
            overlay.ApplyLocalization();
            english = overlay.Toolbar.Fit.Content;
            overlay.Arrange(new SizeD(800, 600), 1);
            englishWidth = overlay.Toolbar.Bounds.Width;
            Assert.Equal(Tr.MainToolbarFit, english);
            Assert.Equal(Tr.MainToolbarFitToolTip, overlay.Toolbar.Fit.ToolTip);
            Assert.Equal(Tr.MainSkippedViewList, overlay.Status.ViewList.Content);
        }

        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            overlay.ApplyLocalization();
            Assert.Equal(Tr.MainToolbarFit, overlay.Toolbar.Fit.Content);
            Assert.NotEqual(english, overlay.Toolbar.Fit.Content);
            Assert.Equal(Tr.MainToolbarOpenFolderToolTip, overlay.Toolbar.OpenFolder.ToolTip);
            Assert.Equal(Tr.MainToolbarToolsAutomationName, overlay.Toolbar.Tools.AutomationName);
            Assert.Equal(Tr.MainFolderInfoAutomationName, overlay.FolderInfo.AutomationName);
            Assert.Equal(Tr.MainZoomIndicatorAutomationName, overlay.ZoomIndicator.AutomationName);
            Assert.Equal(Tr.MainSkippedViewListAutomationName, overlay.Status.ViewList.AutomationName);
            overlay.Arrange(new SizeD(800, 600), 1);
            Assert.NotEqual(englishWidth, overlay.Toolbar.Bounds.Width);
        }
    }
}
