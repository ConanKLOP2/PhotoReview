using System.Windows.Media;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests;

/// <summary>
/// Recovery window text/colour mapping for the states the older presenter tests did not reach: changed/unreadable paths,
/// every capture-group member verdict, the single-file retry confirmation and the enum fallbacks.
/// </summary>
[Collection("GlobalState")] // Tr.* reads the ambient localizer
public sealed class RecoveryPresenterCoverageTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    private static RecoveryPathCheck PathCheck(RecoveryPathStatus status, long? size = 1, DateTime? writeUtc = null) =>
        new(@"C:\a.jpg", status, size, writeUtc ?? Stamp, true);

    private static JournalEntry Entry(FileOperationType type = FileOperationType.Move, string? errorCode = null) =>
        new("x", type, JournalState.Failed, @"C:\photos\a.jpg", @"D:\a.jpg", 1, Stamp, Stamp, ErrorCode: errorCode);

    [Theory]
    [InlineData(RecoveryPathStatus.Exists, 0x6C, 0xCB, 0x7A)]
    [InlineData(RecoveryPathStatus.Changed, 0xF0, 0xB0, 0x40)]
    [InlineData(RecoveryPathStatus.Missing, 0xB0, 0xB0, 0xB0)]
    [InlineData(RecoveryPathStatus.Unreadable, 0xFF, 0x6B, 0x6B)]
    public void PathView_ColoursAndWordsTheStatusOfThePath(RecoveryPathStatus status, byte r, byte g, byte b)
    {
        var view = RecoveryPresenter.PathView("Source", PathCheck(status), Entry(), folderMissingNote: false);

        Assert.Equal(Color.FromRgb(r, g, b), ColorOf(view.StatusBrush));
        Assert.Equal(Tr.RecoveryDetailStatus(RecoveryPresenter.PathStatusText(status)), view.StatusText);
        Assert.Equal("Source", view.Title);
        Assert.Equal(@"C:\a.jpg", view.Path);
        Assert.Null(view.Note);
    }

    [Fact]
    public void PathStatusText_GivesEachStatusItsOwnWording()
    {
        var texts = Enum.GetValues<RecoveryPathStatus>().Select(RecoveryPresenter.PathStatusText).ToList();

        Assert.Equal(Tr.RecoveryPathStatusExists, RecoveryPresenter.PathStatusText(RecoveryPathStatus.Exists));
        Assert.Equal(Tr.RecoveryPathStatusMissing, RecoveryPresenter.PathStatusText(RecoveryPathStatus.Missing));
        Assert.Equal(Tr.RecoveryPathStatusChanged, RecoveryPresenter.PathStatusText(RecoveryPathStatus.Changed));
        Assert.Equal(Tr.RecoveryPathStatusUnreadable, RecoveryPresenter.PathStatusText(RecoveryPathStatus.Unreadable));
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PathView_WithTheFolderMissingNote_CarriesTheFolderMissingText()
    {
        var view = RecoveryPresenter.PathView("Destination", PathCheck(RecoveryPathStatus.Missing, null, null), Entry(), folderMissingNote: true);

        Assert.Equal(Tr.RecoveryDetailFolderMissing, view.Note);
        Assert.Equal(Tr.RecoveryDetailNowMissing, view.NowText);
        Assert.Equal(RecoveryPresenter.JournalText(Entry()), view.JournalText);
    }

    [Fact]
    public void NowText_ForAnUnreadableFile_SaysItCouldNotBeRead_AndAChangedOneStillShowsItsSizeAndTime()
    {
        Assert.Equal(Tr.RecoveryDetailNowUnreadable, RecoveryPresenter.NowText(PathCheck(RecoveryPathStatus.Unreadable)));
        Assert.Equal(Tr.RecoveryDetailNowMissing, RecoveryPresenter.NowText(PathCheck(RecoveryPathStatus.Missing)));
        Assert.Equal(Tr.RecoveryDetailNowPresent(RecoveryPresenter.FormatSize(77), RecoveryPresenter.FormatTime(Stamp)),
            RecoveryPresenter.NowText(PathCheck(RecoveryPathStatus.Changed, 77)));
        Assert.NotEqual(Tr.RecoveryDetailNowUnreadable, Tr.RecoveryDetailNowMissing);
    }

    [Fact]
    public void NeutralBrush_IsTheFrozenGrayUsedForUncoloredStates()
    {
        Assert.Equal(Color.FromRgb(0xB0, 0xB0, 0xB0), ColorOf(RecoveryPresenter.NeutralBrush));
        Assert.True(RecoveryPresenter.NeutralBrush.IsFrozen);
    }

    [Theory]
    [InlineData(RecoveryVerdict.AlreadyDone)]
    [InlineData(RecoveryVerdict.CanRetry)]
    [InlineData(RecoveryVerdict.NotRecycled)]
    [InlineData(RecoveryVerdict.RecycleUnverifiable)]
    [InlineData(RecoveryVerdict.SourceChanged)]
    [InlineData(RecoveryVerdict.DestinationChanged)]
    [InlineData(RecoveryVerdict.Conflict)]
    [InlineData(RecoveryVerdict.Lost)]
    [InlineData(RecoveryVerdict.Unknown)]
    [InlineData(RecoveryVerdict.PermanentlyDeleted)]
    [InlineData(RecoveryVerdict.PartiallyPermanentlyDeleted)]
    public void GroupStatusText_PerVerdict_UsesThatVerdictsMemberWording(RecoveryVerdict verdict)
    {
        var member = new JournalGroupMember(@"C:\a.jpg", null, 1, Stamp);
        var expected = verdict switch
        {
            RecoveryVerdict.AlreadyDone => Tr.RecoveryGroupExists,
            RecoveryVerdict.CanRetry => Tr.RecoveryGroupSourceStatus,
            RecoveryVerdict.NotRecycled => Tr.RecoveryGroupOnDisk,
            RecoveryVerdict.RecycleUnverifiable => Tr.RecoveryGroupMissing,
            RecoveryVerdict.SourceChanged or RecoveryVerdict.DestinationChanged => Tr.RecoveryGroupChanged,
            RecoveryVerdict.Conflict => Tr.RecoveryGroupConflict,
            RecoveryVerdict.Lost => Tr.RecoveryGroupLost,
            RecoveryVerdict.Unknown => Tr.RecoveryGroupUnreadable,
            _ => RecoveryPresenter.VerdictText(verdict), // no member-specific wording: the plain verdict text
        };

        var text = RecoveryPresenter.GroupStatusText(verdict, member, PathCheck(RecoveryPathStatus.Exists), null);

        Assert.Equal(expected, text); // a member without a destination shows only the state
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void GroupStatusText_MemberWordingDiffersBetweenTheDistinctStates()
    {
        var member = new JournalGroupMember(@"C:\a.jpg", null, 1, Stamp);
        var texts = new[]
        {
            RecoveryVerdict.AlreadyDone, RecoveryVerdict.CanRetry, RecoveryVerdict.NotRecycled, RecoveryVerdict.SourceChanged,
            RecoveryVerdict.Conflict, RecoveryVerdict.Lost, RecoveryVerdict.Unknown,
        }.Select(v => RecoveryPresenter.GroupStatusText(v, member, PathCheck(RecoveryPathStatus.Exists), null)).ToList();

        Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RetryConfirmText_SingleFileEntry_NamesTheOperationAndTheFileNameOnly()
    {
        var text = RecoveryPresenter.RetryConfirmText(Entry(FileOperationType.Copy), null, allowPermanentDelete: false);

        Assert.Equal(Tr.DialogConfirmRetryMessage(Tr.EnumFileOperationCopy, "a.jpg"), text);
        Assert.DoesNotContain(@"C:\photos", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryConfirmText_CancelledSingleFileEntry_SaysARetryFinishesIt()
    {
        var text = RecoveryPresenter.RetryConfirmText(Entry(FileOperationType.Move, JournalErrors.CancelledByUser), null, allowPermanentDelete: true);

        Assert.Equal(Tr.RecoveryRetryConfirmCancelled(Tr.EnumFileOperationMove, 1), text);
    }

    [Fact]
    public void RetryConfirmText_NullEntry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RecoveryPresenter.RetryConfirmText(null!, null, allowPermanentDelete: false));
    }

    [Fact]
    public void OperationText_NamesEachOperation_AndFallsBackToTheEnumNameForUnknownOnes()
    {
        Assert.Equal(Tr.EnumFileOperationMove, RecoveryPresenter.OperationText(FileOperationType.Move));
        Assert.Equal(Tr.EnumFileOperationCopy, RecoveryPresenter.OperationText(FileOperationType.Copy));
        Assert.Equal(Tr.EnumFileOperationRecycle, RecoveryPresenter.OperationText(FileOperationType.Recycle));
        Assert.Equal("99", RecoveryPresenter.OperationText((FileOperationType)99));
    }

    [Fact]
    public void StateText_NamesEachJournalState_AndFallsBackToTheEnumNameForUnknownOnes()
    {
        Assert.Equal(Tr.EnumJournalStatePrepared, RecoveryPresenter.StateText(JournalState.Prepared));
        Assert.Equal(Tr.EnumJournalStateCommitted, RecoveryPresenter.StateText(JournalState.Committed));
        Assert.Equal(Tr.EnumJournalStateFailed, RecoveryPresenter.StateText(JournalState.Failed));
        Assert.Equal("99", RecoveryPresenter.StateText((JournalState)99));
    }

    [Fact]
    public void NearestExistingFolder_APathTheFileSystemCannotParse_HasNoExistingFolder()
    {
        var asked = new List<string>();

        var result = RecoveryPresenter.NearestExistingFolder("   ", folder =>
        {
            asked.Add(folder);
            return true;
        });

        Assert.Null(result);
        Assert.Empty(asked);
    }

    [Fact]
    public void NearestExistingFolder_NullPredicate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RecoveryPresenter.NearestExistingFolder(@"C:\a.jpg", null!));
    }

    [Fact]
    public void FormatTime_TreatsTheStampAsUtcAndShowsLocalTime()
    {
        // A journal timestamp is UTC even when it was deserialized with another Kind: a Local kind must not be shifted twice.
        var asLocalKind = DateTime.SpecifyKind(Stamp, DateTimeKind.Local);
        var asUnspecifiedKind = DateTime.SpecifyKind(Stamp, DateTimeKind.Unspecified);

        Assert.Equal(RecoveryPresenter.FormatTime(Stamp), RecoveryPresenter.FormatTime(asLocalKind));
        Assert.Equal(RecoveryPresenter.FormatTime(Stamp), RecoveryPresenter.FormatTime(asUnspecifiedKind));
        Assert.Equal(Stamp.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), RecoveryPresenter.FormatTime(Stamp));
    }
}
