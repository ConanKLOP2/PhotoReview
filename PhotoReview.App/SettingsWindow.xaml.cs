using System.Windows;
using System.Windows.Input;
using System.Text.Json;

namespace PhotoReview.App;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Settings = new AppSettings
        {
            Folder2Name = current.Folder2Name,
            InitialViewMode = current.InitialViewMode,
            LoadingMode = AppSettings.NormalizeLoadingMode(current.LoadingMode),
            ImageSortMode = AppSettings.NormalizeImageSortMode(current.ImageSortMode),
            Actions = current.Actions.Select(action => new ReviewAction
            {
                Name = action.Name, Shortcut = action.Shortcut, Operation = action.Operation,
                Destination = action.Destination, Confirm = action.Confirm
            }).ToList(),
            Shortcuts = new ShortcutMappings
            {
                Next = current.Shortcuts.Next, Previous = current.Shortcuts.Previous,
                MoveToFolder2 = current.Shortcuts.MoveToFolder2, SendToRecycleBin = current.Shortcuts.SendToRecycleBin,
                Compare = current.Shortcuts.Compare, NextFolder = current.Shortcuts.NextFolder, PreviousFolder = current.Shortcuts.PreviousFolder,
                FirstImage = current.Shortcuts.FirstImage, ZoomIn = current.Shortcuts.ZoomIn, ZoomOut = current.Shortcuts.ZoomOut, ToggleFit = current.Shortcuts.ToggleFit
            }
        };
        foreach (var textBox in new[] { NextText, PreviousText, MoveText, RecycleText, CompareText, NextFolderText, PreviousFolderText, FirstImageText, ZoomInText, ZoomOutText, ToggleFitText })
            textBox.PreviewKeyDown += ShortcutText_PreviewKeyDown;
        LoadFields();
    }

    private void LoadFields()
    {
        Folder2Text.Text = Settings.Folder2Name;
        NextText.Text = Settings.Shortcuts.Next; PreviousText.Text = Settings.Shortcuts.Previous;
        MoveText.Text = Settings.Shortcuts.MoveToFolder2; RecycleText.Text = Settings.Shortcuts.SendToRecycleBin;
        CompareText.Text = Settings.Shortcuts.Compare; NextFolderText.Text = Settings.Shortcuts.NextFolder; PreviousFolderText.Text = Settings.Shortcuts.PreviousFolder;
        FirstImageText.Text = Settings.Shortcuts.FirstImage; ZoomInText.Text = Settings.Shortcuts.ZoomIn; ZoomOutText.Text = Settings.Shortcuts.ZoomOut; ToggleFitText.Text = Settings.Shortcuts.ToggleFit;
        ActionsText.Text = JsonSerializer.Serialize(Settings.Actions, new JsonSerializerOptions { WriteIndented = true });
        ViewModeCombo.SelectedIndex = Settings.InitialViewMode switch { "100%" => 1, "200%" => 2, "400%" => 3, _ => 0 };
        LoadingModeCombo.SelectedIndex = Settings.LoadingMode switch { "Preview" => 1, "Original" => 2, _ => 0 };
        SortModeCombo.SelectedIndex = Settings.ImageSortMode == "Name" ? 1 : 0;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        Settings.Folder2Name = "Loai-2"; Settings.InitialViewMode = "Fit"; Settings.LoadingMode = "Preview"; Settings.ImageSortMode = "PortraitFirst"; Settings.Shortcuts = ShortcutMappings.Default(); LoadFields();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = new[] { NextText.Text, PreviousText.Text, MoveText.Text, RecycleText.Text, CompareText.Text, NextFolderText.Text, PreviousFolderText.Text, FirstImageText.Text, ZoomInText.Text, ZoomOutText.Text, ToggleFitText.Text };
        if (string.IsNullOrWhiteSpace(Folder2Text.Text) || values.Any(v => !Enum.TryParse<Key>(v, true, out _)) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            System.Windows.MessageBox.Show(this, "Folder không được trống; các phím phải hợp lệ và không được trùng nhau.", "Cài đặt không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        Settings.Folder2Name = Folder2Text.Text.Trim();
        Settings.InitialViewMode = (ViewModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Fit";
        Settings.ImageSortMode = ((System.Windows.Controls.ComboBoxItem)SortModeCombo.SelectedItem)?.Tag?.ToString() ?? "PortraitFirst";
        var loadingItem = LoadingModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
        Settings.LoadingMode = loadingItem?.Tag?.ToString() ?? loadingItem?.Content?.ToString() ?? "Fast";
        Settings.Shortcuts.Next = NextText.Text.Trim(); Settings.Shortcuts.Previous = PreviousText.Text.Trim();
        Settings.Shortcuts.MoveToFolder2 = MoveText.Text.Trim(); Settings.Shortcuts.SendToRecycleBin = RecycleText.Text.Trim();
        Settings.Shortcuts.Compare = CompareText.Text.Trim(); Settings.Shortcuts.NextFolder = NextFolderText.Text.Trim(); Settings.Shortcuts.PreviousFolder = PreviousFolderText.Text.Trim();
        Settings.Shortcuts.FirstImage = FirstImageText.Text.Trim(); Settings.Shortcuts.ZoomIn = ZoomInText.Text.Trim(); Settings.Shortcuts.ZoomOut = ZoomOutText.Text.Trim(); Settings.Shortcuts.ToggleFit = ToggleFitText.Text.Trim();
        try
        {
            Settings.Actions = JsonSerializer.Deserialize<List<ReviewAction>>(ActionsText.Text) ?? [];
            if (Settings.Actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Shortcut) ||
                !Enum.TryParse<Key>(action.Shortcut, true, out _) ||
                !new[] { "Move", "Copy", "Recycle", "Delete" }.Contains(action.Operation, StringComparer.OrdinalIgnoreCase)))
                throw new JsonException("Action thiếu tên/phím tắt hoặc có Operation không hợp lệ.");
            if (Settings.Actions.GroupBy(action => action.Shortcut, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new JsonException("Các action không được trùng phím tắt.");
        }
        catch { System.Windows.MessageBox.Show(this, "Action profiles JSON không hợp lệ.", "Cài đặt không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        AppSettings.Save(Settings); DialogResult = true;
    }

    private static void ShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        textBox.Text = key.ToString();
        textBox.SelectAll();
        e.Handled = true;
    }

    private void EditActions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var actions = JsonSerializer.Deserialize<List<ReviewAction>>(ActionsText.Text) ?? ReviewAction.Defaults();
            var editor = new ActionProfilesWindow(actions) { Owner = this };
            if (editor.ShowDialog() == true) ActionsText.Text = JsonSerializer.Serialize(editor.Actions, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { System.Windows.MessageBox.Show(this, "Action profiles JSON hiện tại không hợp lệ.", "Không thể mở editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
