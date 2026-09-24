using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhotoReview.App;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Tests;

/// <summary>
/// T47: what remains after removing the source-text checks that now have real behavioral tests
/// (see docs/refactoring/test-parity.md, "T47 outcome"). Two groups are left:
/// <list type="bullet">
/// <item>XAML structure checks that parse the markup with <see cref="XDocument"/> instead of grepping it.</item>
/// <item>Source-presence checks that still have NO behavioral replacement (real HWND, real
/// LocalAppData, Settings window code-behind, PerformanceOptions being internal, install script).
/// They are kept deliberately rather than deleted; each names what would replace it.</item>
/// </list>
/// </summary>
[Trait("Category", "HotPath")]
public sealed class SourcePresenceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static XDocument Xaml(string text) => XDocument.Parse(text);

    // I18N L05: XAML text is "{loc:Tr key}"; these helpers return the catalog keys instead of literal text.
    private static readonly Regex TrMarkup = new(@"^\{\s*loc:Tr\s+(?:Key\s*=\s*)?(?<key>[A-Za-z0-9_.\-]+)\s*\}$", RegexOptions.CultureInvariant);

    private static string? TrKey(string? attributeValue)
    {
        var match = attributeValue is null ? null : TrMarkup.Match(attributeValue.Trim());
        return match is { Success: true } ? match.Groups["key"].Value : null;
    }

    private static IEnumerable<string> AutomationNameKeys(XDocument doc) =>
        doc.Descendants().Select(e => TrKey((string?)e.Attribute("AutomationProperties.Name"))).OfType<string>();

    private static IEnumerable<string> AttributeKeys(XDocument doc) =>
        doc.Descendants().SelectMany(e => e.Attributes()).Select(a => TrKey(a.Value)).OfType<string>();

    private static string Attr(XElement element, string name) => (string?)element.Attribute(name) ?? string.Empty;

    // ---- XAML structure (KEEP-XAML from the T10 matrix, merged per its proposal) ----

    /// <summary>
    /// The accessible names now come from the catalogs. Each case pins the key in the XAML and the Vietnamese text
    /// that key resolves to, which is exactly the literal the XAML held before L05 (so screen readers hear the same).
    /// </summary>
    [Theory(DisplayName = "Accessible names are present in MainWindow, RecoveryWindow and Settings XAML (as keys, same Vietnamese text)")]
    [InlineData("MainWindow.xaml", TrKeys.MainToolbarOpenFolderAutomationName, "Mở thư mục ảnh")]
    [InlineData("MainWindow.xaml", TrKeys.MainToolbarSettingsAutomationName, "Mở cài đặt")]
    [InlineData("MainWindow.xaml", TrKeys.MainCompareLeftAutomationName, "Ảnh xem trước bên trái, nhấn để chọn")]
    [InlineData("MainWindow.xaml", TrKeys.MainCompareRightAutomationName, "Ảnh xem trước bên phải, nhấn để chọn")]
    [InlineData("MainWindow.xaml", TrKeys.MainMenuUndoAutomationName, "Hoàn tác thao tác vừa thực hiện")]
    [InlineData("RecoveryWindow.xaml", TrKeys.RecoveryRetryAutomationName, "Thử lại thao tác Di chuyển hoặc Sao chép bị lỗi")]
    [InlineData("RecoveryWindow.xaml", TrKeys.RecoveryClearSelectedAutomationName, "Xóa các mục đã chọn khỏi danh sách phục hồi")]
    [InlineData("RecoveryWindow.xaml", TrKeys.RecoveryClearAllAutomationName, "Xóa tất cả mục khỏi danh sách phục hồi")]
    [InlineData("SettingsWindow.xaml", TrKeys.SettingsOpenLogLocation, "Mở vị trí tệp log")]
    public void AccessibleNamesArePresentInXaml(string xamlFile, string key, string vietnamese)
    {
        Assert.Contains(key, AutomationNameKeys(Xaml(ProjectSources.Read(xamlFile))));
        Assert.Equal(vietnamese, TestLocalization.Vietnamese.Get(key));
    }

    [Fact(DisplayName = "MainWindow XAML wires lifecycle, drag-drop, compare selection and toolbar overlay")]
    public void MainWindowXamlWiresLifecycleDragDropCompareAndOverlay()
    {
        var doc = Xaml(ProjectSources.MainWindowXaml);
        var window = doc.Root!;

        Assert.Equal("Window_Loaded", Attr(window, "Loaded"));
        Assert.Equal("Window_Closing", Attr(window, "Closing"));
        Assert.Equal("Window_Closed", Attr(window, "Closed"));
        Assert.Equal("True", Attr(window, "AllowDrop"));
        Assert.Equal("Window_PreviewDragOver", Attr(window, "PreviewDragOver"));
        Assert.Equal("Window_Drop", Attr(window, "Drop"));
        Assert.Null(window.Attribute("WindowState"));

        // Compare previews are focusable and handle keyboard selection; images no longer navigate on click.
        var left = Named(doc, "CompareLeftBorder");
        var right = Named(doc, "CompareRightBorder");
        Assert.Equal("True", Attr(left, "Focusable"));
        Assert.Equal("CompareLeft_KeyDown", Attr(left, "PreviewKeyDown"));
        Assert.Equal("True", Attr(right, "Focusable"));
        Assert.Equal("CompareRight_KeyDown", Attr(right, "PreviewKeyDown"));
        Assert.DoesNotContain(doc.Descendants().SelectMany(e => e.Attributes()),
            a => a.Value is "Image_LeftClick" or "Image_RightClick");

        // The context menu keeps the Undo entry.
        Assert.Contains(doc.Descendants(Presentation + "MenuItem"), e => Attr(e, "Click") == "UndoLastAction_Click");

        // The image fills the client area and the toolbar is a compact overlay: the root grid has no
        // row definitions and the toolbar border sits on top with a translucent background.
        var rootGrid = window.Elements(Presentation + "Grid").Single();
        Assert.Null(rootGrid.Element(Presentation + "Grid.RowDefinitions"));
        Assert.Contains(rootGrid.Elements(Presentation + "Border"),
            b => Attr(b, "Panel.ZIndex") == "100" && Attr(b, "Background") == "#B0181818");

        // The folder text is kept in the tree but hidden; the folder is shown in the native title bar.
        var folderText = Named(doc, "FolderText");
        Assert.Equal("Collapsed", Attr(folderText, "Visibility"));
        Assert.Contains("FolderTitle", Attr(window, "Title"));
    }

    [Fact(DisplayName = "Recovery and Diagnostics XAML expose their documented controls")]
    public void RecoveryAndDiagnosticsXamlExposeDocumentedControls()
    {
        Assert.Contains(TrKeys.RecoveryRetry, AttributeKeys(Xaml(ProjectSources.RecoveryWindowXaml)));
        Assert.Equal("Thử lại Di chuyển/Sao chép", TestLocalization.Vietnamese.Get(TrKeys.RecoveryRetry));

        Assert.Contains(TrKeys.DiagnosticsLabelSourceFileReads, AttributeKeys(Xaml(ProjectSources.DiagnosticsWindowXaml)));
        Assert.Equal("Số lần đọc tệp nguồn", TestLocalization.Vietnamese.Get(TrKeys.DiagnosticsLabelSourceFileReads));
    }

    [Fact(DisplayName = "I18N L08: the Settings language group header is bilingual in every shipped catalog")]
    public void SettingsLanguageGroupHeaderIsBilingual()
    {
        var header = Attr(Named(Xaml(ProjectSources.SettingsWindowXaml), "LanguageGroup"), "Header");
        Assert.Equal(TrKeys.SettingsGroupLanguage, TrKey(header));
        Assert.Equal("Language / Ngôn ngữ", TestLocalization.Vietnamese.Get(TrKeys.SettingsGroupLanguage));
        Assert.Equal("Language / Ngôn ngữ", TestLocalization.English.Get(TrKeys.SettingsGroupLanguage));
    }

    // ---- Kept source-presence checks with no behavioral replacement yet ----

    // WindowPlacementService operates on a real HWND via GetWindowPlacement/SetWindowPlacement
    // and reads the attached monitor set, so it cannot be exercised headlessly.
    [Fact(DisplayName = "Native window placement restores after Loaded and persists monitor, bounds, and maximized state (source presence: needs a real Window handle)")]
    public void NativeWindowPlacementRestoresAndPersists() =>
        Assert.True(!ProjectSources.MainWindow.Contains("Window_SourceInitialized")
            && ProjectSources.MainWindow.Contains("WindowPlacementService.Restore(this)")
            && ProjectSources.MainWindow.Contains("WindowPlacementService.Save(this)")
            && ProjectSources.WindowPlacementService.Contains("GetWindowPlacement")
            && ProjectSources.WindowPlacementService.Contains("SetWindowPlacement"));

    [Fact(DisplayName = "Saved placement is rejected when its monitor is no longer connected (source presence: needs real multi-monitor hardware)")]
    public void SavedPlacementIsRejectedWhenMonitorIsGone() =>
        Assert.True(ProjectSources.WindowPlacementService.Contains("EnumDisplayMonitors")
            && ProjectSources.WindowPlacementService.Contains("GetMonitorInfo"));

    [Fact(DisplayName = "File association command is registered (source presence: registering needs the real Windows registry)")]
    public void FileAssociationCommandIsRegistered()
    {
        var association = Path.Combine(ProjectSources.ProjectRoot, "outputs", "install-photo-review-association.ps1");
        var associationText = File.ReadAllText(association);
        Assert.True(associationText.Contains("$progId\\shell\\open\\command") && associationText.Contains("\"%1\""));
    }
}
