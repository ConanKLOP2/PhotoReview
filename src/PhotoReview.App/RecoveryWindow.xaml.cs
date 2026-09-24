using System.IO;
using System.Windows;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

public partial class RecoveryWindow : Window
{
    private readonly List<JournalEntry> _entries;
    private readonly Func<JournalEntry, RecoveryRetryResult>? _retry;
    private readonly Action<IReadOnlyList<JournalEntry>>? _dismiss;

    public RecoveryWindow(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, RecoveryRetryResult>? retry = null, Action<IReadOnlyList<JournalEntry>>? dismiss = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        InitializeComponent();
        _entries = entries.ToList();
        _retry = retry;
        _dismiss = dismiss;
        EntriesList.SelectionChanged += (_, _) => UpdateButtons();
        RefreshEntries();
    }

    // Items wrap the entry so multi-selection maps back unambiguously even when two rows read the same.
    private sealed record Row(JournalEntry Entry)
    {
        public override string ToString() => FormatEntry(Entry);
    }

    private void RefreshEntries()
    {
        EntriesList.ItemsSource = _entries.Select(entry => new Row(entry)).ToList();
        SummaryText.Text = _entries.Count == 0 ? Tr.RecoverySummaryEmpty : Tr.RecoverySummaryCount(_entries.Count);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        RetryButton.IsEnabled = _retry is not null && EntriesList.SelectedItems.Count == 1 && _entries[EntriesList.SelectedIndex].Type is FileOperationType.Move or FileOperationType.Copy;
        ClearSelectedButton.IsEnabled = _dismiss is not null && EntriesList.SelectedItems.Count > 0;
        ClearAllButton.IsEnabled = _dismiss is not null && _entries.Count > 0;
    }

    private void ClearSelected_Click(object sender, RoutedEventArgs e) => Dismiss(EntriesList.SelectedItems.Cast<Row>().Select(row => row.Entry).ToList());

    private void ClearAll_Click(object sender, RoutedEventArgs e) => Dismiss(_entries.ToList());

    private void Dismiss(List<JournalEntry> entries)
    {
        if (_dismiss is null || entries.Count == 0) return;
        var prompt = Tr.RecoveryDismissConfirmMessage(entries.Count);
        if (System.Windows.MessageBox.Show(this, prompt, Tr.RecoveryDismissConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _dismiss(entries); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Tr.RecoveryDismissFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _entries.RemoveAll(entries.Contains);
        RefreshEntries();
    }

    // Journal errors are localized by their stable code; old entries without a code show the stored text (Q-L3).
    internal static string FormatEntry(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var state = StateText(entry.State);
        var operation = OperationText(entry.Type);
        var fileName = Path.GetFileName(entry.Source);
        var error = JournalErrors.Describe(entry);
        return error is null
            ? Tr.RecoveryEntry(state, operation, fileName, entry.Source)
            : Tr.RecoveryEntryWithError(state, operation, fileName, entry.Source, error);
    }

    internal static string OperationText(FileOperationType type) => type switch
    {
        FileOperationType.Move => Tr.EnumFileOperationMove,
        FileOperationType.Copy => Tr.EnumFileOperationCopy,
        FileOperationType.Recycle => Tr.EnumFileOperationRecycle,
        _ => type.ToString(),
    };

    internal static string StateText(JournalState state) => state switch
    {
        JournalState.Prepared => Tr.EnumJournalStatePrepared,
        JournalState.Committed => Tr.EnumJournalStateCommitted,
        JournalState.Failed => Tr.EnumJournalStateFailed,
        _ => state.ToString(),
    };

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retry is null || EntriesList.SelectedIndex < 0) return;
        var entry = _entries[EntriesList.SelectedIndex];
        if (System.Windows.MessageBox.Show(this, Tr.DialogConfirmRetryMessage(OperationText(entry.Type), Path.GetFileName(entry.Source)), Tr.DialogConfirmRetryTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
    }
}
