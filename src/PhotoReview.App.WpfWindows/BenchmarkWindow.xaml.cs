using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Wpf;

namespace PhotoReview.App;

/// <summary>
/// The Benchmark window (modeless): a thin WPF view. Profile list, Run / Quick check / Cancel, progress, the result grid,
/// report saving and the close-mid-run handling live in <see cref="BenchmarkPresenter"/> (WP-35 G2a).
/// </summary>
public partial class BenchmarkWindow : Window, IDisposable, IBenchmarkView
{
    /// <summary>Header of the best-marker column (the XAML cannot reach the App.Shared constant by x:Static).</summary>
    public const string BestSymbol = BenchmarkResultRow.BestSymbol;

    private readonly BenchmarkPresenter _presenter;

    public BenchmarkWindow(string? initialFolder)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        _presenter = new BenchmarkPresenter(this, new WindowUiScheduler(Dispatcher),
            BenchmarkProfileSession.Factory(WpfBitmapSourceCodec.Instance, () => new WpfBitmapImageDecoder()));
        FolderText.Text = BenchmarkPresenter.InitialFolder(initialFolder);
        foreach (var item in _presenter.ProfileItems) ProfilesList.Items.Add(item);
        ProfilesList.SelectedItems.Add(_presenter.DefaultSelection);
        ImageLimitText.Text = BenchmarkPresenter.DefaultImageLimitText;
        ResultsGrid.ItemsSource = _presenter.Rows;
        Localizer.CurrentChanged += OnLanguageChanged;
    }

    // XAML texts follow a language switch through {loc:Tr}; the profile list and the grid cells are
    // code-provided, so refresh them here (the window is modeless and can stay open across a switch).
    private void OnLanguageChanged(object? sender, EventArgs e) => _ = Dispatcher.InvokeAsync(_presenter.NotifyLanguageChanged);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Tr.BenchmarkPickFolderTitle };
        if (Directory.Exists(FolderText.Text)) dialog.InitialDirectory = FolderText.Text;
        if (dialog.ShowDialog(this) == true) FolderText.Text = dialog.FolderName;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ProfilesList.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => ProfilesList.UnselectAll();

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var profiles = ProfilesList.SelectedItems.Cast<BenchmarkProfileItem>().Select(i => i.Profile).ToArray();
        await _presenter.RunSelectedAsync(profiles, FolderText.Text, ImageLimitText.Text);
    }

    // "Quick check": a fixed, fast smoke run that never touches ProfilesList.SelectedItems.
    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void QuickCheck_Click(object sender, RoutedEventArgs e) =>
        await _presenter.QuickCheckAsync(FolderText.Text, ImageLimitText.Text);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _presenter.Cancel();

    // This window is opened modelessly (dialog.Show() in MainWindow), not via ShowDialog(),
    // so IsCancel/DialogResult cannot be used on the Close button -- WPF throws
    // InvalidOperationException when a non-dialog Window's DialogResult is set. OnClosed cancels
    // any running benchmark so the background run doesn't outlive the window.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _presenter.Dispose();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e) => _presenter.OpenReport();

    private void OpenResultFolder_Click(object sender, RoutedEventArgs e) => _presenter.OpenResultFolder();

    // ---- IBenchmarkView ----

    void IBenchmarkView.SetStatus(string text) => StatusText.Text = text;

    void IBenchmarkView.SetProgress(double fraction) => RunProgress.Value = fraction;

    void IBenchmarkView.SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        QuickCheckButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        BrowseButton.IsEnabled = !running;
        ProfilesList.IsEnabled = !running;
        ImageLimitText.IsEnabled = !running;
        FolderText.IsEnabled = !running;
    }

    void IBenchmarkView.RefreshRows() => ResultsGrid.Items.Refresh();

    /// <summary>Opens Explorer and disposes the process handle; a launch failure is logged (the dispatcher would swallow it silently).</summary>
    void IBenchmarkView.OpenInExplorer(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Could not start Explorer", ex);
        }
    }

    /// <summary>The window's dispatcher as the presenter's UI scheduler (progress comes from background threads).</summary>
    private sealed class WindowUiScheduler(Dispatcher dispatcher) : IUiScheduler
    {
        public void Post(Action action) => _ = dispatcher.BeginInvoke(action);

        public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) =>
            new(dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background, cancellationToken).Task);
    }
}
