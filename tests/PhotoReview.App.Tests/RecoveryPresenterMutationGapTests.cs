using System.Windows.Media;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>Colour, size and group-status pins for <see cref="RecoveryPresenter"/> found by Stryker (mutation gaps).</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class RecoveryPresenterMutationGapTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    public static TheoryData<RecoveryVerdict, byte, byte, byte> VerdictColors => new()
    {
        { RecoveryVerdict.CanRetry, 0x6C, 0xCB, 0x7A },
        { RecoveryVerdict.AlreadyDone, 0x6F, 0xB1, 0xFF },
        { RecoveryVerdict.Conflict, 0xF0, 0xB0, 0x40 },
        { RecoveryVerdict.SourceChanged, 0xF0, 0xB0, 0x40 },
        { RecoveryVerdict.DestinationChanged, 0xF0, 0xB0, 0x40 },
        { RecoveryVerdict.NotRecycled, 0xF0, 0xB0, 0x40 },
        { RecoveryVerdict.PartiallyPermanentlyDeleted, 0xF0, 0xB0, 0x40 },
        { RecoveryVerdict.PermanentlyDeleted, 0xFF, 0x6B, 0x6B },
        { RecoveryVerdict.Lost, 0xFF, 0x6B, 0x6B },
        { RecoveryVerdict.RecycleUnverifiable, 0xB0, 0xB0, 0xB0 },
        { RecoveryVerdict.Unknown, 0xB0, 0xB0, 0xB0 },
    };

    [Theory]
    [MemberData(nameof(VerdictColors))]
    public void VerdictBrush_IsTheDocumentedHintColourAndIsFrozen(RecoveryVerdict verdict, byte r, byte g, byte b)
    {
        var brush = RecoveryPresenter.VerdictBrush(verdict);

        Assert.Equal(Color.FromRgb(r, g, b), ColorOf(brush));
        Assert.True(brush.IsFrozen); // shared statics: they must be usable from any thread
    }

    [Fact]
    public void VerdictBrush_CoversEveryVerdict()
    {
        var covered = VerdictColors.Select(row => (RecoveryVerdict)row[0]).ToHashSet();

        Assert.Equal(Enum.GetValues<RecoveryVerdict>().ToHashSet(), covered);
    }

    [Fact]
    public void AllowsRetry_ForAGroupRecycleEntry_IsTrueWhenItCanRetry()
    {
        var member = new JournalGroupMember(@"C:\a.jpg", null, 1, Stamp);
        var entry = new JournalEntry("g", FileOperationType.Recycle, JournalState.Failed, member.Source, null, 1, Stamp, Stamp,
            GroupId: "c", GroupMembers: [member]);
        var memberCheck = new RecoveryCheckResult(entry, new RecoveryPathCheck(member.Source, RecoveryPathStatus.Exists, 1, Stamp, true), null, RecoveryVerdict.CanRetry);
        var result = new RecoveryCheckResult(entry, new RecoveryPathCheck(member.Source, RecoveryPathStatus.Exists, 1, Stamp, true), null,
            RecoveryVerdict.CanRetry, [new RecoveryGroupMemberCheck(member, memberCheck)]);

        Assert.True(result.IsGroup);
        Assert.True(RecoveryPresenter.AllowsRetry(result));
    }

    [Fact]
    public void NowText_ForAPresentFile_ShowsItsCurrentSizeAndTime()
    {
        var check = new RecoveryPathCheck(@"C:\a.jpg", RecoveryPathStatus.Exists, 1234, Stamp, true);

        Assert.Equal(Tr.RecoveryDetailNowPresent(RecoveryPresenter.FormatSize(1234), RecoveryPresenter.FormatTime(Stamp)),
            RecoveryPresenter.NowText(check));
    }

    [Fact]
    public void GroupStatusText_WithADestination_ReportsTheDestinationsOwnStatus()
    {
        var member = new JournalGroupMember(@"C:\a.cr2", @"D:\a.cr2", 1, Stamp);
        var source = new RecoveryPathCheck(member.Source, RecoveryPathStatus.Missing, null, null, true);
        var destination = new RecoveryPathCheck(member.Destination!, RecoveryPathStatus.Exists, 1, Stamp, true);

        var text = RecoveryPresenter.GroupStatusText(RecoveryVerdict.AlreadyDone, member, source, destination);

        Assert.EndsWith($"{Tr.RecoveryDetailDestination}: {RecoveryPresenter.PathStatusText(RecoveryPathStatus.Exists)}", text, StringComparison.Ordinal);
        Assert.Contains($"{Tr.RecoveryDetailSource}: {RecoveryPresenter.PathStatusText(RecoveryPathStatus.Missing)}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupStatusText_WithADestinationButNoDestinationCheck_ReportsItMissing()
    {
        var member = new JournalGroupMember(@"C:\a.cr2", @"D:\a.cr2", 1, Stamp);
        var source = new RecoveryPathCheck(member.Source, RecoveryPathStatus.Exists, 1, Stamp, true);

        var text = RecoveryPresenter.GroupStatusText(RecoveryVerdict.CanRetry, member, source, null);

        Assert.EndsWith($"{Tr.RecoveryDetailDestination}: {RecoveryPresenter.PathStatusText(RecoveryPathStatus.Missing)}", text, StringComparison.Ordinal);
    }
}
