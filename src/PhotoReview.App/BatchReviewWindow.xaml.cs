using System.Globalization;
using System.Windows;
using System.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

public partial class BatchReviewWindow : Window
{
    public BatchReviewWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        FilesList.ItemsSource = paths.Select(BuildItem).ToList();
        SummaryText.Text = Tr.BatchReviewSummary(paths.Count);
    }

    /// <summary>One list line; any path the file system rejects (bad chars, unsupported form, missing) gives the "missing" line instead of breaking the list.</summary>
    internal static string BuildItem(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return Tr.BatchReviewItem(Path.GetFileName(path), info.Length.ToString("N0", CultureInfo.CurrentCulture), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Tr.BatchReviewItemMissing(Path.GetFileName(path), path);
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
