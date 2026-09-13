using System.Windows;
using System.Windows.Input;

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
            LoadingMode = current.LoadingMode is "Preview" ? "Preview" : "Fast",
            Shortcuts = new ShortcutMappings
            {
                Next = current.Shortcuts.Next, Previous = current.Shortcuts.Previous,
                MoveToFolder2 = current.Shortcuts.MoveToFolder2, SendToRecycleBin = current.Shortcuts.SendToRecycleBin
            }
        };
        LoadFields();
    }

    private void LoadFields()
    {
        Folder2Text.Text = Settings.Folder2Name;
        NextText.Text = Settings.Shortcuts.Next; PreviousText.Text = Settings.Shortcuts.Previous;
        MoveText.Text = Settings.Shortcuts.MoveToFolder2; RecycleText.Text = Settings.Shortcuts.SendToRecycleBin;
        ViewModeCombo.SelectedIndex = Settings.InitialViewMode switch { "100%" => 1, "200%" => 2, "400%" => 3, _ => 0 };
        LoadingModeCombo.SelectedIndex = Settings.LoadingMode == "Preview" ? 1 : 0;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        Settings.Folder2Name = "Loai-2"; Settings.InitialViewMode = "Fit"; Settings.LoadingMode = "Preview"; Settings.Shortcuts = ShortcutMappings.Default(); LoadFields();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = new[] { NextText.Text, PreviousText.Text, MoveText.Text, RecycleText.Text };
        if (string.IsNullOrWhiteSpace(Folder2Text.Text) || values.Any(v => !Enum.TryParse<Key>(v, true, out _)) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            System.Windows.MessageBox.Show(this, "Folder không được trống; các phím phải hợp lệ và không được trùng nhau.", "Cài đặt không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        Settings.Folder2Name = Folder2Text.Text.Trim();
        Settings.InitialViewMode = (ViewModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Fit";
        Settings.LoadingMode = (LoadingModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Fast";
        Settings.Shortcuts.Next = NextText.Text.Trim(); Settings.Shortcuts.Previous = PreviousText.Text.Trim();
        Settings.Shortcuts.MoveToFolder2 = MoveText.Text.Trim(); Settings.Shortcuts.SendToRecycleBin = RecycleText.Text.Trim();
        AppSettings.Save(Settings); DialogResult = true;
    }
}
