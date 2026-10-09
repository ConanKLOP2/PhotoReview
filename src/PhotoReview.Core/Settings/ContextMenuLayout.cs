namespace PhotoReview.Core.Settings;

/// <summary>
/// Stable identifiers of the right-click menu items the user can turn on or off. The enum NAME is what
/// <see cref="AppSettings.HiddenContextMenuItems"/> stores in <c>config.json</c>, so never rename a member (add new ones at the end).
/// </summary>
public enum ContextMenuItemId
{
    Undo,
    MoveToRecycleBin,
    Fit,
    ZoomToLevel,
    ZoomSubmenu,
    OpenFolder,
    NextFolder,
    PreviousFolder,
    ExternalEditor,
    CopyFileName,
    CopyFullPath,
    /// <summary>Always shown: switching it off would lock the user out of Settings.</summary>
    Settings,
    // Children of the Zoom submenu.
    ZoomFitWidth,
    ZoomFitWidth2,
    ZoomFitHeight,
    ZoomPresets,
    ZoomLevelOptions,
}

/// <summary>One entry of the item table: where it sits (parent, group), and whether it can be turned off / needs an open photo.</summary>
public sealed record ContextMenuItemInfo(ContextMenuItemId Id, ContextMenuItemId? Parent, int Group, bool Locked = false, bool NeedsPhoto = false);

/// <summary>What the menu is opened over (the context part of the visibility rule).</summary>
public sealed record ContextMenuContext(bool HasPhoto);

/// <summary>One line of a computed menu: an item, or (Item = null) the separator drawn right before <see cref="Group"/>.</summary>
public sealed record ContextMenuEntry(ContextMenuItemId? Item, int Group);

/// <summary>The computed menu level: ordered entries (items and separators) to show.</summary>
public sealed class ContextMenuLayout
{
    /// <summary>Ordered lines to show; separators only sit BETWEEN two non-empty groups (never first, last or doubled).</summary>
    public IReadOnlyList<ContextMenuEntry> Entries { get; }

    internal ContextMenuLayout(IReadOnlyList<ContextMenuEntry> entries) => Entries = entries;

    public bool IsVisible(ContextMenuItemId id) => Entries.Any(e => e.Item == id);

    public bool HasSeparatorBefore(int group) => Entries.Any(e => e.Item is null && e.Group == group);
}

/// <summary>
/// The single list of right-click menu items plus the pure visibility rule (items + hidden set + context -> what to show).
/// Top-level groups, in menu order: 1 Undo/Recycle, 2 zoom cluster, 3 folders, 4 external editor, 5 copy, 6 Settings.
/// </summary>
public static class ContextMenuItems
{
    /// <summary>Every item, in menu order (top level first, then the Zoom submenu's children).</summary>
    public static IReadOnlyList<ContextMenuItemInfo> Items { get; } =
    [
        new(ContextMenuItemId.Undo, null, 1),
        new(ContextMenuItemId.MoveToRecycleBin, null, 1),
        new(ContextMenuItemId.Fit, null, 2),
        new(ContextMenuItemId.ZoomToLevel, null, 2),
        new(ContextMenuItemId.ZoomSubmenu, null, 2),
        new(ContextMenuItemId.OpenFolder, null, 3),
        new(ContextMenuItemId.NextFolder, null, 3),
        new(ContextMenuItemId.PreviousFolder, null, 3),
        new(ContextMenuItemId.ExternalEditor, null, 4),
        new(ContextMenuItemId.CopyFileName, null, 5, NeedsPhoto: true),
        new(ContextMenuItemId.CopyFullPath, null, 5, NeedsPhoto: true),
        new(ContextMenuItemId.Settings, null, 6, Locked: true),
        new(ContextMenuItemId.ZoomFitWidth, ContextMenuItemId.ZoomSubmenu, 1),
        new(ContextMenuItemId.ZoomFitWidth2, ContextMenuItemId.ZoomSubmenu, 1),
        new(ContextMenuItemId.ZoomFitHeight, ContextMenuItemId.ZoomSubmenu, 1),
        new(ContextMenuItemId.ZoomPresets, ContextMenuItemId.ZoomSubmenu, 2),
        new(ContextMenuItemId.ZoomLevelOptions, ContextMenuItemId.ZoomSubmenu, 3),
    ];

    public static ContextMenuItemInfo Info(ContextMenuItemId id) => Items.First(i => i.Id == id);

    /// <summary>The ids that are hidden by default (the pre-customisation defaults: the zoom cluster and Move to Recycle Bin).</summary>
    public static IReadOnlySet<ContextMenuItemId> DefaultHidden { get; } = new HashSet<ContextMenuItemId>
    {
        ContextMenuItemId.MoveToRecycleBin, ContextMenuItemId.Fit, ContextMenuItemId.ZoomToLevel, ContextMenuItemId.ZoomSubmenu,
    };

    /// <summary>
    /// The set hidden by the three legacy flags (<see cref="AppSettings.ShowZoomMenuItems"/>, <see cref="AppSettings.ShowFolderMenuItems"/>,
    /// <see cref="AppSettings.ShowRecycleMenuItem"/>). A flag that is false hides every item it used to gate.
    /// </summary>
    public static IReadOnlySet<ContextMenuItemId> LegacyHidden(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hidden = new HashSet<ContextMenuItemId>();
        if (!settings.ShowZoomMenuItems) { hidden.Add(ContextMenuItemId.Fit); hidden.Add(ContextMenuItemId.ZoomToLevel); hidden.Add(ContextMenuItemId.ZoomSubmenu); }
        if (!settings.ShowFolderMenuItems) { hidden.Add(ContextMenuItemId.OpenFolder); hidden.Add(ContextMenuItemId.NextFolder); hidden.Add(ContextMenuItemId.PreviousFolder); }
        if (!settings.ShowRecycleMenuItem) hidden.Add(ContextMenuItemId.MoveToRecycleBin);
        return hidden;
    }

    /// <summary>
    /// What is hidden right now: the stored list when there is one (an empty list = show everything), otherwise the legacy flags
    /// (a config written before the list existed). <see cref="ContextMenuItemId.Settings"/> is never hidden.
    /// </summary>
    public static IReadOnlySet<ContextMenuItemId> EffectiveHidden(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.HiddenContextMenuItems is null) return LegacyHidden(settings);
        return ParseHidden(settings.HiddenContextMenuItems);
    }

    /// <summary>Known, unlocked ids from stored names; unknown/blank names and the locked Settings item are dropped.</summary>
    public static IReadOnlySet<ContextMenuItemId> ParseHidden(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var hidden = new HashSet<ContextMenuItemId>();
        foreach (var name in names)
        {
            if (name is null || !Enum.TryParse<ContextMenuItemId>(name, ignoreCase: false, out var id) || !Enum.IsDefined(id)) continue;
            if (Info(id).Locked) continue;
            hidden.Add(id);
        }
        return hidden;
    }

    /// <summary>Canonical stored form: enum names in menu order, no duplicates.</summary>
    public static List<string> ToStored(IEnumerable<ContextMenuItemId> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        var set = hidden.ToHashSet();
        return [.. Items.Where(i => !i.Locked && set.Contains(i.Id)).Select(i => i.Id.ToString())];
    }

    /// <summary>
    /// Stores <paramref name="hidden"/> in <paramref name="settings"/> and mirrors it into the three legacy flags (an older build
    /// reading the same file still behaves sensibly; a flag is "on" while any item it gates is shown).
    /// </summary>
    public static void ApplyHidden(AppSettings settings, IEnumerable<ContextMenuItemId> hidden)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var set = hidden.Where(id => !Info(id).Locked).ToHashSet();
        settings.HiddenContextMenuItems = ToStored(set);
        settings.ShowZoomMenuItems = !(set.Contains(ContextMenuItemId.Fit) && set.Contains(ContextMenuItemId.ZoomToLevel) && set.Contains(ContextMenuItemId.ZoomSubmenu));
        settings.ShowFolderMenuItems = !(set.Contains(ContextMenuItemId.OpenFolder) && set.Contains(ContextMenuItemId.NextFolder) && set.Contains(ContextMenuItemId.PreviousFolder));
        settings.ShowRecycleMenuItem = !set.Contains(ContextMenuItemId.MoveToRecycleBin);
    }

    /// <summary>
    /// The visibility rule for one menu level (<paramref name="parent"/> null = top level, else that submenu's children). An item
    /// shows when it is not hidden by the user (the locked Settings item always shows), its context allows it (needs a photo) and,
    /// for a submenu, at least one child shows. A separator sits only between two groups that both have something to show.
    /// </summary>
    public static ContextMenuLayout Compute(IReadOnlySet<ContextMenuItemId> hidden, ContextMenuContext context, ContextMenuItemId? parent = null)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(context);
        var entries = new List<ContextMenuEntry>();
        var previousGroup = 0;
        foreach (var group in Items.Where(i => i.Parent == parent).GroupBy(i => i.Group))
        {
            var visible = group.Where(i => ItemShows(i, hidden, context)).ToList();
            if (visible.Count == 0) continue;
            if (previousGroup != 0) entries.Add(new ContextMenuEntry(null, group.Key));
            entries.AddRange(visible.Select(i => new ContextMenuEntry(i.Id, group.Key)));
            previousGroup = group.Key;
        }
        return new ContextMenuLayout(entries);
    }

    private static bool ItemShows(ContextMenuItemInfo item, IReadOnlySet<ContextMenuItemId> hidden, ContextMenuContext context)
    {
        if (!item.Locked && hidden.Contains(item.Id)) return false;
        if (item.NeedsPhoto && !context.HasPhoto) return false;
        // A submenu with nothing left to show would be an empty arrow: collapse it too.
        if (Items.Any(i => i.Parent == item.Id)) return Items.Where(i => i.Parent == item.Id).Any(c => ItemShows(c, hidden, context));
        return true;
    }
}
