using System.IO;
using System.Windows;
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
        public override string ToString() => $"{Entry.State} · {Entry.Type} · {Path.GetFileName(Entry.Source)} · {Entry.Source}{(Entry.Error is null ? string.Empty : $" · {Entry.Error}")}";
    }

    private void RefreshEntries()
    {
        EntriesList.ItemsSource = _entries.Select(entry => new Row(entry)).ToList();
        SummaryText.Text = _entries.Count == 0 ? "Không có operation pending/failed cần xem." : $"Có {_entries.Count} operation pending/failed. Chọn Move/Copy để retry thủ công có kiểm tra an toàn.";
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
        var prompt = $"Xoá {entries.Count} mục khỏi danh sách Recovery?\n\nChỉ xoá khỏi danh sách, không di chuyển/xoá file nào. Sau khi xoá sẽ không thể retry các mục này.";
        if (System.Windows.MessageBox.Show(this, prompt, "Xác nhận xoá", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _dismiss(entries); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Không thể xoá", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _entries.RemoveAll(entries.Contains);
        RefreshEntries();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retry is null || EntriesList.SelectedIndex < 0) return;
        var entry = _entries[EntriesList.SelectedIndex];
        if (System.Windows.MessageBox.Show(this, $"Retry {entry.Type} cho {Path.GetFileName(entry.Source)}?", "Xác nhận retry", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RecoveryRetryResult result;
        try { result = _retry(entry); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Retry bị từ chối", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var journalWarning = result.Succeeded && !result.JournalPersisted;
        var title = result.Succeeded
            ? journalWarning ? "Retry hoàn tất nhưng nhật ký lỗi" : "Retry thành công"
            : "Retry bị từ chối";
        var icon = result.Succeeded && !journalWarning ? MessageBoxImage.Information : MessageBoxImage.Warning;
        System.Windows.MessageBox.Show(this, result.Message, title, MessageBoxButton.OK, icon);
        if (result.Succeeded) Close();
    }
}
