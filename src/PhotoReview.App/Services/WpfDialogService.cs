using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Services;

/// <summary>
/// Hiện thực IDialogService bằng hộp thoại WPF và Win32/WPF Window.
/// </summary>
public sealed class WpfDialogService(IServiceProvider serviceProvider) : IDialogService
{
    public bool ShowConfirmation(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is not null
            ? System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }

    public void ShowMessage(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null)
        {
            System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void ShowError(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null)
        {
            System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public string? PickFolder(string? initialFolder = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn folder ảnh" };
        if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;

        var owner = System.Windows.Application.Current?.MainWindow;
        var res = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return res == true ? dialog.FolderName : null;
    }

    public bool ShowBatchReview(IReadOnlyList<string> paths)
    {
        var window = new BatchReviewWindow(paths)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public void ShowRecovery()
    {
        var journal = serviceProvider.GetService<OperationJournal>();
        var retryService = serviceProvider.GetService<RecoveryRetryService>();
        if (journal is null) return;

        var entries = journal.ReadPendingOperations().Concat(journal.ReadFailedOperations()).ToList();
        Func<JournalEntry, RecoveryRetryResult>? retry = retryService is not null ? retryService.RetryMoveOrCopy : null;
        var window = new RecoveryWindow(entries, retry)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    public void ShowDiagnostics()
    {
        var metrics = serviceProvider.GetService<ReviewMetrics>();
        var snapshot = metrics?.Snapshot() ?? new ReviewMetrics().Snapshot();
        var window = new DiagnosticsWindow(snapshot)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    public bool ShowSettings()
    {
        var store = serviceProvider.GetService<SettingsStore>();
        if (store is null) return false;

        var decoderFactory = serviceProvider.GetService<IImageDecoderFactory>();
        var window = new SettingsWindow(store, decoderFactory)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public void ShowBenchmark(string? folder = null)
    {
        var window = new BenchmarkWindow(folder)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.Show();
    }
}
