using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhotoReview.App;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Recovery window: the live check runs when the window opens and the details panel shows source and destination.
/// Real temp files + the physical file system; the window is never shown (RunChecksAsync is what Loaded triggers).
/// </summary>
[Collection("GlobalState")]
public sealed class RecoveryWindowTests
{
    private static readonly DateTime Stamp = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    /// <summary>Entries in several verdict states; index order: CanRetry, AlreadyDone, Lost, Conflict, RecycleUnverifiable.</summary>
    internal static List<JournalEntry> SampleEntries(string root)
    {
        static string Make(string path, int size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, Stamp);
            return path;
        }

        JournalEntry Entry(string id, FileOperationType type, JournalState state, string src, string? dst, int size, string? error = null, string? code = null) =>
            new(id, type, state, src, dst, size, Stamp, Stamp, error, code);

        var retrySrc = Make(Path.Combine(root, "shots", "retry-me.jpg"), 2048);
        var doneDst = Make(Path.Combine(root, "keep", "already-there.jpg"), 4096);
        var conflictSrc = Make(Path.Combine(root, "shots", "conflict.jpg"), 1000);
        var conflictDst = Make(Path.Combine(root, "keep", "conflict.jpg"), 1000);
        return
        [
            Entry("1", FileOperationType.Move, JournalState.Failed, retrySrc, Path.Combine(root, "gone-folder", "retry-me.jpg"), 2048, "The process cannot access the file"),
            Entry("2", FileOperationType.Move, JournalState.Prepared, Path.Combine(root, "shots", "already-there.jpg"), doneDst, 4096, "Pending operation could not be confirmed", JournalErrors.PendingUnconfirmed),
            Entry("3", FileOperationType.Copy, JournalState.Failed, Path.Combine(root, "shots", "vanished.jpg"), Path.Combine(root, "keep", "vanished.jpg"), 3000),
            Entry("4", FileOperationType.Move, JournalState.Failed, conflictSrc, conflictDst, 1000),
            Entry("5", FileOperationType.Recycle, JournalState.Prepared, Path.Combine(root, "shots", "recycled.jpg"), null, 500),
        ];
    }

    [Fact]
    public async Task OpeningCheck_FillsVerdicts_AndDetailsPanelShowsSourceAndDestination()
    {
        using var temp = new PhotoReview.TestSupport.TempRoot("recovery-window");
        var entries = SampleEntries(temp.Path);
        await StaTestHost.RunAsync(async () =>
        {
            var window = new RecoveryWindow(entries, retry: _ => throw new InvalidOperationException("must not retry"), dismiss: _ => { });
            Assert.All(window.VisibleRows, row => Assert.Null(row.Check)); // not checked yet: "checking" state

            await window.RunChecksAsync();

            Assert.Equal(
                [RecoveryVerdict.CanRetry, RecoveryVerdict.AlreadyDone, RecoveryVerdict.Lost, RecoveryVerdict.Conflict, RecoveryVerdict.RecycleUnverifiable],
                window.VisibleRows.Select(r => r.Check!.Verdict).ToArray());

            window.SelectRow(0);
            Assert.Equal(entries[0].Source, window.SourceDetails.CurrentPath);
            Assert.Equal(entries[0].Destination, window.DestinationDetails.CurrentPath);
            Assert.Equal(Visibility.Visible, window.DestinationDetails.Visibility);
            Assert.Equal(Visibility.Visible, window.DetailsScroll.Visibility);
            Assert.True(window.RetryButton.IsEnabled);
            Assert.True(window.ClearSelectedButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.ErrorText.Visibility);
            Assert.Contains("cannot access", window.ErrorText.Text, StringComparison.Ordinal);

            window.SelectRow(1); // already done: dismiss only
            Assert.False(window.RetryButton.IsEnabled);
            Assert.True(window.ClearSelectedButton.IsEnabled);

            window.SelectRow(4); // recycle: no destination
            Assert.Equal(Visibility.Collapsed, window.DestinationDetails.Visibility);
            Assert.Equal(Visibility.Visible, window.NoDestinationText.Visibility);
            Assert.False(window.RetryButton.IsEnabled);

            window.Close();
        });
    }

    [Fact]
    public async Task Retry_DoesNotBlockDispatcher()
    {
        using var temp = new PhotoReview.TestSupport.TempRoot("recovery-retry-async");
        var entries = SampleEntries(temp.Path);
        await StaTestHost.RunAsync(async () =>
        {
            var gate = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new RecoveryWindow(entries, retry: _ => gate.Task, dismiss: _ => { });
            await window.RunChecksAsync();
            window.SelectRow(0);
            Assert.True(window.RetryButton.IsEnabled);

            var retry = window.ExecuteRetryAsync(entries[0]);

            // The dispatcher must still run queued work while the retry is pending (a blocking wait would hang here).
            var posted = false;
            await StaTestHost.Dispatcher.InvokeAsync(() => posted = true);
            Assert.True(posted);
            Assert.False(retry.IsCompleted);
            Assert.False(window.RetryButton.IsEnabled);

            var expected = new RecoveryRetryResult(true, "ok", null);
            gate.SetResult(expected);
            Assert.Same(expected, await retry);
            Assert.True(window.RetryButton.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public async Task VerdictFilter_ShowsOnlyMatchingEntries()
    {
        using var temp = new PhotoReview.TestSupport.TempRoot("recovery-filter");
        var entries = SampleEntries(temp.Path);
        await StaTestHost.RunAsync(async () =>
        {
            var window = new RecoveryWindow(entries);
            await window.RunChecksAsync();
            Assert.Equal(5, window.VisibleRows.Count);

            window.FilterCombo.SelectedIndex = 1; // Can retry
            Assert.Equal(["1"], window.VisibleRows.Select(r => r.Entry.Id).ToArray());
            window.FilterCombo.SelectedIndex = 2; // Already done
            Assert.Equal(["2"], window.VisibleRows.Select(r => r.Entry.Id).ToArray());
            window.FilterCombo.SelectedIndex = 3; // Problems
            Assert.Equal(["3", "4", "5"], window.VisibleRows.Select(r => r.Entry.Id).ToArray());
            window.FilterCombo.SelectedIndex = 0;
            Assert.Equal(5, window.VisibleRows.Count);
            window.Close();
        });
    }

    [Fact]
    public async Task Check_DoesNotModifyAnyFile()
    {
        using var temp = new PhotoReview.TestSupport.TempRoot("recovery-readonly");
        var entries = SampleEntries(temp.Path);
        var before = Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        await StaTestHost.RunAsync(async () =>
        {
            var window = new RecoveryWindow(entries);
            await window.RunChecksAsync();
            await window.RunChecksAsync(); // Re-check
            window.Close();
        });
        var after = Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after);
    }
}
