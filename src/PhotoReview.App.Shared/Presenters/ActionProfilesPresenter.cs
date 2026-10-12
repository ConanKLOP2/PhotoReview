using System.Text.Json;
using PhotoReview.App.Input;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Presenters;

/// <summary>The detail fields of the Action Profiles editor as the view shows them.</summary>
/// <param name="OperationIndex">0 Move, 1 Copy, 2 Recycle (the combo item order; a legacy "Delete" is read as Recycle by the JSON alias).</param>
internal sealed record ActionProfileForm(string Name, string Shortcut, int OperationIndex, string Destination, bool Confirm)
{
    /// <summary>The empty detail fields shown when the list is empty.</summary>
    public static ActionProfileForm Empty { get; } = new(string.Empty, string.Empty, 0, string.Empty, false);
}

/// <summary>The result of the Apply button.</summary>
/// <param name="Accepted">True: the list is valid and the dialog may close with OK.</param>
/// <param name="Message">The localized reason when not accepted.</param>
internal sealed record ActionProfilesApplyResult(bool Accepted, string? Message);

/// <summary>
/// WP-35 G0: the model behind the Action Profiles editor (the list being edited, the open row, add/remove, import/export, the
/// Apply checks). No WPF types. The view owns the list box and the detail fields; it passes the fields in whenever the
/// open row may be written back (<see cref="SaveCurrent"/>) and gets the fields to show from <see cref="Open"/>.
/// </summary>
internal sealed class ActionProfilesPresenter
{
    private ReviewAction? _loaded;

    public ActionProfilesPresenter(IEnumerable<ReviewAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        Actions = actions.Select(Clone).ToList();
    }

    /// <summary>The list under edit (a copy of what was passed in).</summary>
    public List<ReviewAction> Actions { get; }

    // ---- Selection / detail fields ----

    /// <summary>The row the detail fields currently belong to (test seam).</summary>
    internal ReviewAction? Loaded => _loaded;

    /// <summary>
    /// The list selection changed to <paramref name="selected"/> (the view has already called <see cref="SaveCurrent"/> for the
    /// previously open row, before anything else, exactly as the old handler did). Returns the fields to show; null = nothing
    /// selected: the view leaves its fields untouched.
    /// </summary>
    public ActionProfileForm? Open(ReviewAction? selected)
    {
        _loaded = selected;
        if (_loaded is null) return null;
        return new ActionProfileForm(_loaded.Name, _loaded.Shortcut, OperationToIndex(_loaded.Operation), _loaded.Destination, _loaded.Confirm);
    }
    /// <summary>Writes the detail fields into the open row (trimmed, shortcut canonicalized). False when no row is open.</summary>
    public bool SaveCurrent(ActionProfileForm fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (_loaded is null || !Actions.Contains(_loaded)) return false;
        _loaded.Name = fields.Name.Trim();
        _loaded.Shortcut = ShortcutKeyCanonical.Canonicalize(fields.Shortcut);
        _loaded.Destination = fields.Destination.Trim();
        _loaded.Confirm = fields.Confirm;
        _loaded.Operation = IndexToOperation(fields.OperationIndex);
        return true;
    }

    internal static int OperationToIndex(FileOperationType operation) => operation switch
    {
        FileOperationType.Copy => 1,
        FileOperationType.Recycle => 2,
        _ => 0,
    };

    internal static FileOperationType IndexToOperation(int index) => index switch
    {
        1 => FileOperationType.Copy,
        2 => FileOperationType.Recycle,
        _ => FileOperationType.Move,
    };

    // ---- Add / remove / replace ----

    /// <summary>The Add button (after the view called <see cref="SaveCurrent"/>): appends a new action with the next free key and returns it for selection.</summary>
    public ReviewAction Add()
    {
        var action = new ReviewAction { Name = Tr.ActionProfilesNewActionName, Shortcut = NextFreeShortcut(Actions), Operation = FileOperationType.Move, Destination = "Output" };
        Actions.Add(action);
        return action;
    }

    /// <summary>
    /// The Remove button. Returns the index to select next, or -1 when the list is now empty (the open row is dropped and the
    /// view clears its fields).
    /// </summary>
    public int Remove(ReviewAction action, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(action);
        Actions.Remove(action);
        if (Actions.Count > 0) return Math.Min(selectedIndex, Actions.Count - 1);
        _loaded = null;
        return -1;
    }

    /// <summary>The Import button after a list was read: replaces the whole list.</summary>
    public void ReplaceAll(IEnumerable<ReviewAction> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var items = imported.ToList();
        Actions.Clear();
        Actions.AddRange(items);
    }

    // ---- Apply ----

    /// <summary>The Apply button (after the view called <see cref="SaveCurrent"/>): checks the list and the destinations.</summary>
    public ActionProfilesApplyResult Apply()
    {
        if (HasInvalidActions(Actions)) return new(false, Tr.DialogActionProfilesInvalidMessage);
        if (FindDestinationProblem(Actions) is { } problem) return new(false, problem);
        return new(true, null);
    }

    // ---- Statics (shared with the Settings dialog's raw JSON box) ----

    /// <summary>
    /// Reads an exported action list. A malformed/empty file gives the "invalid file" message; an I/O or access error is
    /// logged and gives its own reason (both used to collapse into one bare catch that hid the cause).
    /// </summary>
    internal static List<ReviewAction>? TryImport(string path, out string error)
    {
        try
        {
            var imported = ActionListJson.DeserializeOrNull(File.ReadAllText(path));
            if (imported is null || imported.Count == 0 || imported.Any(action => action is null)) throw new JsonException();
            error = "";
            return imported.Select(Clone).ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
        {
            error = Tr.DialogImportActionsInvalidMessage;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            AppLog.Error($"Action import failed: {path}", ex);
            error = Tr.DialogImportActionsFailedMessage(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// R7-9: writes the actions as JSON; the localized reason on an I/O or access error (it used to escape to
    /// DispatcherUnhandledException, which marks it handled, so the export failed silently), or null on success.
    /// </summary>
    internal static string? TryExport(string path, IEnumerable<ReviewAction> actions)
    {
        try
        {
            File.WriteAllText(path, ActionListJson.SerializeForExport(actions));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            AppLog.Error($"Action export failed: {path}", ex);
            return Tr.DialogExportActionsFailedMessage(ex.Message);
        }
    }

    /// <summary>
    /// True when the list is empty, a name is blank, an operation is undefined, a shortcut cannot be parsed, or two
    /// shortcuts resolve to the same <see cref="KeyId"/> (aliases such as Return/Enter parse to one key and would leave the
    /// second action unreachable, so comparing the strings is not enough).
    /// </summary>
    internal static bool HasInvalidActions(IReadOnlyList<ReviewAction> actions)
    {
        if (actions.Count == 0) return true;
        var seen = new HashSet<KeyId>();
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Name) || !Enum.IsDefined(action.Operation)) return true;
            if (!ShortcutKeyName.TryParse(action.Shortcut, out var key) || !seen.Add(key)) return true;
        }

        return false;
    }

    /// <summary>The first F6..F12 key no action uses yet (by parsed key); "F6" when all are taken so Apply reports it.</summary>
    internal static string NextFreeShortcut(IEnumerable<ReviewAction> actions)
    {
        var used = new HashSet<KeyId>();
        foreach (var action in actions)
            if (ShortcutKeyName.TryParse(action.Shortcut, out var key)) used.Add(key);
        for (var k = KeyId.F6; k <= KeyId.F12; k++)
            if (!used.Contains(k)) return k.ToString();
        return nameof(KeyId.F6);
    }

    /// <summary>
    /// CORE-03 / Q-R2: the localized reason the first Move/Copy action has an unusable destination, or null when all are fine.
    /// Recycle actions have no destination, so theirs is ignored.
    /// </summary>
    internal static string? FindDestinationProblem(IEnumerable<ReviewAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.Operation == FileOperationType.Recycle) continue;
            switch (ActionDestinationPolicy.Validate(action.Destination))
            {
                case ActionDestinationCheck.Ok: break;
                case ActionDestinationCheck.EscapesSourceFolder: return Tr.ActionProfilesErrorDestinationEscapes(action.Name);
                default: return Tr.ActionProfilesErrorDestinationInvalid(action.Name);
            }
        }

        return null;
    }

    internal static ReviewAction Clone(ReviewAction action) =>
        new() { Name = action.Name, Shortcut = action.Shortcut, Operation = action.Operation, Destination = action.Destination, Confirm = action.Confirm };
}
