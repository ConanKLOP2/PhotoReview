using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10 G-MENU (<c>context-menu.v1.json</c>): for 12 settings configurations, the ids of the VISIBLE right-click menu entries of the
/// real <see cref="MainWindow"/>, in order, separators included. Ids: <c>ContextMenuItemId</c> names for the top level and the Zoom
/// submenu's visibility groups; <c>Separator:{group}</c> for the line drawn before a group (top-level groups 2..6 as in
/// <c>ContextMenuItems</c>; <c>Separator:Zoom.{group}</c> inside the submenu); <c>Zoom.Preset.{percent}</c>, <c>Zoom.Custom</c>,
/// <c>Zoom.AlsoSetClickLevel</c>, <c>Zoom.SetCurrentAsClickLevel</c> for the submenu's built-in items. Submenu entries follow the
/// <c>ZoomSubmenu</c> entry (flat list). Name prefix: <c>photo|</c> = an image is open, <c>nophoto|</c> = no folder open.
/// </summary>
internal static class MenuGoldenRecorder
{
    internal sealed record MenuConfig(string Name, string SettingsJson);

    internal static readonly MenuConfig[] Configs =
    [
        new("photo|default", "{}"),
        new("nophoto|default", "{}"),
        new("photo|all-visible", "{\"hiddenContextMenuItems\":[]}"),
        new("nophoto|all-visible", "{\"hiddenContextMenuItems\":[]}"),
        new("photo|hide-zoom-cluster", "{\"hiddenContextMenuItems\":[\"Fit\",\"ZoomToLevel\",\"ZoomSubmenu\"]}"),
        new("photo|hide-folder-group", "{\"hiddenContextMenuItems\":[\"OpenFolder\",\"NextFolder\",\"PreviousFolder\"]}"),
        new("photo|hide-editor-and-copy", "{\"hiddenContextMenuItems\":[\"ExternalEditor\",\"CopyFileName\",\"CopyFullPath\"],\"externalEditorPath\":\"C:\\\\Tools\\\\editor.exe\"}"),
        new("photo|zoom-submenu-no-presets", "{\"hiddenContextMenuItems\":[\"ZoomPresets\"]}"),
        new("photo|zoom-submenu-fitwidth-only", "{\"hiddenContextMenuItems\":[\"ZoomFitWidth2\",\"ZoomFitHeight\",\"ZoomPresets\",\"ZoomLevelOptions\"]}"),
        new("photo|only-undo-and-settings", "{\"hiddenContextMenuItems\":[\"MoveToRecycleBin\",\"Fit\",\"ZoomToLevel\",\"ZoomSubmenu\",\"Refresh\",\"OpenFolder\",\"NextFolder\",\"PreviousFolder\",\"ExternalEditor\",\"CopyFileName\",\"CopyFullPath\"]}"),
        new("photo|legacy-flags", "{\"showZoomMenuItems\":true,\"showFolderMenuItems\":false,\"showRecycleMenuItem\":true}"),
        new("photo|hide-undo-recycle-refresh", "{\"hiddenContextMenuItems\":[\"Undo\",\"MoveToRecycleBin\",\"Refresh\"]}"),
    ];

    internal const string Notes =
        "Recorded from the WPF MainWindow: ContextMenu.Opened raised on ImageContextMenu (ApplyContextMenuLayout), then every entry of " +
        "ImageContextMenu.Items (and the Zoom submenu when ZoomSubmenu is visible) with Visibility=Visible, in order. Ids: see " +
        "MenuGoldenRecorder. Name prefix photo|/nophoto| says whether an image is open. SettingsOverridesJson applies on top of the defaults.";

    public static async Task<IReadOnlyList<GoldenMenuCase>> RecordAsync()
    {
        using var folder = new TempRoot("golden-menu");
        WriteImage(Path.Combine(folder.Path, "a.png"));
        var cases = new List<GoldenMenuCase>();
        await GoldenWpfHost.RunAsync(view => RecordCases(view, cases, photo: true), folder.Path);
        await GoldenWpfHost.RunAsync(view => RecordCases(view, cases, photo: false));
        return Configs.Select(config => cases.Single(c => c.Name == config.Name)).ToList();
    }

    private static Task RecordCases(GoldenWpfView view, List<GoldenMenuCase> cases, bool photo)
    {
        foreach (var config in Configs.Where(c => c.Name.StartsWith(photo ? "photo|" : "nophoto|", StringComparison.Ordinal)))
        {
            view.ApplySettings(config.SettingsJson);
            view.Window.ImageContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, view.Window.ImageContextMenu));
            cases.Add(new GoldenMenuCase(config.Name, config.SettingsJson, VisibleIds(view.Window)));
        }
        return Task.CompletedTask;
    }

    /// <summary>The visible ids of <paramref name="window"/>'s context menu right now (shared by the recorder and the conformance test).</summary>
    internal static IReadOnlyList<string> VisibleIds(MainWindow window)
    {
        var ids = new List<string>();
        foreach (var item in window.ImageContextMenu.Items.OfType<FrameworkElement>())
        {
            if (item.Visibility != Visibility.Visible) continue;
            ids.Add(TopLevelId(window, item));
            if (ReferenceEquals(item, window.ZoomMenu)) ids.AddRange(SubmenuIds(window));
        }
        return ids;
    }

    private static string TopLevelId(MainWindow window, FrameworkElement item) => item.Name switch
    {
        "UndoMenuItem" => "Undo",
        "RecycleMenuItem" => "MoveToRecycleBin",
        "FitMenuItem" => "Fit",
        "ZoomToLevelMenuItem" => "ZoomToLevel",
        "ZoomMenu" => "ZoomSubmenu",
        "RefreshMenuItem" => "Refresh",
        "OpenFolderMenuItem" => "OpenFolder",
        "NextFolderMenuItem" => "NextFolder",
        "PreviousFolderMenuItem" => "PreviousFolder",
        "OpenInExternalEditorMenuItem" => "ExternalEditor",
        "CopyFileNameMenuItem" => "CopyFileName",
        "CopyFullPathMenuItem" => "CopyFullPath",
        "SettingsMenuItem" => "Settings",
        "ZoomGroupSeparator" => "Separator:2",
        "FolderGroupSeparator" => "Separator:3",
        "ExternalEditorGroupSeparator" => "Separator:4",
        "CopyGroupSeparator" => "Separator:5",
        "SettingsGroupSeparator" => "Separator:6",
        _ => throw new InvalidOperationException($"Unmapped context-menu entry '{item.Name}' ({item.GetType().Name}) in {window.ImageContextMenu.Name}."),
    };

    /// <summary>The Zoom submenu is built in code (MainWindow.BuildZoomMenu): ids by position and tag.</summary>
    private static IEnumerable<string> SubmenuIds(MainWindow window)
    {
        var items = window.ZoomMenu.Items.OfType<FrameworkElement>().ToList();
        string[] fixedHead = ["ZoomFitWidth", "ZoomFitWidth2", "ZoomFitHeight", "Separator:Zoom.2"];
        var presetCount = items.Count(i => i is MenuItem { Tag: int });
        var tail = new[] { "Zoom.Custom", "Separator:Zoom.3", "Zoom.AlsoSetClickLevel", "Zoom.SetCurrentAsClickLevel" };
        var expectedCount = fixedHead.Length + presetCount + tail.Length;
        if (items.Count != expectedCount) throw new InvalidOperationException($"Zoom submenu has {items.Count} entries, expected {expectedCount}.");

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Visibility != Visibility.Visible) continue;
            if (index < fixedHead.Length) yield return fixedHead[index];
            else if (index < fixedHead.Length + presetCount)
                yield return "Zoom.Preset." + ((int)((MenuItem)items[index]).Tag).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else yield return tail[index - fixedHead.Length - presetCount];
        }
    }

    private static void WriteImage(string path)
    {
        var bitmap = new WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static GoldenDocument<GoldenMenuCase> Document(IReadOnlyList<GoldenMenuCase> cases) =>
        new("context-menu", 1, "PhotoReview.Integration.Tests.Golden.MenuGoldenRecorder (WPF MainWindow)", Notes, cases);
}
