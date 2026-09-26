using System.Globalization;
using System.Windows;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.App;

/// <summary>
/// Right-click context menu "Click zoom level" &gt; "Custom…": a small dark-themed dialog asking for a value in
/// [<see cref="AppSettings.MinClickZoomPercent"/>, <see cref="AppSettings.MaxClickZoomPercent"/>] (same rule and
/// message as the Settings window's click zoom level box).
/// </summary>
public partial class ClickZoomCustomDialog : Window
{
    /// <summary>The validated value once <see cref="Window.DialogResult"/> is true.</summary>
    public int Value { get; private set; }

    public ClickZoomCustomDialog(int currentPercent)
    {
        InitializeComponent();
        PercentText.Text = currentPercent.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) =>
        {
            PercentText.Focus();
            PercentText.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PercentText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
            || percent < AppSettings.MinClickZoomPercent || percent > AppSettings.MaxClickZoomPercent)
        {
            ErrorText.Text = Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent);
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Value = percent;
        DialogResult = true;
    }
}
