using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

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

    private void Add_Click(object sender, RoutedEventArgs e) { SaveCurrent(); var action = new ReviewAction { Name = Tr.ActionProfilesNewActionName, Shortcut = "F6", Operation = FileOperationType.Move, Destination = "Output" }; Actions.Add(action); ActionList.Items.Refresh(); ActionList.SelectedItem = action; }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (ActionList.SelectedItem is ReviewAction action) { Actions.Remove(action); ActionList.Items.Refresh(); if (Actions.Count > 0) ActionList.SelectedIndex = 0; } }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Tr.DialogFileFilterJsonOrAll };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = JsonSerializer.Deserialize<List<ReviewAction>>(System.IO.File.ReadAllText(dialog.FileName));
            if (imported is null || imported.Count == 0) throw new JsonException();
            Actions.Clear(); Actions.AddRange(imported.Select(Clone)); ActionList.Items.Refresh(); ActionList.SelectedIndex = 0;
        }
        catch { System.Windows.MessageBox.Show(this, Tr.DialogImportActionsInvalidMessage, Tr.DialogImportActionsFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning); }
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
        if (Actions.Count == 0 || Actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || !ShortcutKeyName.TryParse(action.Shortcut, out _) || !Enum.IsDefined(action.Operation)) || Actions.GroupBy(action => action.Shortcut, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        { System.Windows.MessageBox.Show(this, Tr.DialogActionProfilesInvalidMessage, Tr.DialogActionProfilesInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (FindDestinationProblem(Actions) is { } problem)
        { System.Windows.MessageBox.Show(this, problem, Tr.DialogActionProfilesInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
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
