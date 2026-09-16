using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace PhotoReview.App;

public partial class ActionProfilesWindow : Window
{
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
        OperationCombo.SelectedIndex = _loaded.Operation.ToUpperInvariant() switch { "COPY" => 1, "RECYCLE" => 2, "DELETE" => 3, _ => 0 };
    }

    private void SaveCurrent()
    {
        if (_loaded is null || !Actions.Contains(_loaded)) return;
        _loaded.Name = NameText.Text.Trim(); _loaded.Shortcut = ShortcutText.Text.Trim(); _loaded.Destination = DestinationText.Text.Trim(); _loaded.Confirm = ConfirmCheck.IsChecked == true;
        _loaded.Operation = (OperationCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Move";
        ActionList.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e) { SaveCurrent(); var action = new ReviewAction { Name = "Action mới", Shortcut = "F6", Destination = "Output" }; Actions.Add(action); ActionList.Items.Refresh(); ActionList.SelectedItem = action; }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (ActionList.SelectedItem is ReviewAction action) { Actions.Remove(action); ActionList.Items.Refresh(); if (Actions.Count > 0) ActionList.SelectedIndex = 0; } }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog { Filter = "JSON (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var imported = JsonSerializer.Deserialize<List<ReviewAction>>(System.IO.File.ReadAllText(dialog.FileName));
            if (imported is null || imported.Count == 0) throw new JsonException();
            Actions.Clear(); Actions.AddRange(imported.Select(Clone)); ActionList.Items.Refresh(); ActionList.SelectedIndex = 0;
        }
        catch { System.Windows.MessageBox.Show(this, "File action profile không hợp lệ.", "Import thất bại", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        using var dialog = new Forms.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "photoreview-actions.json" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) System.IO.File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Actions, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        if (Actions.Count == 0 || Actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || !Enum.TryParse<Key>(action.Shortcut, true, out _) || string.IsNullOrWhiteSpace(action.Operation)) || Actions.GroupBy(action => action.Shortcut, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        { System.Windows.MessageBox.Show(this, "Action phải có tên, phím hợp lệ và không được trùng phím.", "Cấu hình không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }

    private static ReviewAction Clone(ReviewAction action) => new() { Name = action.Name, Shortcut = action.Shortcut, Operation = action.Operation, Destination = action.Destination, Confirm = action.Confirm };
}
