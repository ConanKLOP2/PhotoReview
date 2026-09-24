using System.Windows;
using System.Windows.Input;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using PhotoReview.App.Localization;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

using PhotoReview.Core.Settings;

namespace PhotoReview.App;

public partial class SettingsWindow : Window
{
    // Relaxed escaping: the JSON is only shown in a local text box, and the default encoder turns every
    // non-ASCII character into an escape sequence (Vietnamese action names became unreadable).
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SettingsStore? _store;
    private readonly LocalizationService? _localization;
    public AppSettings Settings { get; }

    public SettingsWindow(SettingsStore store, IImageDecoderFactory? decoderFactory = null, LocalizationService? localization = null)
        : this(store.Current, decoderFactory, localization)
    {
        _store = store;
    }

    public SettingsWindow(AppSettings current, IImageDecoderFactory? decoderFactory = null, LocalizationService? localization = null)
    {
        InitializeComponent();
        _localization = localization;
        VersionText.Text = BuildInfo.Describe(typeof(SettingsWindow).Assembly);
        Settings = new AppSettings
        {
            UiLanguage = current.UiLanguage,
            InitialViewMode = current.InitialViewMode,
            LoadingMode = current.LoadingMode,
            ImageSortMode = current.ImageSortMode,
            ScalingQuality = current.ScalingQuality,
            DecoderBackend = current.DecoderBackend,
            CompareHashEnabled = current.CompareHashEnabled,
            CompareSizeEnabled = current.CompareSizeEnabled,
            LoggingEnabled = current.LoggingEnabled,
            JournalDurability = current.JournalDurability,
            ImageCacheCapacityBytes = current.ImageCacheCapacityBytes,
            MemoryReserveBytes = current.MemoryReserveBytes,
            PreloadWorkerCount = current.PreloadWorkerCount,
            PreloadMemoryLoadLimit = current.PreloadMemoryLoadLimit,
            PreviewDiskCacheCapacityBytes = current.PreviewDiskCacheCapacityBytes,
            UseSourceBytesCache = current.UseSourceBytesCache,
            SourceBytesCapacityBytes = current.SourceBytesCapacityBytes,
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
        ApplyDecoderAvailability(decoderFactory);
        LoadLanguages();
    }

    private void ApplyDecoderAvailability(IImageDecoderFactory? decoderFactory)
    {
        if (decoderFactory is not null && !decoderFactory.IsRegistered(DecoderBackend.TurboJpeg))
        {
            TurboJpegOption.IsEnabled = false;
            TurboJpegOption.ToolTip = Tr.SettingsDecoderBackendTurboJpegUnavailable;
        }
    }

    // ---- I18N L08: language picker, languages folder, reload, export ----

    /// <summary>Fills the picker from the shipped and user folders and selects <see cref="AppSettings.UiLanguage"/>.</summary>
    private void LoadLanguages()
    {
        if (_localization is null)
        {
            // No LocalizationService (a window built without DI): there is nothing to switch, so hide the group.
            LanguageGroup.Visibility = Visibility.Collapsed;
            return;
        }
        var selected = LanguageCombo.SelectedItem is LanguageOption option ? option.Code : Settings.UiLanguage;
        var options = LanguageOptions.Build(_localization.DiscoverLanguages());
        LanguageCombo.ItemsSource = options;
        LanguageCombo.SelectedIndex = LanguageOptions.IndexOf(options, selected);
    }

    private void OpenLanguagesFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_localization is null) return;
        Directory.CreateDirectory(_localization.UserLanguagesDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_localization.UserLanguagesDir}\"") { UseShellExecute = true });
    }

    private void ReloadTranslations_Click(object sender, RoutedEventArgs e)
    {
        if (_localization is null) return;
        _localization.Reload();
        LoadLanguages(); // a translator may have added a language file
        AppLog.Info("Translations reloaded");
    }

    private void ExportTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (_localization is null) return;
        var language = LanguageOptions.ToSetting(LanguageCombo.SelectedItem as LanguageOption, Settings.UiLanguage);
        string path;
        try
        {
            path = _localization.ExportTodo(language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogExportTranslationFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppLog.Info("Translation export written");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
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
        NextText.Text = Settings.Shortcuts.Next; PreviousText.Text = Settings.Shortcuts.Previous;
        RecycleText.Text = Settings.Shortcuts.SendToRecycleBin;
        CompareText.Text = Settings.Shortcuts.Compare; NextFolderText.Text = Settings.Shortcuts.NextFolder; PreviousFolderText.Text = Settings.Shortcuts.PreviousFolder;
        FirstImageText.Text = Settings.Shortcuts.FirstImage; ZoomInText.Text = Settings.Shortcuts.ZoomIn; ZoomOutText.Text = Settings.Shortcuts.ZoomOut; ToggleFitText.Text = Settings.Shortcuts.ToggleFit; SkipText.Text = Settings.Shortcuts.Skip; UndoText.Text = Settings.Shortcuts.Undo; FullscreenText.Text = Settings.Shortcuts.Fullscreen;
        ActionsText.Text = JsonSerializer.Serialize(Settings.Actions, JsonOptions);
        ViewModeCombo.SelectedIndex = Settings.InitialViewMode switch { InitialViewMode.Percent100 => 1, InitialViewMode.Percent200 => 2, InitialViewMode.Percent400 => 3, _ => 0 };
        LoadingModeCombo.SelectedIndex = Settings.LoadingMode switch { LoadingMode.Preview => 1, LoadingMode.Original => 2, _ => 0 };
        SortModeCombo.SelectedIndex = Settings.ImageSortMode switch { ImageSortMode.SizeAscending => 1, ImageSortMode.SizeDescending => 2, _ => 0 };
        ScalingQualityCombo.SelectedIndex = Settings.ScalingQuality == ScalingQuality.Linear ? 1 : 0;
        DecoderBackendCombo.SelectedIndex = Settings.DecoderBackend switch { DecoderBackend.WicDirect => 1, DecoderBackend.TurboJpeg => 2, _ => 0 };
        CompareHashCheck.IsChecked = Settings.CompareHashEnabled;
        CompareSizeCheck.IsChecked = Settings.CompareSizeEnabled;
        LoggingCheck.IsChecked = Settings.LoggingEnabled;
        JournalSafeRadio.IsChecked = Settings.JournalDurability == JournalDurability.PowerLossSafe;
        JournalFastRadio.IsChecked = !JournalSafeRadio.IsChecked;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        Settings.LoggingEnabled = false;
        Settings.JournalDurability = JournalDurability.Fast;
        Settings.ImageCacheCapacityBytes = PerformanceOptions.ImageCacheCapacityBytes;
        Settings.MemoryReserveBytes = PerformanceOptions.MemoryReserveBytes;
        Settings.PreloadWorkerCount = PerformanceOptions.PreloadWorkerCount;
        Settings.PreloadMemoryLoadLimit = PerformanceOptions.PreloadMemoryLoadLimit;
        Settings.PreviewDiskCacheCapacityBytes = PerformanceOptions.PreviewDiskCacheCapacityBytes;
        Settings.UseSourceBytesCache = PerformanceOptions.UseSourceBytesCache;
        Settings.SourceBytesCapacityBytes = PerformanceOptions.SourceBytesCapacityBytes;
        Settings.InitialViewMode = InitialViewMode.Fit; Settings.LoadingMode = LoadingMode.Preview; Settings.ImageSortMode = ImageSortMode.Name; Settings.ScalingQuality = ScalingQuality.HighQuality; Settings.DecoderBackend = DecoderBackend.Wpf; Settings.CompareHashEnabled = true; Settings.CompareSizeEnabled = true; Settings.Shortcuts = ShortcutMappings.Default(); LoadFields();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Settings save requested");
        var values = new[] { NextText.Text, PreviousText.Text, RecycleText.Text, CompareText.Text, NextFolderText.Text, PreviousFolderText.Text, FirstImageText.Text, ZoomInText.Text, ZoomOutText.Text, ToggleFitText.Text, SkipText.Text, UndoText.Text, FullscreenText.Text };
        if (values.Any(v => !Enum.TryParse<Key>(v, true, out _)) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            System.Windows.MessageBox.Show(this, Tr.DialogSettingsInvalidShortcuts, Tr.DialogSettingsInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        Settings.InitialViewMode = ViewModeCombo.SelectedIndex switch { 1 => InitialViewMode.Percent100, 2 => InitialViewMode.Percent200, 3 => InitialViewMode.Percent400, _ => InitialViewMode.Fit };
        Settings.ImageSortMode = SortModeCombo.SelectedIndex switch { 1 => ImageSortMode.SizeAscending, 2 => ImageSortMode.SizeDescending, _ => ImageSortMode.Name };
        Settings.ScalingQuality = ScalingQualityCombo.SelectedIndex == 1 ? ScalingQuality.Linear : ScalingQuality.HighQuality;
        Settings.DecoderBackend = DecoderBackendCombo.SelectedIndex switch { 1 => DecoderBackend.WicDirect, 2 => DecoderBackend.TurboJpeg, _ => DecoderBackend.Wpf };
        Settings.LoadingMode = LoadingModeCombo.SelectedIndex switch { 1 => LoadingMode.Preview, 2 => LoadingMode.Original, _ => LoadingMode.Fast };
        Settings.CompareHashEnabled = CompareHashCheck.IsChecked == true;
        Settings.CompareSizeEnabled = CompareSizeCheck.IsChecked == true;
        Settings.LoggingEnabled = LoggingCheck.IsChecked == true;
        Settings.JournalDurability = JournalSafeRadio.IsChecked == true ? JournalDurability.PowerLossSafe : JournalDurability.Fast;
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
                throw new JsonException("An action has no name, an invalid shortcut or an invalid operation."); // never shown (caught below)
            if (Settings.Actions.GroupBy(action => action.Shortcut, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new JsonException("Two actions use the same shortcut."); // never shown (caught below)
        }
        catch { System.Windows.MessageBox.Show(this, Tr.DialogSettingsInvalidActionsJson, Tr.DialogSettingsInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var validator = new PhotoReview.App.Services.WpfKeyNameValidator();
        var shortcutError = new SettingsValidator(validator).ValidateShortcuts(Settings);
        if (shortcutError is not null) { System.Windows.MessageBox.Show(this, shortcutError, Tr.DialogSettingsInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_localization is not null)
            Settings.UiLanguage = LanguageOptions.ToSetting(LanguageCombo.SelectedItem as LanguageOption, Settings.UiLanguage);
        if (_store is not null)
            _store.Save(Settings);
        else
            AppSettings.Save(Settings);
        AppLog.Info("Settings saved");
        // Q-L8: the new language applies live (UI thread, ADR 0005); every {loc:Tr} text follows the switch.
        if (_localization is not null && !string.Equals(_localization.RequestedLanguage, Settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
            _localization.Switch(Settings.UiLanguage);
        DialogResult = true;
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
            if (editor.ShowDialog() == true) ActionsText.Text = JsonSerializer.Serialize(editor.Actions, JsonOptions);
        }
        catch { System.Windows.MessageBox.Show(this, Tr.DialogOpenEditorFailedMessage, Tr.DialogOpenEditorFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
