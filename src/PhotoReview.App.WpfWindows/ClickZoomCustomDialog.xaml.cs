using System.Windows;
using PhotoReview.App.Presenters;

namespace PhotoReview.App;

/// <summary>
/// Right-click context menu "Click zoom level" &gt; "Custom...": a small dark-themed dialog asking for a value in
/// [<see cref="AppSettings.MinClickZoomPercent"/>, <see cref="AppSettings.MaxClickZoomPercent"/>] (same rule and
/// message as the Settings window's click zoom level box). WPF view only: the rule lives in <see cref="ClickZoomCustomPresenter"/>.
/// </summary>
public partial class ClickZoomCustomDialog : Window
{
    private readonly ClickZoomCustomPresenter _presenter;

    /// <summary>The validated value once <see cref="Window.DialogResult"/> is true.</summary>
    public int Value => _presenter.Value;

    public ClickZoomCustomDialog(int currentPercent)
    {
        _presenter = new ClickZoomCustomPresenter(currentPercent);
        InitializeComponent();
        PercentText.Text = _presenter.InitialText;
        Loaded += (_, _) =>
        {
            PercentText.Focus();
            PercentText.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_presenter.TryAccept(PercentText.Text) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}