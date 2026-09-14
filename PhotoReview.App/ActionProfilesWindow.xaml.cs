using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoReview.App;

public partial class ActionProfilesWindow : Window
{
    public List<ReviewAction> Actions { get; }

    public ActionProfilesWindow(IEnumerable<ReviewAction> actions)
    {
        InitializeComponent();
        Actions = actions.Select(Clone).ToList();
        ActionList.ItemsSource = Actions;
        if (Actions.Count > 0) ActionList.SelectedIndex = 0;
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionList.SelectedItem is not ReviewAction action) return;
        NameText.Text = action.Name; ShortcutText.Text = action.Shortcut; DestinationText.Text = action.Destination; ConfirmCheck.IsChecked = action.Confirm;
        OperationCombo.SelectedIndex = action.Operation.ToUpperInvariant() switch { "COPY" => 1, "RECYCLE" => 2, "DELETE" => 3, _ => 0 };
    }

    private void SaveCurrent()
    {
        if (ActionList.SelectedItem is not ReviewAction action) return;
        action.Name = NameText.Text.Trim(); action.Shortcut = ShortcutText.Text.Trim(); action.Destination = DestinationText.Text.Trim(); action.Confirm = ConfirmCheck.IsChecked == true;
        action.Operation = (OperationCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Move";
        ActionList.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e) { SaveCurrent(); var action = new ReviewAction { Name = "Action mới", Shortcut = "F6", Destination = "Output" }; Actions.Add(action); ActionList.Items.Refresh(); ActionList.SelectedItem = action; }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (ActionList.SelectedItem is ReviewAction action) { Actions.Remove(action); ActionList.Items.Refresh(); if (Actions.Count > 0) ActionList.SelectedIndex = 0; } }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        if (Actions.Count == 0 || Actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || !Enum.TryParse<Key>(action.Shortcut, true, out _) || string.IsNullOrWhiteSpace(action.Operation)) || Actions.GroupBy(action => action.Shortcut, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        { System.Windows.MessageBox.Show(this, "Action phải có tên, phím hợp lệ và không được trùng phím.", "Cấu hình không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }

    private static ReviewAction Clone(ReviewAction action) => new() { Name = action.Name, Shortcut = action.Shortcut, Operation = action.Operation, Destination = action.Destination, Confirm = action.Confirm };
}
