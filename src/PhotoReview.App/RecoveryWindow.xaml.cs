using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

/// <summary>One Recovery list row; <see cref="Check"/> stays null ("checking") until the background check reports.</summary>
internal sealed class RecoveryRow(JournalEntry entry) : INotifyPropertyChanged
{
    private RecoveryCheckResult? _result;

    public event PropertyChangedEventHandler? PropertyChanged;

    public JournalEntry Entry { get; } = entry;

    public RecoveryCheckResult? Check => _result;

    public string Title => Path.GetFileName(Entry.Source);

    public string Subtitle => Tr.RecoveryRowSubtitle(RecoveryPresenter.StateText(Entry.State), RecoveryPresenter.OperationText(Entry.Type));

    public string BadgeText => _result is null ? Tr.RecoveryRowChecking : RecoveryPresenter.VerdictText(_result.Verdict);

    public Brush BadgeBrush => _result is null ? RecoveryPresenter.NeutralBrush : RecoveryPresenter.VerdictBrush(_result.Verdict);

    public void SetResult(RecoveryCheckResult? result)
    {
        _result = result;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Check)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeBrush)));
    }
}

public partial class RecoveryWindow : Window
{
    private readonly List<RecoveryRow> _rows;
    private readonly Func<JournalEntry, RecoveryRetryResult>? _retry;
    private readonly Action<IReadOnlyList<JournalEntry>>? _dismiss;
    private readonly RecoveryFileCheck _checker;
    private readonly System.Collections.ObjectModel.ObservableCollection<RecoveryRow> _visible = [];
    private Action? _cancelCheck;

    public RecoveryWindow(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, RecoveryRetryResult>? retry = null, Action<IReadOnlyList<JournalEntry>>? dismiss = null, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        InitializeComponent();
        _rows = entries.Select(entry => new RecoveryRow(entry)).ToList();
        _retry = retry;
        _dismiss = dismiss;
        _checker = new RecoveryFileCheck(fileSystem ?? new PhysicalFileSystem());
        foreach (var row in _rows) row.PropertyChanged += OnRowChanged;
        void ExplorerFailed(string message) => System.Windows.MessageBox.Show(this, message, Tr.RecoveryExplorerFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        SourcePanel.ExplorerFailed += ExplorerFailed;
        DestinationPanel.ExplorerFailed += ExplorerFailed;
        EntriesList.ItemsSource = _visible;
        EntriesList.SelectionChanged += (_, _) => { UpdateDetails(); UpdateButtons(); };
        // The check starts when the window opens (Loaded), off the UI thread, and is cancelled on close.
        Loaded += (_, _) => _ = RunChecksAsync();
        Closed += (_, _) => _cancelCheck?.Invoke();
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

    // Rebuilds the visible list (filter applied) and keeps the selection when the rows are still shown.
    private void RefreshEntries()
    {
        var selected = EntriesList.SelectedItems.Cast<RecoveryRow>().ToList();
        var filter = CurrentFilter;
        var shown = _rows.Where(row => RecoveryPresenter.Matches(filter, row.Check?.Verdict)).ToList();
        _visible.Clear();
        foreach (var row in shown) _visible.Add(row);
        foreach (var row in selected.Where(shown.Contains)) EntriesList.SelectedItems.Add(row);
        SummaryText.Text = _rows.Count == 0 ? Tr.RecoverySummaryEmpty : Tr.RecoverySummaryCount(_rows.Count);
        FilterEmptyText.Visibility = _rows.Count > 0 && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetails();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        RetryButton.IsEnabled = _retry is not null && RecoveryPresenter.AllowsRetry(SelectedRow?.Check);
        ClearSelectedButton.IsEnabled = _dismiss is not null && EntriesList.SelectedItems.Count > 0;
        ClearAllButton.IsEnabled = _dismiss is not null && _rows.Count > 0;
    }

    private void UpdateDetails()
    {
        var row = SelectedRow;
        DetailsEmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsScroll.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;

        var entry = row.Entry;
        var result = row.Check;
        var error = JournalErrors.Describe(entry);
        ErrorText.Text = error is null ? string.Empty : Tr.RecoveryDetailError(error);
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;

        if (result is null)
        {
            VerdictText.Text = Tr.RecoveryRowChecking;
            VerdictText.Foreground = VerdictBadge.BorderBrush = RecoveryPresenter.NeutralBrush;
            ExplainText.Text = Tr.RecoveryDetailChecking;
            ActionText.Text = string.Empty;
            SourcePanel.Show(new RecoveryPathView(Tr.RecoveryDetailSource, entry.Source, string.Empty, RecoveryPresenter.NeutralBrush, string.Empty, RecoveryPresenter.JournalText(entry), null));
            ShowDestination(entry.Destination is null ? null : new RecoveryPathView(Tr.RecoveryDetailDestination, entry.Destination, string.Empty, RecoveryPresenter.NeutralBrush, string.Empty, RecoveryPresenter.JournalText(entry), null));
            return;
        }

        var brush = RecoveryPresenter.VerdictBrush(result.Verdict);
        VerdictText.Text = RecoveryPresenter.VerdictText(result.Verdict);
        VerdictText.Foreground = brush;
        VerdictBadge.BorderBrush = brush;
        ExplainText.Text = RecoveryPresenter.ExplainText(result.Verdict);
        ActionText.Text = RecoveryPresenter.ActionText(result.Verdict);
        SourcePanel.Show(RecoveryPresenter.PathView(Tr.RecoveryDetailSource, result.Source, entry, folderMissingNote: false));
        ShowDestination(result.Destination is null ? null : RecoveryPresenter.PathView(Tr.RecoveryDetailDestination, result.Destination, entry, result.DestinationFolderMissing));
    }

    private void ShowDestination(RecoveryPathView? view)
    {
        DestinationPanel.Visibility = view is null ? Visibility.Collapsed : Visibility.Visible;
        NoDestinationText.Visibility = view is null ? Visibility.Visible : Visibility.Collapsed;
        if (view is not null) DestinationPanel.Show(view);
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _rows is null) return;
        RefreshEntries();
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => _ = RunChecksAsync();

    /// <summary>
    /// Read-only live check of every entry, on a worker thread; each result is posted back to the UI as it arrives.
    /// A newer run (Re-check) or closing the window cancels the previous one. No file is ever modified.
    /// </summary>
    internal async Task RunChecksAsync()
    {
        _cancelCheck?.Invoke();
        using var cts = new CancellationTokenSource();
        _cancelCheck = () =>
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* that run already finished */ }
        };
        var token = cts.Token;
        var rows = _rows.ToList();
        var total = rows.Count;
        foreach (var row in rows) row.SetResult(null);
        if (total == 0)
        {
            CheckStatusText.Text = string.Empty;
            return;
        }

        RecheckButton.IsEnabled = false;
        var done = 0;
        CheckStatusText.Text = Tr.RecoveryCheckProgress(0, total);
        var progress = new Progress<(RecoveryRow Row, RecoveryCheckResult Outcome)>(item =>
        {
            if (token.IsCancellationRequested) return;
            item.Row.SetResult(item.Outcome);
            CheckStatusText.Text = Tr.RecoveryCheckProgress(++done, total);
        });
        try
        {
            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    ((IProgress<(RecoveryRow, RecoveryCheckResult)>)progress).Report((row, _checker.Check(row.Entry)));
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested) return;
        RecheckButton.IsEnabled = true;
        CheckStatusText.Text = Tr.RecoveryCheckDone(total, DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
        RefreshEntries();
    }

    private void ClearSelected_Click(object sender, RoutedEventArgs e) => Dismiss(EntriesList.SelectedItems.Cast<RecoveryRow>().ToList());

    private void ClearAll_Click(object sender, RoutedEventArgs e) => Dismiss(_rows.ToList());

    private void Dismiss(List<RecoveryRow> rows)
    {
        if (_dismiss is null || rows.Count == 0) return;
        var prompt = Tr.RecoveryDismissConfirmMessage(rows.Count);
        if (System.Windows.MessageBox.Show(this, prompt, Tr.RecoveryDismissConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _dismiss(rows.Select(row => row.Entry).ToList()); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Tr.RecoveryDismissFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _rows.RemoveAll(rows.Contains);
        RefreshEntries();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (_retry is null || row is null) return;
        var entry = row.Entry;
        if (System.Windows.MessageBox.Show(this, Tr.DialogConfirmRetryMessage(RecoveryPresenter.OperationText(entry.Type), Path.GetFileName(entry.Source)), Tr.DialogConfirmRetryTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RecoveryRetryResult result;
        try { result = _retry(entry); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogRetryRejectedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var journalWarning = result.Succeeded && !result.JournalPersisted;
        var title = result.Succeeded
            ? journalWarning ? Tr.DialogRetryDoneJournalFailedTitle : Tr.DialogRetrySucceededTitle
            : Tr.DialogRetryRejectedTitle;
        var icon = result.Succeeded && !journalWarning ? MessageBoxImage.Information : MessageBoxImage.Warning;
        System.Windows.MessageBox.Show(this, result.Message, title, MessageBoxButton.OK, icon);
        if (result.Succeeded) Close();
        else _ = RunChecksAsync();
    }
}
