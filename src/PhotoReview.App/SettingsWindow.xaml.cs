using System.Windows;
using System.Windows.Input;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetName().Version?.ToString(3) ?? "unknown";
        var build = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = string.IsNullOrWhiteSpace(build) || build == version ? $"Phiên bản {version}" : $"Phiên bản {version} · {build}";
        Settings = new AppSettings
        {
            Folder2Name = current.Folder2Name,
            InitialViewMode = current.InitialViewMode,
            LoadingMode = current.LoadingMode,
            ImageSortMode = current.ImageSortMode,
            CompareHashEnabled = current.CompareHashEnabled,
            CompareSizeEnabled = current.CompareSizeEnabled,
            LoggingEnabled = current.LoggingEnabled,
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
                FirstImage = current.Shortcuts.FirstImage, ZoomIn = current.Shortcuts.ZoomIn, ZoomOut = current.Shortcuts.ZoomOut, ToggleFit = current.Shortcuts.ToggleFit,
                Skip = current.Shortcuts.Skip, Undo = current.Shortcuts.Undo, Fullscreen = current.Shortcuts.Fullscreen
            }
        };
        foreach (var textBox in new[] { NextText, PreviousText, RecycleText, CompareText, NextFolderText, PreviousFolderText, FirstImageText, ZoomInText, ZoomOutText, ToggleFitText, SkipText, UndoText, FullscreenText })
            textBox.PreviewKeyDown += ShortcutText_PreviewKeyDown;
        LoadFields();
    }

    private void OpenLogLocation_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(AppLog.FilePath)!;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppLog.FilePath}\"") { UseShellExecute = true });
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SettingsScrollViewer.ScrollToHome();
        FocusManager.SetFocusedElement(this, null);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            SettingsScrollViewer.ScrollToVerticalOffset(0);
            Keyboard.ClearFocus();
        });
    }

    private void LoadFields()
    {
        Folder2Text.Text = Settings.Folder2Name;
        NextText.Text = Settings.Shortcuts.Next; PreviousText.Text = Settings.Shortcuts.Previous;
        RecycleText.Text = Settings.Shortcuts.SendToRecycleBin;
        CompareText.Text = Settings.Shortcuts.Compare; NextFolderText.Text = Settings.Shortcuts.NextFolder; PreviousFolderText.Text = Settings.Shortcuts.PreviousFolder;
        FirstImageText.Text = Settings.Shortcuts.FirstImage; ZoomInText.Text = Settings.Shortcuts.ZoomIn; ZoomOutText.Text = Settings.Shortcuts.ZoomOut; ToggleFitText.Text = Settings.Shortcuts.ToggleFit; SkipText.Text = Settings.Shortcuts.Skip; UndoText.Text = Settings.Shortcuts.Undo; FullscreenText.Text = Settings.Shortcuts.Fullscreen;
        ActionsText.Text = JsonSerializer.Serialize(Settings.Actions, new JsonSerializerOptions { WriteIndented = true });
        ViewModeCombo.SelectedIndex = Settings.InitialViewMode switch { InitialViewMode.Percent100 => 1, InitialViewMode.Percent200 => 2, InitialViewMode.Percent400 => 3, _ => 0 };
        LoadingModeCombo.SelectedIndex = Settings.LoadingMode switch { LoadingMode.Preview => 1, LoadingMode.Original => 2, _ => 0 };
        SortModeCombo.SelectedIndex = Settings.ImageSortMode switch { ImageSortMode.SizeAscending => 1, ImageSortMode.SizeDescending => 2, _ => 0 };
        CompareHashCheck.IsChecked = Settings.CompareHashEnabled;
        CompareSizeCheck.IsChecked = Settings.CompareSizeEnabled;
        LoggingCheck.IsChecked = Settings.LoggingEnabled;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        Settings.LoggingEnabled = false;
        Settings.Folder2Name = "Loai-2"; Settings.InitialViewMode = InitialViewMode.Fit; Settings.LoadingMode = LoadingMode.Preview; Settings.ImageSortMode = ImageSortMode.Name; Settings.CompareHashEnabled = true; Settings.CompareSizeEnabled = true; Settings.Shortcuts = ShortcutMappings.Default(); LoadFields();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Settings save requested");
        var values = new[] { NextText.Text, PreviousText.Text, RecycleText.Text, CompareText.Text, NextFolderText.Text, PreviousFolderText.Text, FirstImageText.Text, ZoomInText.Text, ZoomOutText.Text, ToggleFitText.Text, SkipText.Text, UndoText.Text, FullscreenText.Text };
        if (string.IsNullOrWhiteSpace(Folder2Text.Text) || values.Any(v => !Enum.TryParse<Key>(v, true, out _)) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            System.Windows.MessageBox.Show(this, "Folder không được trống; các phím phải hợp lệ và không được trùng nhau.", "Cài đặt không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        Settings.Folder2Name = Folder2Text.Text.Trim();
        Settings.InitialViewMode = ViewModeCombo.SelectedIndex switch { 1 => InitialViewMode.Percent100, 2 => InitialViewMode.Percent200, 3 => InitialViewMode.Percent400, _ => InitialViewMode.Fit };
        Settings.ImageSortMode = SortModeCombo.SelectedIndex switch { 1 => ImageSortMode.SizeAscending, 2 => ImageSortMode.SizeDescending, _ => ImageSortMode.Name };
        Settings.LoadingMode = LoadingModeCombo.SelectedIndex switch { 1 => LoadingMode.Preview, 2 => LoadingMode.Original, _ => LoadingMode.Fast };
        Settings.CompareHashEnabled = CompareHashCheck.IsChecked == true;
        Settings.CompareSizeEnabled = CompareSizeCheck.IsChecked == true;
        Settings.LoggingEnabled = LoggingCheck.IsChecked == true;
        Settings.Shortcuts.Next = NextText.Text.Trim(); Settings.Shortcuts.Previous = PreviousText.Text.Trim();
        Settings.Shortcuts.SendToRecycleBin = RecycleText.Text.Trim();
        Settings.Shortcuts.Compare = CompareText.Text.Trim(); Settings.Shortcuts.NextFolder = NextFolderText.Text.Trim(); Settings.Shortcuts.PreviousFolder = PreviousFolderText.Text.Trim();
        Settings.Shortcuts.FirstImage = FirstImageText.Text.Trim(); Settings.Shortcuts.ZoomIn = ZoomInText.Text.Trim(); Settings.Shortcuts.ZoomOut = ZoomOutText.Text.Trim(); Settings.Shortcuts.ToggleFit = ToggleFitText.Text.Trim(); Settings.Shortcuts.Skip = SkipText.Text.Trim(); Settings.Shortcuts.Undo = UndoText.Text.Trim(); Settings.Shortcuts.Fullscreen = FullscreenText.Text.Trim();
        try
        {
            Settings.Actions = JsonSerializer.Deserialize<List<ReviewAction>>(ActionsText.Text) ?? [];
            if (Settings.Actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Shortcut) ||
                !Enum.TryParse<Key>(action.Shortcut, true, out _) ||
                !Enum.IsDefined(action.Operation)))
                throw new JsonException("Action thiếu tên/phím tắt hoặc có Operation không hợp lệ.");
            if (Settings.Actions.GroupBy(action => action.Shortcut, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new JsonException("Các action không được trùng phím tắt.");
        }
        catch { System.Windows.MessageBox.Show(this, "Action profiles JSON không hợp lệ.", "Cài đặt không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var shortcutError = AppSettings.ValidateShortcuts(Settings);
        if (shortcutError is not null) { System.Windows.MessageBox.Show(this, shortcutError, "Cài đặt không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        AppSettings.Save(Settings); AppLog.Info("Settings saved"); DialogResult = true;
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
