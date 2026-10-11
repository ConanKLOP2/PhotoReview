using System.Globalization;
using System.Windows;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

public partial class BatchReviewWindow : Window
{
    /// <summary>Path-only form (sizes unknown: every line shows the "no longer exists" text). Production passes sizes.</summary>
    public BatchReviewWindow(IReadOnlyList<string> paths)
        : this(paths.Select(path => new BatchReviewItem(path, null)).ToList())
    {
    }

    /// <summary>RV-A13: sizes come from the caller (the catalog scan already stat'ed every file); the UI thread never touches the disk here.</summary>
    public BatchReviewWindow(IReadOnlyList<BatchReviewItem> items)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        FilesList.ItemsSource = items.Select(BuildItem).ToList();
        SummaryText.Text = Tr.BatchReviewSummary(items.Count);
    }

    /// <summary>One list line; an item without a known size (missing, or never stat'ed) gives the "missing" line instead of breaking the list.</summary>
    internal static string BuildItem(BatchReviewItem item) => item.Length is { } length
        ? Tr.BatchReviewItem(Path.GetFileName(item.Path), length.ToString("N0", CultureInfo.CurrentCulture), item.Path)
        : Tr.BatchReviewItemMissing(Path.GetFileName(item.Path), item.Path);

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
