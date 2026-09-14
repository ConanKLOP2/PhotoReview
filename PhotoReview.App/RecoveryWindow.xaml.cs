using System.IO;
using System.Windows;

namespace PhotoReview.App;

public partial class RecoveryWindow : Window
{
    public RecoveryWindow(IReadOnlyList<JournalEntry> entries)
    {
        InitializeComponent();
        EntriesList.ItemsSource = entries.Select(entry => $"{entry.State} · {entry.Type} · {Path.GetFileName(entry.Source)} · {entry.Source}{(entry.Error is null ? string.Empty : $" · {entry.Error}")}").ToList();
        SummaryText.Text = entries.Count == 0 ? "Không có operation pending cần xử lý." : $"Có {entries.Count} operation chưa được xác nhận. Không có thao tác nào tự động được thực hiện.";
    }
}
