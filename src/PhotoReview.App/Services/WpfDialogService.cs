using System.Windows;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

/// <summary>
/// Hiện thực IDialogService bằng hộp thoại WPF chuẩn (MessageBox).
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public bool ShowConfirmation(string title, string message)
    {
        var result = System.Windows.MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }

    public void ShowMessage(string title, string message)
    {
        System.Windows.MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
