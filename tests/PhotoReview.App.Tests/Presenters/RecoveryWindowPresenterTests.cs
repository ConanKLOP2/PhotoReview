using System.IO;
using PhotoReview.App.Presenters;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>
/// WP-35 G0 (PR 2): the Recovery window's behaviour without a window. The live check is read-only; Retry runs once through
/// the supplied delegate; Clear removes only what the journal accepted or reported stale; the window may not close mid-retry.
/// </summary>
[Collection("GlobalState")]
public sealed class RecoveryWindowPresenterTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JournalEntry Entry(string id, FileOperationType type = FileOperationType.Move, string? source = null, string? destination = null,
        JournalState state = JournalState.Failed, long size = 10, string? errorCode = null, IReadOnlyList<JournalGroupMember>? members = null) =>
        new(id, type, state, source ?? Path.Combine(Path.GetTempPath(), "wp35g0-missing", id + ".jpg"), destination, size, Stamp, Stamp,
            ErrorCode: errorCode, GroupMembers: members);

    private static RecoveryCheckResult Check(JournalEntry entry, RecoveryVerdict verdict, RecoveryPathCheck? destination = null,
        IReadOnlyList<RecoveryGroupMemberCheck>? members = null) =>
        new(entry, new RecoveryPathCheck(entry.Source, RecoveryPathStatus.Exists, entry.Size, Stamp, true), destination, verdict, members);

    private static RecoveryRetryResult Ok() => new(Succeeded: true, Message: "done", Entry: null);

    private sealed class FakeView : IRecoveryCheckView
    {
        public List<string> Status { get; } = [];
        public List<bool> RecheckEnabled { get; } = [];
        public int Completed { get; private set; }
        public void SetCheckStatus(string text) => Status.Add(text);
        public void SetRecheckEnabled(bool enabled) => RecheckEnabled.Add(enabled);
        public void CheckCompleted() => Completed++;
    }

    /// <summary>Runs <see cref="Progress{T}"/> callbacks inline, so a test can observe them without a UI thread.</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static async Task RunInline(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try { await body(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // ---- List ----

    [Fact(DisplayName = "Rows start unchecked: they match 'All' only, the summary counts them and an empty filter result is flagged")]
    public void List_UncheckedRows_MatchAllOnly()
    {
        var presenter = new RecoveryWindowPresenter([Entry("1"), Entry("2")]);

        Assert.Equal(2, presenter.Shown(RecoveryFilter.All).Count);
        Assert.Empty(presenter.Shown(RecoveryFilter.CanRetry));
        Assert.Empty(presenter.Shown(RecoveryFilter.Problems));
        Assert.Equal(Tr.RecoverySummaryCount(2), presenter.SummaryText);
        Assert.True(presenter.FilterEmptyVisible(0));
        Assert.False(presenter.FilterEmptyVisible(2));
        Assert.False(new RecoveryWindowPresenter([]).FilterEmptyVisible(0));
        Assert.Equal(Tr.RecoverySummaryEmpty, new RecoveryWindowPresenter([]).SummaryText);
    }

    [Fact(DisplayName = "The filter follows the verdict of each row")]
    public void List_FilterFollowsVerdicts()
    {
        var presenter = new RecoveryWindowPresenter([Entry("1"), Entry("2"), Entry("3")]);
        presenter.Rows[0].SetResult(Check(presenter.Rows[0].Entry, RecoveryVerdict.CanRetry));
        presenter.Rows[1].SetResult(Check(presenter.Rows[1].Entry, RecoveryVerdict.AlreadyDone));
        presenter.Rows[2].SetResult(Check(presenter.Rows[2].Entry, RecoveryVerdict.Lost));

        Assert.Equal(["1"], presenter.Shown(RecoveryFilter.CanRetry).Select(r => r.Entry.Id).ToArray());
        Assert.Equal(["2"], presenter.Shown(RecoveryFilter.AlreadyDone).Select(r => r.Entry.Id).ToArray());
        Assert.Equal(["3"], presenter.Shown(RecoveryFilter.Problems).Select(r => r.Entry.Id).ToArray());
        Assert.Equal(3, presenter.Shown(RecoveryFilter.All).Count);
    }

    [Fact(DisplayName = "A row shows 'checking' with a neutral tone until its result arrives, then the verdict text and tone; a change is announced")]
    public void Row_BadgeFollowsResult()
    {
        var row = new RecoveryRowModel(Entry("1"));
        var changes = 0;
        row.ResultChanged += () => changes++;
        Assert.Equal(Tr.RecoveryRowChecking, row.BadgeText);
        Assert.Equal(RecoveryTone.Neutral, row.BadgeTone);

        row.SetResult(Check(row.Entry, RecoveryVerdict.CanRetry));

        Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry), row.BadgeText);
        Assert.Equal(RecoveryTone.Good, row.BadgeTone);
        Assert.Equal(1, changes);
    }

    [Fact(DisplayName = "A group row is titled with its name and member count; a plain row with the file name")]
    public void Row_Titles()
    {
        var plain = new RecoveryRowModel(Entry("1", source: @"C:\a\one.jpg"));
        Assert.Equal("one.jpg", plain.Title);

        var group = new RecoveryRowModel(Entry("2", source: @"C:\a\two.jpg",
            members: [new JournalGroupMember(@"C:\a\two.jpg", null, 1, Stamp), new JournalGroupMember(@"C:\a\two.raw", null, 1, Stamp)]));
        Assert.Equal(Tr.RecoveryGroupTitle("two.jpg", 2), group.Title);
    }

    // ---- Buttons ----

    [Fact(DisplayName = "Retry needs a retry delegate and a retryable verdict; Clear needs a dismiss delegate and a selection; nothing works mid-retry")]
    public async Task Buttons_TruthTable()
    {
        var noDelegates = new RecoveryWindowPresenter([Entry("1")]);
        noDelegates.Rows[0].SetResult(Check(noDelegates.Rows[0].Entry, RecoveryVerdict.CanRetry));
        Assert.Equal((false, false, false), noDelegates.ButtonStates(noDelegates.Rows[0], 1));

        var gate = new TaskCompletionSource<RecoveryRetryResult>();
        var presenter = new RecoveryWindowPresenter([Entry("1"), Entry("2")], retry: _ => gate.Task,
            dismiss: entries => new DismissOutcome(entries, []));
        presenter.Rows[0].SetResult(Check(presenter.Rows[0].Entry, RecoveryVerdict.CanRetry));
        presenter.Rows[1].SetResult(Check(presenter.Rows[1].Entry, RecoveryVerdict.Conflict));

        Assert.Equal((true, true, true), presenter.ButtonStates(presenter.Rows[0], 1));
        Assert.Equal((false, true, true), presenter.ButtonStates(presenter.Rows[1], 1));   // not a retry verdict
        Assert.Equal((false, false, true), presenter.ButtonStates(null, 0));               // nothing selected
        Assert.Equal((false, true, true), presenter.ButtonStates(null, 2));                // several selected: no single row

        var busy = new List<bool>();
        var running = presenter.ExecuteRetryAsync(presenter.Rows[0].Entry, busy.Add, () => { });
        Assert.True(presenter.IsRetrying);
        Assert.False(presenter.CanClose);
        Assert.Equal((false, false, false), presenter.ButtonStates(presenter.Rows[0], 1));
        Assert.False(presenter.CanStartRetry(presenter.Rows[0]));

        gate.SetResult(Ok());
        await running;
        Assert.False(presenter.IsRetrying);
        Assert.True(presenter.CanClose);
        Assert.Equal([true, false], busy);
    }

    // ---- Details ----

    [Fact(DisplayName = "Details of an unchecked row show its journal data with the 'checking' text and no group list")]
    public void Details_Unchecked()
    {
        var row = new RecoveryRowModel(Entry("1", source: @"C:\a\x.jpg", destination: @"D:\b\x.jpg", errorCode: JournalErrors.PendingUnconfirmed));

        var details = RecoveryWindowPresenter.Details(row);

        Assert.Equal(Tr.RecoveryRowChecking, details.VerdictText);
        Assert.Equal(Tr.RecoveryDetailChecking, details.ExplainText);
        Assert.Equal(string.Empty, details.ActionText);
        Assert.True(details.SourceVisible);
        Assert.Equal(@"C:\a\x.jpg", details.Source!.Path);
        Assert.True(details.DestinationVisible);
        Assert.Equal(@"D:\b\x.jpg", details.Destination!.Path);
        Assert.False(details.NoDestinationVisible);
        Assert.Null(details.GroupMembers);
        Assert.True(details.ErrorVisible);
        Assert.False(string.IsNullOrWhiteSpace(details.ErrorText));
    }

    [Fact(DisplayName = "Details of an unchecked row without a destination show the 'no destination' text instead of a panel")]
    public void Details_UncheckedWithoutDestination()
    {
        var details = RecoveryWindowPresenter.Details(new RecoveryRowModel(Entry("1", FileOperationType.Recycle, destination: null)));

        Assert.False(details.DestinationVisible);
        Assert.Null(details.Destination);
        Assert.True(details.NoDestinationVisible);
    }

    [Fact(DisplayName = "Details of a checked plain row carry verdict, explanation, action and both path views")]
    public void Details_CheckedPlainRow()
    {
        var entry = Entry("1", source: @"C:\a\x.jpg", destination: @"D:\gone\x.jpg");
        var destination = new RecoveryPathCheck(@"D:\gone\x.jpg", RecoveryPathStatus.Missing, null, null, FolderExists: false);
        var row = new RecoveryRowModel(entry);
        row.SetResult(Check(entry, RecoveryVerdict.CanRetry, destination));

        var details = RecoveryWindowPresenter.Details(row);

        Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry), details.VerdictText);
        Assert.Equal(RecoveryTone.Good, details.VerdictTone);
        Assert.Equal(RecoveryPresentation.ExplainText(RecoveryVerdict.CanRetry), details.ExplainText);
        Assert.Equal(RecoveryPresentation.ActionText(RecoveryVerdict.CanRetry), details.ActionText);
        Assert.Equal(Tr.RecoveryDetailFolderMissing, details.Destination!.Note);   // the folder is gone: said so
        Assert.Null(details.Source!.Note);
        Assert.True(details.SourceVisible);
        Assert.True(details.DestinationVisible);
        Assert.False(details.NoDestinationVisible);
    }

    [Fact(DisplayName = "Details of a checked entry without a destination hide the destination panel and show the 'no destination' text")]
    public void Details_CheckedWithoutDestination()
    {
        var entry = Entry("1", FileOperationType.Recycle);
        var row = new RecoveryRowModel(entry);
        row.SetResult(Check(entry, RecoveryVerdict.NotRecycled));

        var details = RecoveryWindowPresenter.Details(row);

        Assert.False(details.DestinationVisible);
        Assert.True(details.NoDestinationVisible);
        Assert.Equal(RecoveryTone.Warn, details.VerdictTone);
    }

    [Fact(DisplayName = "Details of a checked group list the members and hide both path panels and the 'no destination' text")]
    public void Details_CheckedGroup()
    {
        var m1 = new JournalGroupMember(@"C:\a\1.jpg", @"D:\b\1.jpg", 1, Stamp);
        var m2 = new JournalGroupMember(@"C:\a\2.jpg", null, 1, Stamp);
        var entry = Entry("g", source: @"C:\a\1.jpg", destination: @"D:\b\1.jpg", members: [m1, m2]);
        var memberChecks = new[]
        {
            new RecoveryGroupMemberCheck(m1, Check(entry, RecoveryVerdict.CanRetry, new RecoveryPathCheck(m1.Destination!, RecoveryPathStatus.Missing, null, null, true))),
            new RecoveryGroupMemberCheck(m2, Check(entry, RecoveryVerdict.AlreadyDone)),
        };
        var row = new RecoveryRowModel(entry);
        row.SetResult(Check(entry, RecoveryVerdict.CanRetry, members: memberChecks));

        var details = RecoveryWindowPresenter.Details(row);

        Assert.Equal(Tr.RecoveryGroupMembers(2), details.GroupHeading);
        Assert.Equal(2, details.GroupMembers!.Count);
        Assert.Equal(@"C:\a\1.jpg", details.GroupMembers[0].Source);
        Assert.Equal(m1.Destination, details.GroupMembers[0].Destination);
        Assert.Equal(Tr.RecoveryDetailNoDestination, details.GroupMembers[1].Destination);
        Assert.False(details.SourceVisible);
        Assert.False(details.DestinationVisible);
        Assert.False(details.NoDestinationVisible);
    }

    // ---- Live check ----

    [Fact(DisplayName = "The live check reports every row, ends with the 'done' line, re-enables Re-check and asks the view to refresh; no file changes")]
    public async Task RunChecks_ReportsEveryRow_AndWritesNothing()
    {
        using var temp = new TempRoot("wp35g0-recovery");
        var source = temp.File(@"shots\retry.jpg", 1, 2, 3);
        var destinationFolder = temp.Dir("keep");
        var destination = Path.Combine(destinationFolder, "retry.jpg");
        var before = Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).Order().ToList();
        var entry = new JournalEntry("1", FileOperationType.Move, JournalState.Failed, source, destination, 3, File.GetLastWriteTimeUtc(source), Stamp);
        var missing = Entry("2", source: Path.Combine(temp.Path, "nothing.jpg"), destination: Path.Combine(destinationFolder, "nothing.jpg"));
        var presenter = new RecoveryWindowPresenter([entry, missing]);
        var view = new FakeView();

        await RunInline(() => presenter.RunChecksAsync(view));

        Assert.Equal(RecoveryVerdict.CanRetry, presenter.Rows[0].Check!.Verdict);
        Assert.NotNull(presenter.Rows[1].Check);
        Assert.Equal([false, true], view.RecheckEnabled);
        Assert.Equal(Tr.RecoveryCheckProgress(0, 2), view.Status[0]);
        Assert.Contains(Tr.RecoveryCheckProgress(2, 2), view.Status);
        Assert.False(string.IsNullOrWhiteSpace(view.Status[^1]));
        Assert.NotEqual(Tr.RecoveryCheckProgress(2, 2), view.Status[^1]); // the last line is the "done" summary, not a progress line
        Assert.Equal(1, view.Completed);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact(DisplayName = "A check with no rows only clears the status line")]
    public async Task RunChecks_NoRows()
    {
        var presenter = new RecoveryWindowPresenter([]);
        var view = new FakeView();

        await RunInline(() => presenter.RunChecksAsync(view));

        Assert.Equal([string.Empty], view.Status);
        Assert.Empty(view.RecheckEnabled);
        Assert.Equal(0, view.Completed);
    }

    [Fact(DisplayName = "A check started again resets every row to 'checking' before the new results arrive")]
    public async Task RunChecks_ResetsRowsFirst()
    {
        var presenter = new RecoveryWindowPresenter([Entry("1")]);
        presenter.Rows[0].SetResult(Check(presenter.Rows[0].Entry, RecoveryVerdict.Lost));
        var sawChecking = false;
        presenter.Rows[0].ResultChanged += () => sawChecking |= presenter.Rows[0].Check is null;

        await RunInline(() => presenter.RunChecksAsync(new FakeView()));

        Assert.True(sawChecking);
    }

    // ---- Clear ----

    [Fact(DisplayName = "Clear asks only when it is available and something is selected")]
    public void Dismiss_PromptNeedsDelegateAndRows()
    {
        var without = new RecoveryWindowPresenter([Entry("1")]);
        Assert.Null(without.DismissPrompt(without.Rows.ToList()));

        var presenter = new RecoveryWindowPresenter([Entry("1"), Entry("2")], dismiss: e => new DismissOutcome(e, []));
        Assert.Null(presenter.DismissPrompt([]));
        Assert.Equal(Tr.RecoveryDismissConfirmMessage(2), presenter.DismissPrompt(presenter.Rows.ToList()));
    }

    [Fact(DisplayName = "Clear removes dismissed rows and rows skipped as stale, keeps the rest and reports the skipped count")]
    public void Dismiss_RemovesDismissedAndStale()
    {
        IReadOnlyList<JournalEntry>? received = null;
        var presenter = new RecoveryWindowPresenter([Entry("1"), Entry("2"), Entry("3")], dismiss: entries =>
        {
            received = entries;
            return new DismissOutcome([entries[0]], [entries[1]]);
        });

        var result = presenter.Dismiss([presenter.Rows[0], presenter.Rows[1]]);

        Assert.Null(result.Error);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(["1", "2"], received!.Select(e => e.Id).ToArray());
        Assert.Equal(["3"], presenter.Rows.Select(r => r.Entry.Id).ToArray());
    }

    [Fact(DisplayName = "A dismiss that throws removes nothing and returns the message")]
    public void Dismiss_Throws_RemovesNothing()
    {
        var presenter = new RecoveryWindowPresenter([Entry("1")], dismiss: _ => throw new IOException("journal locked"));

        var result = presenter.Dismiss(presenter.Rows.ToList());

        Assert.Equal("journal locked", result.Error);
        Assert.Single(presenter.Rows);
    }

    // ---- Retry ----

    [Fact(DisplayName = "Retry without a delegate is refused before anything runs")]
    public async Task Retry_WithoutDelegate_Throws()
    {
        var presenter = new RecoveryWindowPresenter([Entry("1")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ExecuteRetryAsync(presenter.Rows[0].Entry, _ => { }, () => { }));
        Assert.False(presenter.IsRetrying);
    }

    [Fact(DisplayName = "A retry that throws still releases the busy state, and the exception reaches the caller")]
    public async Task Retry_Throws_ReleasesBusyState()
    {
        var presenter = new RecoveryWindowPresenter([Entry("1")], retry: _ => throw new InvalidOperationException("nope"));
        var busy = new List<bool>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ExecuteRetryAsync(presenter.Rows[0].Entry, busy.Add, () => { }));

        Assert.False(presenter.IsRetrying);
        Assert.Equal([true, false], busy);
        Assert.Single(presenter.Rows);
    }

    [Fact(DisplayName = "A superseded retry drops the row and tells the view; a normal one keeps it")]
    public async Task Retry_Superseded_DropsTheRow()
    {
        var superseded = new RecoveryWindowPresenter([Entry("1"), Entry("2")],
            retry: _ => Task.FromResult(new RecoveryRetryResult(false, "someone else did it", null, Superseded: true)));
        var changed = 0;
        var result = await superseded.ExecuteRetryAsync(superseded.Rows[0].Entry, _ => { }, () => changed++);
        Assert.True(result.Superseded);
        Assert.Equal(1, changed);
        Assert.Equal(["2"], superseded.Rows.Select(r => r.Entry.Id).ToArray());

        var normal = new RecoveryWindowPresenter([Entry("1")], retry: _ => Task.FromResult(Ok()));
        changed = 0;
        await normal.ExecuteRetryAsync(normal.Rows[0].Entry, _ => { }, () => changed++);
        Assert.Equal(0, changed);
        Assert.Single(normal.Rows);
    }

    [Fact(DisplayName = "The retry result maps to its dialog: success closes, journal warning is a warning that still closes, refusal re-checks, stale is informational")]
    public void DescribeRetry_MapsEveryOutcome()
    {
        var success = RecoveryWindowPresenter.DescribeRetry(Ok());
        Assert.Equal(Tr.DialogRetrySucceededTitle, success.Title);
        Assert.False(success.IsWarning);
        Assert.True(success.CloseWindow);
        Assert.False(success.Recheck);

        var journalWarning = RecoveryWindowPresenter.DescribeRetry(new RecoveryRetryResult(true, "moved", null, JournalPersisted: false));
        Assert.Equal(Tr.DialogRetryDoneJournalFailedTitle, journalWarning.Title);
        Assert.True(journalWarning.IsWarning);
        Assert.True(journalWarning.CloseWindow);

        var refused = RecoveryWindowPresenter.DescribeRetry(new RecoveryRetryResult(false, "conflict", null));
        Assert.Equal(Tr.DialogRetryRejectedTitle, refused.Title);
        Assert.True(refused.IsWarning);
        Assert.False(refused.CloseWindow);
        Assert.True(refused.Recheck);

        var stale = RecoveryWindowPresenter.DescribeRetry(new RecoveryRetryResult(false, "gone", null, Superseded: true));
        Assert.Equal(Tr.RecoveryRetryStaleTitle, stale.Title);
        Assert.False(stale.IsWarning);
        Assert.False(stale.CloseWindow);
        Assert.False(stale.Recheck);
        Assert.Equal("gone", stale.Message);
    }

    [Fact(DisplayName = "The retry confirmation reads the permanent-delete setting at the moment it is asked")]
    public void RetryConfirm_ReadsSettingLazily()
    {
        var allow = false;
        var members = new[] { new JournalGroupMember(@"C:\a\1.jpg", null, 1, Stamp, Permanent: true) };
        var entry = Entry("g", FileOperationType.Recycle, source: @"C:\a\1.jpg", state: JournalState.Prepared, members: members);
        var presenter = new RecoveryWindowPresenter([entry], allowPermanentDelete: () => allow);

        var before = presenter.RetryConfirmText(presenter.Rows[0]);
        allow = true;
        var after = presenter.RetryConfirmText(presenter.Rows[0]);

        Assert.Equal(Tr.RecoveryGroupRetryConfirm(RecoveryPresentation.OperationText(FileOperationType.Recycle), 1), before);
        Assert.Equal(Tr.RecoveryGroupRetryConfirmPermanent(RecoveryPresentation.OperationText(FileOperationType.Recycle), 1, 1), after);
    }

    [Fact(DisplayName = "Tone colours are the documented hint values and every tone is distinct")]
    public void Tones_HaveDistinctDocumentedColours()
    {
        Assert.Equal(((byte)0x6C, (byte)0xCB, (byte)0x7A), RecoveryPresentation.Rgb(RecoveryTone.Good));
        Assert.Equal(((byte)0xB0, (byte)0xB0, (byte)0xB0), RecoveryPresentation.Rgb(RecoveryTone.Neutral));
        var all = Enum.GetValues<RecoveryTone>().Select(RecoveryPresentation.Rgb).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
