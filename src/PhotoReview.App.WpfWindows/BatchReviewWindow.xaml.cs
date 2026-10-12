using System.Windows;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App;

/// <summary>Batch confirmation list, WPF view only (WP-35 G0): the lines come from <see cref="BatchReviewPresenter"/>.</summary>
public partial class BatchReviewWindow : Window
{
    /// <summary>Path-only form (sizes unknown: every line shows the "no longer exists" text). Production passes sizes.</summary>
    public BatchReviewWindow(IReadOnlyList<string> paths)
        : this(BatchReviewPresenter.FromPaths(paths))
    {
    }

    /// <summary>RV-A13: sizes come from the caller (the catalog scan already stat'ed every file); the UI thread never touches the disk here.</summary>
    public BatchReviewWindow(IReadOnlyList<BatchReviewItem> items)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        FilesList.ItemsSource = BatchReviewPresenter.BuildLines(items);
        SummaryText.Text = BatchReviewPresenter.Summary(items.Count);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}