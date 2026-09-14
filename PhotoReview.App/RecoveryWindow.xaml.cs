using System.IO;
using System.Windows;

namespace PhotoReview.App;

public partial class RecoveryWindow : Window
{
    private readonly IReadOnlyList<JournalEntry> _entries;
    private readonly Func<JournalEntry, RecoveryRetryResult>? _retry;

    public RecoveryWindow(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, RecoveryRetryResult>? retry = null)
    {
        InitializeComponent();
        _entries = entries;
        _retry = retry;
        EntriesList.ItemsSource = entries.Select(entry => $"{entry.State} · {entry.Type} · {Path.GetFileName(entry.Source)} · {entry.Source}{(entry.Error is null ? string.Empty : $" · {entry.Error}")}").ToList();
        EntriesList.SelectionChanged += (_, _) => RetryButton.IsEnabled = _retry is not null && EntriesList.SelectedIndex >= 0 && _entries[EntriesList.SelectedIndex].Type is "Move" or "Copy";
        SummaryText.Text = entries.Count == 0 ? "Không có operation pending/failed cần xem." : $"Có {entries.Count} operation pending/failed. Chọn Move/Copy để retry thủ công có kiểm tra an toàn.";
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retry is null || EntriesList.SelectedIndex < 0) return;
        var entry = _entries[EntriesList.SelectedIndex];
        if (System.Windows.MessageBox.Show(this, $"Retry {entry.Type} cho {Path.GetFileName(entry.Source)}?", "Xác nhận retry", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var result = _retry(entry);
        System.Windows.MessageBox.Show(this, result.Message, result.Succeeded ? "Retry thành công" : "Retry bị từ chối", MessageBoxButton.OK, result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (result.Succeeded) Close();
    }
}
