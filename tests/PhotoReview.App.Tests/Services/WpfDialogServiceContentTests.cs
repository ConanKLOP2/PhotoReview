using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Services;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// Complements <see cref="WpfDialogHostSeamTests"/>: what each <see cref="WpfDialogService"/> call hands to the window it
/// opens (items, folder, entries), the unreadable-journal path through the service, and the current behaviour when the
/// owner is a main window that was never shown (the known leftover in docs/ACTIVE-TASKS.md: not guarded, so the window
/// initializer's Owner assignment throws and nothing is opened).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WpfDialogServiceContentTests
{
    private sealed class FakeHost : IDialogHost
    {
        public Window? Owner { get; set; }
        public List<(Window? Owner, string Message, string Title, MessageBoxButton Button, MessageBoxImage Image)> Boxes { get; } = [];
        public List<Window> Dialogs { get; } = [];
        public List<Window> Shown { get; } = [];

        public MessageBoxResult ShowMessageBox(Window? owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            Boxes.Add((owner, message, title, button, image));
            return MessageBoxResult.OK;
        }

        public string? PickFolder(Window? owner, string title, string? initialFolder) => null;

        public bool? ShowDialog(Window window)
        {
            Dialogs.Add(window);
            return null;
        }

        public void Show(Window window) => Shown.Add(window);
    }

    private static void EnsureResourceAssembly()
    {
        try
        {
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
        }
        catch
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?.SetValue(null, typeof(MainWindow).Assembly);
        }
    }

    private static WpfDialogService Create(FakeHost host, IServiceProvider? services = null)
        => new(services ?? new ServiceCollection().BuildServiceProvider(), host);

    private static ListBox FindListBox(DependencyObject root)
    {
        if (root is ListBox match) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (TryFindListBox(child) is { } found) return found;
        }
        throw new InvalidOperationException("no ListBox");
    }

    private static ListBox? TryFindListBox(DependencyObject root)
    {
        if (root is ListBox match) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (TryFindListBox(child) is { } found) return found;
        }
        return null;
    }

    [Fact]
    public void ShowBatchReview_WithSizedItems_HandsTheItemsToTheWindow()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var host = new FakeHost();
            var items = new[] { new BatchReviewItem(@"C:\a\one.jpg", 1234), new BatchReviewItem(@"C:\a\two.jpg", null) };

            Create(host).ShowBatchReview(items);

            var window = (BatchReviewWindow)Assert.Single(host.Dialogs);
            Assert.Equal(Tr.BatchReviewSummary(2), window.SummaryText.Text);
            Assert.Equal(items.Select(BatchReviewWindow.BuildItem), window.FilesList.ItemsSource.Cast<object>().Select(o => o.ToString()));
        });
    }

    [Fact]
    public void ShowBatchReview_WithPlainPaths_ListsEveryPathAsAnItemWithoutASize()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var host = new FakeHost();

            string[] paths = [@"C:\a\one.jpg", @"C:\a\two.jpg"];

            Create(host).ShowBatchReview(paths);

            var window = (BatchReviewWindow)Assert.Single(host.Dialogs);
            Assert.Equal(Tr.BatchReviewSummary(2), window.SummaryText.Text);
            Assert.Equal(
                paths.Select(p => BatchReviewWindow.BuildItem(new BatchReviewItem(p, null))),
                window.FilesList.ItemsSource.Cast<object>().Select(o => o.ToString()));
        });
    }

    [Fact]
    public void ShowSkippedFiles_ListsEveryEntry()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var host = new FakeHost();
            var entries = new[]
            {
                new SkippedEntry(@"C:\x.jpg", "locked"),
                new SkippedEntry(@"C:\dir", "gone", SkippedKind.ListingInterrupted),
            };

            Create(host).ShowSkippedFiles(entries);

            var window = Assert.Single(host.Dialogs);
            var list = FindListBox(window);
            Assert.Equal(entries.Select(SkippedFilesWindow.FormatEntry), list.ItemsSource.Cast<string>());
        });
    }

    [Fact]
    public void ShowBenchmark_StartsInTheGivenFolder_OrThePicturesFolder()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var withFolder = new FakeHost();
            var without = new FakeHost();

            Create(withFolder).ShowBenchmark(@"D:\bench");
            Create(without).ShowBenchmark();

            Assert.Equal(@"D:\bench", ((BenchmarkWindow)Assert.Single(withFolder.Shown)).FolderText.Text);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ((BenchmarkWindow)Assert.Single(without.Shown)).FolderText.Text);
        });
    }

    [Fact]
    public void ShowRecovery_WithoutAJournal_OpensNothingAndShowsNoError()
    {
        var host = new FakeHost();

        Create(host).ShowRecovery();

        Assert.Empty(host.Dialogs);
        Assert.Empty(host.Boxes);
    }

    [Fact]
    public void ShowRecovery_WhenTheJournalCannotBeRead_ReportsItInsteadOfOpeningTheWindow()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var dir = Path.Combine(Path.GetTempPath(), "PhotoReview_RecoveryLocked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var paths = new AppPaths(dir);
                var journal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock());
                var services = new ServiceCollection().AddSingleton(journal).BuildServiceProvider();
                var host = new FakeHost();
                Directory.CreateDirectory(Path.GetDirectoryName(paths.JournalFile)!);
                using (new FileStream(paths.JournalFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    Create(host, services).ShowRecovery();
                }

                Assert.Empty(host.Dialogs);
                var box = Assert.Single(host.Boxes);
                Assert.Equal(Tr.RecoveryTitle, box.Title);
                Assert.Equal((MessageBoxButton.OK, MessageBoxImage.Warning), (box.Button, box.Image));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        });
    }

    [Fact]
    public void ShowSkippedFiles_WithAnOwnerThatWasNeverShown_ThrowsAndOpensNothing()
    {
        // Pins the current (unguarded) behaviour: WPF refuses an Owner that was never shown. Reachability is unconfirmed
        // (docs/ACTIVE-TASKS.md, error-handling review leftovers); a future guard should change this test on purpose.
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var host = new FakeHost { Owner = new Window() };

            Assert.Throws<InvalidOperationException>(() => Create(host).ShowSkippedFiles([new SkippedEntry(@"C:\x.jpg", "locked")]));

            Assert.Empty(host.Dialogs);
        });
    }
}
