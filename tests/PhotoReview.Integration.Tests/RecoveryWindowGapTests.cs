using System.Reflection;
using System.IO;
using PhotoReview.App;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-T61 (re-check while a check runs) and RV-T64 (retry refused by the INV-4 file-action gate, through the real
/// <see cref="RecoveryRetryService"/>). The superseded-row and close-while-retrying scenarios of RV-T61 are already covered by
/// <see cref="RecoveryWindowTests"/>.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class RecoveryWindowGapTests
{
    /// <summary>Forwards everything to the real file system; <see cref="OnGetFileStat"/> may block the calling (worker) thread.</summary>
    public class HookedFileSystem : DispatchProxy
    {
        public IFileSystem Inner { get; set; } = null!;
        public Action<string>? OnGetFileStat { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFileSystem.GetFileStat)) OnGetFileStat?.Invoke((string)args![0]!);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException!; }
        }
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new InvalidOperationException("recycle must not be used");
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => throw new InvalidOperationException("recycle must not be used");
    }

    [Fact]
    public async Task Recheck_WhileACheckIsRunning_OldRunNeverReEnablesTheButtonOrOverwritesTheNewRun()
    {
        using var temp = new PhotoReview.TestSupport.TempRoot("recovery-recheck-overlap");
        var entries = RecoveryWindowTests.SampleEntries(temp.Path);
        var lastSource = entries[^1].Source; // the run blocks while checking the LAST row, so it finishes its loop once released
        var hits = 0;
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var firstBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = DispatchProxy.Create<IFileSystem, HookedFileSystem>();
        var hooked = (HookedFileSystem)(object)fs;
        hooked.Inner = new PhysicalFileSystem();
        hooked.OnGetFileStat = path =>
        {
            if (!string.Equals(path, lastSource, StringComparison.OrdinalIgnoreCase)) return;
            switch (Interlocked.Increment(ref hits))
            {
                case 1: firstBlocked.TrySetResult(); Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(20))); break;
                case 2: secondBlocked.TrySetResult(); Assert.True(releaseSecond.Wait(TimeSpan.FromSeconds(20))); break;
            }
        };
        await StaTestHost.RunAsync(async () =>
        {
            var window = new RecoveryWindow(entries, fileSystem: fs);
            var enabledTransitions = 0;
            window.RecheckButton.IsEnabledChanged += (_, e) => { if (e.NewValue is true) enabledTransitions++; };

            var first = window.RunChecksAsync();
            await firstBlocked.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(window.RecheckButton.IsEnabled);

            var second = window.RunChecksAsync(); // "Re-check" while the first run is still checking
            await secondBlocked.Task.WaitAsync(TimeSpan.FromSeconds(20));

            releaseFirst.Set(); // the superseded run now finishes its last row
            await first;
            Assert.False(window.RecheckButton.IsEnabled); // only the newest run may re-enable it
            Assert.Equal(0, enabledTransitions);

            releaseSecond.Set();
            await second;
            Assert.True(window.RecheckButton.IsEnabled);
            Assert.Equal(1, enabledTransitions); // exactly once
            Assert.All(window.VisibleRows, row => Assert.NotNull(row.Check));
            window.Close();
        });
    }

    [Fact]
    public async Task Retry_WhileFileActionGateIsHeld_ShowsBusyMessageAndLeavesTheRowAndFilesUnchanged()
    {
        using var temp = new PhotoReview.TestSupport.TempRoot("recovery-retry-busy");
        var entries = RecoveryWindowTests.SampleEntries(temp.Path);
        var failed = entries[0]; // Move, Failed, CanRetry: source exists, destination folder is gone
        var fs = new PhysicalFileSystem();
        var clock = new SystemClock();
        var journal = new OperationJournal(new AppPaths(temp.Path), fs, clock);
        journal.Append(failed);
        var gate = new FileActionService(journal, fs, clock, new NoRecycleBin());
        var retryService = new RecoveryRetryService(journal, fs, clock, fileActionGate: gate);
        Assert.True(gate.TryBegin()); // a Move/Copy/Delete is running
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var window = new RecoveryWindow(entries, retry: entry => retryService.RetryMoveOrCopyAsync(entry), dismiss: _ => new DismissOutcome([], []));
                await window.RunChecksAsync();
                window.SelectRow(0);
                Assert.True(window.RetryButton.IsEnabled);

                var result = await window.ExecuteRetryAsync(failed);

                Assert.False(result.Succeeded);
                Assert.False(result.Superseded);
                Assert.Equal(Tr.CoreRecoveryBusy, result.Message); // what Retry_Click shows in its dialog
                Assert.Contains(window.VisibleRows, row => row.Entry.Id == failed.Id); // row unchanged
                Assert.Equal(entries.Count, window.VisibleRows.Count);
                Assert.True(window.RetryButton.IsEnabled); // the window is usable again
                Assert.True(File.Exists(failed.Source));
                Assert.False(File.Exists(failed.Destination!));
                Assert.True(gate.IsBusy); // the running action still owns the gate
                window.Close();
            });
        }
        finally
        {
            gate.End();
        }
    }
}
