using System.Windows;
using System.IO;

namespace PhotoReview.App;

public partial class BatchReviewWindow : Window
{
    public BatchReviewWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        FilesList.ItemsSource = paths.Select(path =>
        {
            var info = new FileInfo(path);
            return $"{Path.GetFileName(path)}    ({info.Length:N0} bytes)    {path}";
        }).ToList();
        SummaryText.Text = $"{paths.Count} file sẽ bị đưa vào Recycle Bin. Hãy kiểm tra danh sách trước khi xác nhận.";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
