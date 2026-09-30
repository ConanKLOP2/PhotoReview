using PhotoReview.App;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests;

[Collection("GlobalState")]
public sealed class RecoveryPresenterTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RecoveryCheckResult Result(FileOperationType type, RecoveryVerdict verdict) =>
        new(new JournalEntry("x", type, JournalState.Failed, @"C:\a.jpg", @"D:\a.jpg", 1, Stamp, Stamp),
            new RecoveryPathCheck(@"C:\a.jpg", RecoveryPathStatus.Exists, 1, Stamp, true), null, verdict);

    [Fact]
    public void EveryVerdict_HasTextExplanationAndAction_AndKeysAreNotShownRaw()
    {
        foreach (var verdict in Enum.GetValues<RecoveryVerdict>())
        {
            foreach (var text in new[] { RecoveryPresenter.VerdictText(verdict), RecoveryPresenter.ExplainText(verdict), RecoveryPresenter.ActionText(verdict) })
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("recovery.", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Verdicts_AreDistinguishableWithoutColor()
    {
        var texts = Enum.GetValues<RecoveryVerdict>().Select(RecoveryPresenter.VerdictText).ToList();
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(RecoveryVerdict.CanRetry, FileOperationType.Move, true)]
    [InlineData(RecoveryVerdict.CanRetry, FileOperationType.Copy, true)]
    [InlineData(RecoveryVerdict.CanRetry, FileOperationType.Recycle, false)]
    [InlineData(RecoveryVerdict.AlreadyDone, FileOperationType.Move, false)]
    [InlineData(RecoveryVerdict.Conflict, FileOperationType.Move, false)]
    [InlineData(RecoveryVerdict.SourceChanged, FileOperationType.Move, false)]
    [InlineData(RecoveryVerdict.Unknown, FileOperationType.Move, false)]
    public void AllowsRetry_OnlyForCanRetryMoveCopy(RecoveryVerdict verdict, FileOperationType type, bool expected) =>
        Assert.Equal(expected, RecoveryPresenter.AllowsRetry(Result(type, verdict)));

    [Fact]
    public void AllowsRetry_WhileStillChecking_IsFalse() => Assert.False(RecoveryPresenter.AllowsRetry(null));

    [Theory]
    [InlineData(RecoveryFilter.All, null, true)]
    [InlineData(RecoveryFilter.All, RecoveryVerdict.Lost, true)]
    [InlineData(RecoveryFilter.CanRetry, RecoveryVerdict.CanRetry, true)]
    [InlineData(RecoveryFilter.CanRetry, RecoveryVerdict.AlreadyDone, false)]
    [InlineData(RecoveryFilter.AlreadyDone, RecoveryVerdict.AlreadyDone, true)]
    [InlineData(RecoveryFilter.AlreadyDone, RecoveryVerdict.Lost, false)]
    [InlineData(RecoveryFilter.Problems, RecoveryVerdict.Lost, true)]
    [InlineData(RecoveryFilter.Problems, RecoveryVerdict.Conflict, true)]
    [InlineData(RecoveryFilter.Problems, RecoveryVerdict.RecycleUnverifiable, true)]
    [InlineData(RecoveryFilter.Problems, RecoveryVerdict.CanRetry, false)]
    [InlineData(RecoveryFilter.Problems, RecoveryVerdict.AlreadyDone, false)]
    [InlineData(RecoveryFilter.Problems, null, false)]
    [InlineData(RecoveryFilter.CanRetry, null, false)]
    public void Matches_FollowsFilter(RecoveryFilter filter, RecoveryVerdict? verdict, bool expected) =>
        Assert.Equal(expected, RecoveryPresenter.Matches(filter, verdict));

    [Fact]
    public void NearestExistingFolder_WalksUpToFirstExistingAncestor()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\keep", @"D:\" };
        Assert.Equal(@"D:\keep", RecoveryPresenter.NearestExistingFolder(@"D:\keep\sub\deeper\a.jpg", existing.Contains));
        Assert.Equal(@"D:\", RecoveryPresenter.NearestExistingFolder(@"D:\other\a.jpg", existing.Contains));
        Assert.Null(RecoveryPresenter.NearestExistingFolder(@"E:\x\a.jpg", _ => false));
    }

    [Fact]
    public void PathView_ShowsPathStatusSizesAndFolderNote()
    {
        var entry = new JournalEntry("x", FileOperationType.Move, JournalState.Failed, @"C:\a.jpg", @"D:\gone\a.jpg", 1234, Stamp, Stamp);
        var missing = new RecoveryPathCheck(@"D:\gone\a.jpg", RecoveryPathStatus.Missing, null, null, FolderExists: false);
        var view = RecoveryPresenter.PathView("Destination", missing, entry, folderMissingNote: true);
        Assert.Equal(@"D:\gone\a.jpg", view.Path);
        Assert.Equal(RecoveryPresenter.PathStatusText(RecoveryPathStatus.Missing), view.StatusText.Split(':')[^1].Trim());
        Assert.Contains("1", view.JournalText, StringComparison.Ordinal);
        Assert.NotNull(view.Note);

        var present = new RecoveryPathCheck(@"C:\a.jpg", RecoveryPathStatus.Exists, 1234, Stamp, true);
        var presentView = RecoveryPresenter.PathView("Source", present, entry, folderMissingNote: false);
        Assert.Null(presentView.Note);
        Assert.Contains(RecoveryPresenter.FormatTime(Stamp), presentView.NowText, StringComparison.Ordinal);
    }

    private static RecoveryPathCheck Path(string path, RecoveryPathStatus status) =>
        new(path, status, status == RecoveryPathStatus.Missing ? null : 1, status == RecoveryPathStatus.Missing ? null : Stamp, true);

    [Fact]
    public void GroupStatusText_NotRecycledMember_SaysStillOnDiskNotMissing()
    {
        var member = new JournalGroupMember(@"C:.cr2", null, 1, Stamp);
        var text = RecoveryPresenter.GroupStatusText(RecoveryVerdict.NotRecycled, member, Path(member.Source, RecoveryPathStatus.Exists), null);

        Assert.Equal(Tr.RecoveryGroupOnDisk, text);
        Assert.NotEqual(Tr.RecoveryGroupMissing, text);
    }

    [Fact]
    public void GroupStatusText_RecycleUnverifiableMember_StaysMissing()
    {
        var member = new JournalGroupMember(@"C:.cr2", null, 1, Stamp);
        var text = RecoveryPresenter.GroupStatusText(RecoveryVerdict.RecycleUnverifiable, member, Path(member.Source, RecoveryPathStatus.Missing), null);

        Assert.Equal(Tr.RecoveryGroupMissing, text);
    }

    private static JournalEntry GroupDelete(bool undo, params JournalGroupMember[] members) =>
        new("g", FileOperationType.Recycle, JournalState.Failed, members[0].Source, null, members[0].Size, Stamp, Stamp,
            Undo: undo ? true : null, GroupId: "c", GroupMembers: members);

    private static RecoveryCheckResult CheckOf(JournalEntry entry, params (JournalGroupMember Member, RecoveryVerdict Verdict)[] members) =>
        new(entry, Path(entry.Source, RecoveryPathStatus.Exists), null, RecoveryVerdict.CanRetry,
            members.Select(item => new RecoveryGroupMemberCheck(item.Member, new RecoveryCheckResult(entry, Path(item.Member.Source, RecoveryPathStatus.Exists), null, item.Verdict))).ToArray());

    [Fact]
    public void RetryConfirmText_GroupDeleteWithPendingPermanentMemberAndSettingOn_WarnsItIsPermanent()
    {
        var jpeg = new JournalGroupMember(@"C:.jpg", null, 1, Stamp);
        var raw = new JournalGroupMember(@"E:.cr2", null, 1, Stamp, Permanent: true);
        var entry = GroupDelete(false, jpeg, raw);
        var check = CheckOf(entry, (jpeg, RecoveryVerdict.NotRecycled), (raw, RecoveryVerdict.NotRecycled));

        var text = RecoveryPresenter.RetryConfirmText(entry, check, allowPermanentDelete: true);

        Assert.Equal(Tr.RecoveryGroupRetryConfirmPermanent(RecoveryPresenter.OperationText(FileOperationType.Recycle), 2, 1), text);
    }

    [Fact]
    public void RetryConfirmText_GroupDeleteSettingOff_UsesThePlainGroupConfirmation()
    {
        var jpeg = new JournalGroupMember(@"C:.jpg", null, 1, Stamp);
        var raw = new JournalGroupMember(@"E:.cr2", null, 1, Stamp, Permanent: true);
        var entry = GroupDelete(false, jpeg, raw);
        var check = CheckOf(entry, (jpeg, RecoveryVerdict.NotRecycled), (raw, RecoveryVerdict.NotRecycled));

        Assert.Equal(Tr.RecoveryGroupRetryConfirm(RecoveryPresenter.OperationText(FileOperationType.Recycle), 2),
            RecoveryPresenter.RetryConfirmText(entry, check, allowPermanentDelete: false));
    }

    [Fact]
    public void RetryConfirmText_PermanentMemberAlreadyGone_DoesNotWarnAboutPermanentDeletion()
    {
        var jpeg = new JournalGroupMember(@"C:.jpg", null, 1, Stamp);
        var raw = new JournalGroupMember(@"E:.cr2", null, 1, Stamp, Permanent: true);
        var entry = GroupDelete(false, jpeg, raw);
        var check = CheckOf(entry, (jpeg, RecoveryVerdict.NotRecycled), (raw, RecoveryVerdict.PermanentlyDeleted)); // nothing left to delete for the RAW

        Assert.Equal(Tr.RecoveryGroupRetryConfirm(RecoveryPresenter.OperationText(FileOperationType.Recycle), 2),
            RecoveryPresenter.RetryConfirmText(entry, check, allowPermanentDelete: true));
    }

    [Fact]
    public void RetryConfirmText_UndoOfGroupDelete_UsesRestoreWordingNotDelete()
    {
        var jpeg = new JournalGroupMember(@"C:.jpg", null, 1, Stamp);
        var raw = new JournalGroupMember(@"C:.cr2", null, 1, Stamp);
        var entry = GroupDelete(true, jpeg, raw);

        var text = RecoveryPresenter.RetryConfirmText(entry, null, allowPermanentDelete: true);

        Assert.Equal(Tr.RecoveryGroupRetryConfirmRestore(2), text);
        Assert.DoesNotContain(RecoveryPresenter.OperationText(FileOperationType.Recycle), text, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryConfirmText_GroupMove_KeepsTheGenericGroupConfirmation()
    {
        var entry = new JournalEntry("g", FileOperationType.Move, JournalState.Failed, @"C:.jpg", @"C:\s.jpg", 1, Stamp, Stamp,
            GroupId: "c", GroupMembers: [new(@"C:.jpg", @"C:\s.jpg", 1, Stamp), new(@"C:.cr2", @"C:\s.cr2", 1, Stamp)]);

        Assert.Equal(Tr.RecoveryGroupRetryConfirm(RecoveryPresenter.OperationText(FileOperationType.Move), 2),
            RecoveryPresenter.RetryConfirmText(entry, null, allowPermanentDelete: true));
    }
}
