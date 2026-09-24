using System.IO;
using System.Windows;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

public partial class RecoveryWindow : Window
{
    private readonly IReadOnlyList<JournalEntry> _entries;
    private readonly Func<JournalEntry, RecoveryRetryResult>? _retry;

    public RecoveryWindow(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, RecoveryRetryResult>? retry = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        InitializeComponent();
        _entries = entries;
        _retry = retry;
        EntriesList.ItemsSource = entries.Select(FormatEntry).ToList();
        EntriesList.SelectionChanged += (_, _) => RetryButton.IsEnabled = _retry is not null && EntriesList.SelectedIndex >= 0 && _entries[EntriesList.SelectedIndex].Type is FileOperationType.Move or FileOperationType.Copy;
        SummaryText.Text = entries.Count == 0 ? Tr.RecoverySummaryEmpty : Tr.RecoverySummaryCount(entries.Count);
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
