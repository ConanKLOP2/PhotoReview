using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.App.Services;

namespace PhotoReview.App;

public partial class ActionProfilesWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public List<ReviewAction> Actions { get; }
    private ReviewAction? _loaded;

    public ActionProfilesWindow(IEnumerable<ReviewAction> actions)
    {
        InitializeComponent();
        Actions = actions.Select(Clone).ToList();
        ActionList.ItemsSource = Actions;
        if (Actions.Count > 0) ActionList.SelectedIndex = 0;
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded is not null && Actions.Contains(_loaded)) SaveCurrent();
        _loaded = ActionList.SelectedItem as ReviewAction;
        if (_loaded is null) return;
        NameText.Text = _loaded.Name; ShortcutText.Text = _loaded.Shortcut; DestinationText.Text = _loaded.Destination; ConfirmCheck.IsChecked = _loaded.Confirm;
        OperationCombo.SelectedIndex = _loaded.Operation switch
        {
            FileOperationType.Copy => 1,
            FileOperationType.Recycle => 2,
            _ => 0
        };
    }

    private void SaveCurrent()
    {
        if (_loaded is null || !Actions.Contains(_loaded)) return;
        _loaded.Name = NameText.Text.Trim(); _loaded.Shortcut = ShortcutText.Text.Trim(); _loaded.Destination = DestinationText.Text.Trim(); _loaded.Confirm = ConfirmCheck.IsChecked == true;
        _loaded.Operation = OperationCombo.SelectedIndex switch
        {
            1 => FileOperationType.Copy,
            2 or 3 => FileOperationType.Recycle,
            _ => FileOperationType.Move
        };
        ActionList.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e) { SaveCurrent(); var action = new ReviewAction { Name = Tr.ActionProfilesNewActionName, Shortcut = NextFreeShortcut(Actions), Operation = FileOperationType.Move, Destination = "Output" }; Actions.Add(action); ActionList.Items.Refresh(); ActionList.SelectedItem = action; }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not ReviewAction action) return;
        var index = ActionList.SelectedIndex;
        Actions.Remove(action);
        ActionList.Items.Refresh();
        if (Actions.Count > 0)
        {
            ActionList.SelectedIndex = Math.Min(index, Actions.Count - 1);
            return;
        }

        // Nothing left to edit: drop the removed action's values from the detail fields.
        _loaded = null;
        NameText.Text = ShortcutText.Text = DestinationText.Text = "";
        ConfirmCheck.IsChecked = false;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Tr.DialogFileFilterJsonOrAll };
        if (dialog.ShowDialog(this) != true) return;
        var imported = TryImport(dialog.FileName, out var error);
        if (imported is null)
        {
            System.Windows.MessageBox.Show(this, error, Tr.DialogImportActionsFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Actions.Clear(); Actions.AddRange(imported); ActionList.Items.Refresh(); ActionList.SelectedIndex = 0;
    }

    /// <summary>
    /// Reads an exported action list. A malformed/empty file gives the "invalid file" message; an I/O or access error is
    /// logged and gives its own reason (both used to collapse into one bare catch that hid the cause).
    /// </summary>
    internal static List<ReviewAction>? TryImport(string path, out string error)
    {
        try
        {
            var imported = JsonSerializer.Deserialize<List<ReviewAction>>(System.IO.File.ReadAllText(path));
            if (imported is null || imported.Count == 0 || imported.Any(action => action is null)) throw new JsonException();
            error = "";
            return imported.Select(Clone).ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            error = Tr.DialogImportActionsInvalidMessage;
            return null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Error($"Action import failed: {path}", ex);
            error = Tr.DialogImportActionsFailedMessage(ex.Message);
            return null;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = Tr.DialogFileFilterJson, FileName = "photoreview-actions.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (TryExport(dialog.FileName, Actions) is { } error)
            System.Windows.MessageBox.Show(this, error, Tr.DialogExportActionsFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// R7-9: writes the actions as JSON; the localized reason on an I/O or access error (it used to escape to
    /// DispatcherUnhandledException, which marks it handled, so the export failed silently), or null on success.
    /// </summary>
    internal static string? TryExport(string path, IEnumerable<ReviewAction> actions)
    {
        try
        {
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(actions, JsonOptions));
            return null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Error($"Action export failed: {path}", ex);
            return Tr.DialogExportActionsFailedMessage(ex.Message);
        }
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        if (HasInvalidActions(Actions))
        { System.Windows.MessageBox.Show(this, Tr.DialogActionProfilesInvalidMessage, Tr.DialogActionProfilesInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (FindDestinationProblem(Actions) is { } problem)
        { System.Windows.MessageBox.Show(this, problem, Tr.DialogActionProfilesInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }

    /// <summary>
    /// True when the list is empty, a name is blank, an operation is undefined, a shortcut cannot be parsed, or two
    /// shortcuts resolve to the same <see cref="Key"/> (aliases such as Return/Enter parse to one key and would leave the
    /// second action unreachable, so comparing the strings is not enough).
    /// </summary>
    internal static bool HasInvalidActions(IReadOnlyList<ReviewAction> actions)
    {
        if (actions.Count == 0) return true;
        var seen = new HashSet<Key>();
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
        var used = new HashSet<Key>();
        foreach (var action in actions)
            if (ShortcutKeyName.TryParse(action.Shortcut, out var key)) used.Add(key);
        for (var k = Key.F6; k <= Key.F12; k++)
            if (!used.Contains(k)) return k.ToString();
        return nameof(Key.F6);
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

    private static ReviewAction Clone(ReviewAction action) => new() { Name = action.Name, Shortcut = action.Shortcut, Operation = action.Operation, Destination = action.Destination, Confirm = action.Confirm };
}
