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
        FilesList.ItemsSource = paths.Select(path =>
        {
            try
            {
                var info = new FileInfo(path);
                return Tr.BatchReviewItem(Path.GetFileName(path), info.Length.ToString("N0", CultureInfo.CurrentCulture), path);
            }
            catch
            {
                return Tr.BatchReviewItemMissing(Path.GetFileName(path), path);
            }
        }).ToList();
        SummaryText.Text = Tr.BatchReviewSummary(paths.Count);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
