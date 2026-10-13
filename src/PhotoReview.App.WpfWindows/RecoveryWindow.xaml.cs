using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

/// <summary>One Recovery list row for the WPF template: the text comes from <see cref="RecoveryRowModel"/>; this adds change notification and the brush.</summary>
internal sealed class RecoveryRow : INotifyPropertyChanged
{
    public RecoveryRow(RecoveryRowModel model)
    {
        Model = model;
        model.ResultChanged += OnResultChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecoveryRowModel Model { get; }

    public JournalEntry Entry => Model.Entry;

    public RecoveryCheckResult? Check => Model.Check;

    public string Title => Model.Title;

    public string Subtitle => Model.Subtitle;

    public string BadgeText => Model.BadgeText;

    public Brush BadgeBrush => RecoveryBrushes.For(Model.BadgeTone);

    /// <summary>Test/UI seam: stores a check result (the presenter normally does this while the live check runs).</summary>
    internal void SetResult(RecoveryCheckResult? result) => Model.SetResult(result);

    private void OnResultChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Check)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeBrush)));
    }
}

/// <summary>The Recovery window, WPF view only (WP-35 G0): rows, filter, details, live check, Clear and Retry live in <see cref="RecoveryWindowPresenter"/>.</summary>
public partial class RecoveryWindow : Window, IRecoveryCheckView
{
    private readonly RecoveryWindowPresenter _presenter;
    private readonly List<RecoveryRow> _rows;
    private readonly System.Collections.ObjectModel.ObservableCollection<RecoveryRow> _visible = [];

    public RecoveryWindow(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, Task<RecoveryRetryResult>>? retry = null, Func<IReadOnlyList<JournalEntry>, DismissOutcome>? dismiss = null, IFileSystem? fileSystem = null,
        Func<bool>? allowPermanentDelete = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        _presenter = new RecoveryWindowPresenter(entries, retry, dismiss, fileSystem, allowPermanentDelete);
        _rows = _presenter.Rows.Select(model => new RecoveryRow(model)).ToList();
        foreach (var row in _rows) row.PropertyChanged += OnRowChanged;
        void ExplorerFailed(string message) => System.Windows.MessageBox.Show(this, message, Tr.RecoveryExplorerFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        SourcePanel.ExplorerFailed += ExplorerFailed;
        DestinationPanel.ExplorerFailed += ExplorerFailed;
        EntriesList.ItemsSource = _visible;
        EntriesList.SelectionChanged += (_, _) => { UpdateDetails(); UpdateButtons(); };
        // The check starts when the window opens (Loaded), off the UI thread, and is cancelled on close.
        Loaded += (_, _) => RunChecksAsync().FireAndLog("Recovery check failed");
        Closed += (_, _) => _presenter.CancelCheck();
        RefreshEntries();
    }

    private RecoveryFilter CurrentFilter => (RecoveryFilter)Math.Max(0, FilterCombo.SelectedIndex);

    private RecoveryRow? SelectedRow => EntriesList.SelectedItems.Count == 1 ? EntriesList.SelectedItem as RecoveryRow : null;

    internal RecoveryPathPanel SourceDetails => SourcePanel;

    internal RecoveryPathPanel DestinationDetails => DestinationPanel;

    internal IReadOnlyList<RecoveryRow> VisibleRows => _visible;

    internal void SelectRow(int visibleIndex) => EntriesList.SelectedIndex = visibleIndex;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecoveryRow.Check) && ReferenceEquals(sender, SelectedRow))
        {
            UpdateDetails();
            UpdateButtons();
        }
    }

    /// <summary>Drops the WPF rows whose model the presenter removed (after Clear or a superseded retry).</summary>
    private void SyncRows()
    {
        var remaining = _presenter.Rows.ToHashSet();
        _rows.RemoveAll(row => !remaining.Contains(row.Model));
    }

    // Rebuilds the visible list (filter applied) and keeps the selection when the rows are still shown.
    private void RefreshEntries()
    {
        var selected = EntriesList.SelectedItems.Cast<RecoveryRow>().ToList();
        var shownModels = _presenter.Shown(CurrentFilter).ToHashSet();
        var shown = _rows.Where(row => shownModels.Contains(row.Model)).ToList();
        _visible.Clear();
        foreach (var row in shown) _visible.Add(row);
        var shownSet = shown.ToHashSet(); // List.Contains per selected row is O(selected * shown) on a big journal
        foreach (var row in selected.Where(shownSet.Contains)) EntriesList.SelectedItems.Add(row);
        SummaryText.Text = _presenter.SummaryText;
        FilterEmptyText.Visibility = _presenter.FilterEmptyVisible(shown.Count) ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetails();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var states = _presenter.ButtonStates(SelectedRow?.Model, EntriesList.SelectedItems.Count);
        RetryButton.IsEnabled = states.Retry;
        ClearSelectedButton.IsEnabled = states.ClearSelected;
        ClearAllButton.IsEnabled = states.ClearAll;
    }

    private void UpdateDetails()
    {
        var row = SelectedRow;
        DetailsEmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsScroll.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;

        var details = RecoveryWindowPresenter.Details(row.Model);
        ErrorText.Text = details.ErrorText;
        ErrorText.Visibility = details.ErrorVisible ? Visibility.Visible : Visibility.Collapsed;
        var brush = RecoveryBrushes.For(details.VerdictTone);
        VerdictText.Text = details.VerdictText;
        VerdictText.Foreground = VerdictBadge.BorderBrush = brush;
        ExplainText.Text = details.ExplainText;
        ActionText.Text = details.ActionText;
        if (details.GroupMembers is { } members)
        {
            GroupMembersHeading.Text = details.GroupHeading;
            GroupMembersHeading.Visibility = Visibility.Visible;
            GroupMembersList.ItemsSource = members;
            GroupMembersList.Visibility = Visibility.Visible;
        }
        else
        {
            GroupMembersHeading.Visibility = Visibility.Collapsed;
            GroupMembersList.Visibility = Visibility.Collapsed;
            GroupMembersList.ItemsSource = null;
        }
        // A group row checked earlier collapsed the panels (it lists members instead); a row that is being (re)checked or is a
        // plain entry brings them back before they are filled.
        SourcePanel.Visibility = details.SourceVisible ? Visibility.Visible : Visibility.Collapsed;
        if (details.Source is { } source) SourcePanel.Show(source);
        DestinationPanel.Visibility = details.DestinationVisible ? Visibility.Visible : Visibility.Collapsed;
        NoDestinationText.Visibility = details.NoDestinationVisible ? Visibility.Visible : Visibility.Collapsed;
        if (details.Destination is { } destination) DestinationPanel.Show(destination);
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _rows is null) return;
        RefreshEntries();
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => RunChecksAsync().FireAndLog("Recovery re-check failed");

    /// <summary>Read-only live check of every entry (see <see cref="RecoveryWindowPresenter.RunChecksAsync"/>).</summary>
    internal Task RunChecksAsync() => _presenter.RunChecksAsync(this);

    void IRecoveryCheckView.SetCheckStatus(string text) => CheckStatusText.Text = text;

    void IRecoveryCheckView.SetRecheckEnabled(bool enabled) => RecheckButton.IsEnabled = enabled;

    void IRecoveryCheckView.CheckCompleted() => RefreshEntries();

    private void ClearSelected_Click(object sender, RoutedEventArgs e) => Dismiss(EntriesList.SelectedItems.Cast<RecoveryRow>().ToList());

    private void ClearAll_Click(object sender, RoutedEventArgs e) => Dismiss(_rows.ToList());

    private void Dismiss(List<RecoveryRow> rows)
    {
        var models = rows.Select(row => row.Model).ToList();
        var prompt = _presenter.DismissPrompt(models);
        if (prompt is null) return;
        if (System.Windows.MessageBox.Show(this, prompt, Tr.RecoveryDismissConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var result = _presenter.Dismiss(models);
        if (result.Error is not null)
        {
            System.Windows.MessageBox.Show(this, result.Error, Tr.RecoveryDismissFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SyncRows();
        RefreshEntries();
        if (result.SkippedCount > 0)
        {
            System.Windows.MessageBox.Show(this, Tr.RecoveryDismissStaleMessage(result.SkippedCount), Tr.RecoveryDismissStaleTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Runs the retry delegate while keeping the UI thread free (a retried cross-drive copy can take minutes):
    /// the Retry/Clear buttons are disabled and the cursor shows Wait until it completes.
    /// </summary>
    internal Task<RecoveryRetryResult> ExecuteRetryAsync(JournalEntry entry) =>
        _presenter.ExecuteRetryAsync(entry, busy =>
        {
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
            UpdateButtons();
        }, () =>
        {
            SyncRows();
            RefreshEntries();
        });

    /// <summary>
    /// A retried move/copy runs on a pool thread with no cancellation token, so closing the window would lose its result
    /// (including the "journal could not be written" warning) and re-enable the main window while the file is still being
    /// moved (R2-A-01). The window therefore refuses to close until the retry completes.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_presenter.CanClose) e.Cancel = true;
        base.OnClosing(e);
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = AsyncVoidJustification.WpfEventHandler)]
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (!_presenter.CanStartRetry(row?.Model)) return;
        var entry = row!.Entry;
        var confirm = _presenter.RetryConfirmText(row.Model);
        if (System.Windows.MessageBox.Show(this, confirm, Tr.DialogConfirmRetryTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RecoveryRetryResult result;
        try { result = await ExecuteRetryAsync(entry); }
        catch (Exception ex)
        {
            if (IsLoaded) System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogRetryRejectedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!IsLoaded) return; // defensive: OnClosing normally keeps the window open while a retry runs
        var notice = RecoveryWindowPresenter.DescribeRetry(result);
        System.Windows.MessageBox.Show(this, notice.Message, notice.Title, MessageBoxButton.OK,
            notice.IsWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (notice.CloseWindow) Close();
        else if (notice.Recheck) RunChecksAsync().FireAndLog("Recovery re-check failed");
    }
}
