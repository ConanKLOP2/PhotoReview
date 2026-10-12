using System.Windows;
using System.Windows.Controls;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

/// <summary>The Action Profiles editor, WPF view only (WP-35 G0): the list model and every check live in <see cref="ActionProfilesPresenter"/>.</summary>
public partial class ActionProfilesWindow : Window
{
    private readonly ActionProfilesPresenter _presenter;

    public List<ReviewAction> Actions => _presenter.Actions;

    public ActionProfilesWindow(IEnumerable<ReviewAction> actions)
    {
        _presenter = new ActionProfilesPresenter(actions);
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        ActionList.ItemsSource = Actions;
        if (Actions.Count > 0) ActionList.SelectedIndex = 0;
    }

    private ActionProfileForm CurrentFields() =>
        new(NameText.Text, ShortcutText.Text, OperationCombo.SelectedIndex, DestinationText.Text, ConfirmCheck.IsChecked == true);

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveCurrent(); // the previously open row first, as before
        var fields = _presenter.Open(ActionList.SelectedItem as ReviewAction);
        if (fields is null) return;
        NameText.Text = fields.Name; ShortcutText.Text = fields.Shortcut; DestinationText.Text = fields.Destination; ConfirmCheck.IsChecked = fields.Confirm;
        OperationCombo.SelectedIndex = fields.OperationIndex;
    }

    /// <summary>Writes the detail fields into the open row and refreshes the list captions.</summary>
    private void SaveCurrent()
    {
        if (_presenter.SaveCurrent(CurrentFields())) ActionList.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var action = _presenter.Add();
        ActionList.Items.Refresh();
        ActionList.SelectedItem = action;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not ReviewAction action) return;
        var next = _presenter.Remove(action, ActionList.SelectedIndex);
        ActionList.Items.Refresh();
        if (next >= 0)
        {
            ActionList.SelectedIndex = next;
            return;
        }

        // Nothing left to edit: drop the removed action's values from the detail fields.
        NameText.Text = ShortcutText.Text = DestinationText.Text = "";
        ConfirmCheck.IsChecked = false;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Tr.DialogFileFilterJsonOrAll };
        if (dialog.ShowDialog(this) != true) return;
        var imported = ActionProfilesPresenter.TryImport(dialog.FileName, out var error);
        if (imported is null)
        {
            System.Windows.MessageBox.Show(this, error, Tr.DialogImportActionsFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _presenter.ReplaceAll(imported); ActionList.Items.Refresh(); ActionList.SelectedIndex = 0;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = Tr.DialogFileFilterJson, FileName = "photoreview-actions.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (ActionProfilesPresenter.TryExport(dialog.FileName, Actions) is { } error)
            System.Windows.MessageBox.Show(this, error, Tr.DialogExportActionsFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var result = _presenter.Apply();
        if (!result.Accepted)
        {
            System.Windows.MessageBox.Show(this, result.Message, Tr.DialogActionProfilesInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
