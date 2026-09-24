using PhotoReview.App;
using PhotoReview.Core.FileActions;
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
}
