using System.IO;
using System.Xml.Linq;
using PhotoReview.App;

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

    private static IEnumerable<string> AutomationNames(XDocument doc) =>
        doc.Descendants().Select(e => (string?)e.Attribute("AutomationProperties.Name")).OfType<string>();

    private static string Attr(XElement element, string name) => (string?)element.Attribute(name) ?? string.Empty;

    // ---- XAML structure (KEEP-XAML from the T10 matrix, merged per its proposal) ----

    [Fact(DisplayName = "Accessible names are present in MainWindow, RecoveryWindow and Settings XAML")]
    public void AccessibleNamesArePresentInXaml()
    {
        var main = AutomationNames(Xaml(ProjectSources.MainWindowXaml)).ToList();
        Assert.Contains("Mở thư mục ảnh", main);
        Assert.Contains("Mở cài đặt", main);
        Assert.Contains("Preview ảnh bên trái, nhấn để chọn", main);
        Assert.Contains("Preview ảnh bên phải, nhấn để chọn", main);
        Assert.Contains("Hoàn tác thao tác vừa thực hiện", main);

        Assert.Contains("Thử lại Move hoặc Copy đã lỗi", AutomationNames(Xaml(ProjectSources.RecoveryWindowXaml)));
        Assert.Contains("Mở vị trí file log", AutomationNames(Xaml(ProjectSources.SettingsWindowXaml)));
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
        var recovery = Xaml(ProjectSources.RecoveryWindowXaml);
        Assert.Contains(recovery.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("Retry Move/Copy", StringComparison.Ordinal));

        var diagnostics = Xaml(ProjectSources.DiagnosticsWindowXaml);
        Assert.Contains(diagnostics.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("Source file reads", StringComparison.Ordinal));
    }

    // ---- Kept source-presence checks with no behavioral replacement yet ----

    // Replace when Settings gets a view model (T46c only wired the dialog service, not the window's code-behind).
    [Fact(DisplayName = "Settings defaults reset compare options (source presence, not behavior)")]
    public void SettingsDefaultsResetCompareOptions() =>
        Assert.True(ProjectSources.SettingsWindow.Contains("CompareHashEnabled = true")
            && ProjectSources.SettingsWindow.Contains("CompareSizeEnabled = true"));

    [Fact(DisplayName = "Open log location follows the configured AppLog path (source presence, not behavior)")]
    public void OpenLogLocationFollowsConfiguredAppLogPath()
    {
        var settingsWindow = ProjectSources.SettingsWindow;
        Assert.True(settingsWindow.Contains("Path.GetDirectoryName(AppLog.FilePath)")
            && settingsWindow.Contains("explorer.exe") && settingsWindow.Contains("AppLog.FilePath"));
    }

    // Window shutdown itself is WPF glue (Closed handler on the Window); the scheduler's own
    // disposal is covered behaviorally by PreloadSchedulerTests.
    [Fact(DisplayName = "Window shutdown disposes preload and thumbnail resources (source presence, not behavior)")]
    public void WindowShutdownDisposesPreloadAndThumbnailResources() =>
        Assert.True(ProjectSources.MainWindowXaml.Contains("Closed=\"Window_Closed\"")
            && ProjectSources.MainWindow.Contains("(_viewModel.PreloadController as IDisposable)?.Dispose()")
            && ProjectSources.MainWindow.Contains("_explorerOrder?.Dispose()"));

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

    // ThumbnailCache does not use DiskCacheStore yet, and prunes the shared on-disk cache under the
    // user's real LocalAppData, so driving the quota path would mutate the developer's own cache.
    [Fact(DisplayName = "Disk thumbnail cache has quota and clear operation (source presence, not behavior)")]
    public void DiskThumbnailCacheHasQuotaAndClearOperation() =>
        Assert.True(ProjectSources.ThumbnailCache.Contains("DefaultMaxDiskBytes")
            && ProjectSources.ThumbnailCache.Contains("PruneDiskCache")
            && ProjectSources.ThumbnailCache.Contains("ClearDisk"));

    [Fact(DisplayName = "Disk cache cleanup tolerates filesystem access failures (source presence, not behavior)")]
    public void DiskCacheCleanupToleratesFilesystemAccessFailures() =>
        Assert.True(ProjectSources.ThumbnailCache.Contains("catch (UnauthorizedAccessException ex)")
            && ProjectSources.ThumbnailCache.Contains("catch (IOException ex)"));

    [Fact(DisplayName = "File association command is registered (source presence: registering needs the real Windows registry)")]
    public void FileAssociationCommandIsRegistered()
    {
        var association = Path.Combine(ProjectSources.ProjectRoot, "outputs", "install-photo-review-association.ps1");
        var associationText = File.ReadAllText(association);
        Assert.True(associationText.Contains("$progId\\shell\\open\\command") && associationText.Contains("\"%1\""));
    }
}
