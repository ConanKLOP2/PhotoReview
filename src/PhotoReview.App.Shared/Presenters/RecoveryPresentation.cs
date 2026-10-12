using System.Globalization;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Presenters;

/// <summary>What the details panel shows for one path (source or destination).</summary>
internal sealed record RecoveryPathView(
    string Title,
    string Path,
    string StatusText,
    RecoveryTone StatusTone,
    string NowText,
    string JournalText,
    string? Note);

/// <summary>One capture-group member line of the details panel.</summary>
internal sealed record RecoveryGroupMemberView(string Status, string Source, string Destination);

/// <summary>
/// Pure mapping from <see cref="RecoveryCheckResult"/> to localized text and colour tones for the Recovery window (no UI
/// types involved, so it is unit-testable and shared by any view). WP-35 G0: moved out of the WPF assembly unchanged
/// except that a colour is a <see cref="RecoveryTone"/> the view turns into a brush.
/// </summary>
internal static class RecoveryPresentation
{
    /// <summary>The documented hint colour of a tone (0xRRGGBB): the view paints with exactly these values.</summary>
    public static (byte R, byte G, byte B) Rgb(RecoveryTone tone) => tone switch
    {
        RecoveryTone.Good => (0x6C, 0xCB, 0x7A),
        RecoveryTone.Info => (0x6F, 0xB1, 0xFF),
        RecoveryTone.Warn => (0xF0, 0xB0, 0x40),
        RecoveryTone.Bad => (0xFF, 0x6B, 0x6B),
        _ => (0xB0, 0xB0, 0xB0),
    };

    public static RecoveryTone VerdictTone(RecoveryVerdict verdict) => verdict switch
    {
        RecoveryVerdict.CanRetry => RecoveryTone.Good,
        RecoveryVerdict.AlreadyDone => RecoveryTone.Info,
        RecoveryVerdict.Conflict or RecoveryVerdict.SourceChanged or RecoveryVerdict.DestinationChanged or RecoveryVerdict.NotRecycled
            or RecoveryVerdict.PartiallyPermanentlyDeleted => RecoveryTone.Warn,
        RecoveryVerdict.PermanentlyDeleted or RecoveryVerdict.Lost => RecoveryTone.Bad,
        _ => RecoveryTone.Neutral,
    };

    public static string VerdictText(RecoveryVerdict verdict) => verdict switch
    {
        RecoveryVerdict.CanRetry => Tr.RecoveryVerdictCanRetry,
        RecoveryVerdict.AlreadyDone => Tr.RecoveryVerdictAlreadyDone,
        RecoveryVerdict.Conflict => Tr.RecoveryVerdictConflict,
        RecoveryVerdict.SourceChanged => Tr.RecoveryVerdictSourceChanged,
        RecoveryVerdict.DestinationChanged => Tr.RecoveryVerdictDestinationChanged,
        RecoveryVerdict.Lost => Tr.RecoveryVerdictLost,
        RecoveryVerdict.RecycleUnverifiable => Tr.RecoveryVerdictRecycleUnverifiable,
        RecoveryVerdict.NotRecycled => Tr.RecoveryVerdictNotRecycled,
        RecoveryVerdict.PermanentlyDeleted => Tr.RecoveryVerdictPermanentlyDeleted,
        RecoveryVerdict.PartiallyPermanentlyDeleted => Tr.RecoveryVerdictPartiallyPermanentlyDeleted,
        _ => Tr.RecoveryVerdictUnknown,
    };

    public static string ExplainText(RecoveryVerdict verdict) => verdict switch
    {
        RecoveryVerdict.CanRetry => Tr.RecoveryExplainCanRetry,
        RecoveryVerdict.AlreadyDone => Tr.RecoveryExplainAlreadyDone,
        RecoveryVerdict.Conflict => Tr.RecoveryExplainConflict,
        RecoveryVerdict.SourceChanged => Tr.RecoveryExplainSourceChanged,
        RecoveryVerdict.DestinationChanged => Tr.RecoveryExplainDestinationChanged,
        RecoveryVerdict.Lost => Tr.RecoveryExplainLost,
        RecoveryVerdict.RecycleUnverifiable => Tr.RecoveryExplainRecycleUnverifiable,
        RecoveryVerdict.NotRecycled => Tr.RecoveryExplainNotRecycled,
        RecoveryVerdict.PermanentlyDeleted => Tr.RecoveryExplainPermanentlyDeleted,
        RecoveryVerdict.PartiallyPermanentlyDeleted => Tr.RecoveryExplainPartiallyPermanentlyDeleted,
        _ => Tr.RecoveryExplainUnknown,
    };

    public static string ActionText(RecoveryVerdict verdict) => verdict switch
    {
        RecoveryVerdict.CanRetry => Tr.RecoveryActionCanRetry,
        RecoveryVerdict.AlreadyDone => Tr.RecoveryActionAlreadyDone,
        RecoveryVerdict.Conflict => Tr.RecoveryActionConflict,
        RecoveryVerdict.SourceChanged => Tr.RecoveryActionSourceChanged,
        RecoveryVerdict.DestinationChanged => Tr.RecoveryActionDestinationChanged,
        RecoveryVerdict.Lost => Tr.RecoveryActionLost,
        RecoveryVerdict.RecycleUnverifiable => Tr.RecoveryActionRecycleUnverifiable,
        RecoveryVerdict.NotRecycled => Tr.RecoveryActionNotRecycled,
        RecoveryVerdict.PermanentlyDeleted => Tr.RecoveryActionPermanentlyDeleted,
        RecoveryVerdict.PartiallyPermanentlyDeleted => Tr.RecoveryActionPartiallyPermanentlyDeleted,
        _ => Tr.RecoveryActionUnknown,
    };

    public static string PathStatusText(RecoveryPathStatus status) => status switch
    {
        RecoveryPathStatus.Exists => Tr.RecoveryPathStatusExists,
        RecoveryPathStatus.Missing => Tr.RecoveryPathStatusMissing,
        RecoveryPathStatus.Changed => Tr.RecoveryPathStatusChanged,
        _ => Tr.RecoveryPathStatusUnreadable,
    };

    public static RecoveryTone PathStatusTone(RecoveryPathStatus status) => status switch
    {
        RecoveryPathStatus.Exists => RecoveryTone.Good,
        RecoveryPathStatus.Changed => RecoveryTone.Warn,
        RecoveryPathStatus.Missing => RecoveryTone.Neutral,
        _ => RecoveryTone.Bad,
    };

    /// <summary>Only the retry verdict enables Retry; RecoveryRetryService still re-checks everything itself.</summary>
    public static bool AllowsRetry(RecoveryCheckResult? result) =>
        result is { Verdict: RecoveryVerdict.CanRetry }
        && (result.Entry.Type is FileOperationType.Move or FileOperationType.Copy
            || (result.IsGroup && result.Entry.Type == FileOperationType.Recycle));

    public static bool Matches(RecoveryFilter filter, RecoveryVerdict? verdict) => filter switch
    {
        RecoveryFilter.All => true,
        RecoveryFilter.CanRetry => verdict == RecoveryVerdict.CanRetry,
        RecoveryFilter.AlreadyDone => verdict == RecoveryVerdict.AlreadyDone,
        _ => verdict is not null and not (RecoveryVerdict.CanRetry or RecoveryVerdict.AlreadyDone),
    };

    public static string FormatSize(long bytes) =>
        Tr.RecoveryDetailBytes(bytes.ToString("N0", CultureInfo.CurrentCulture));

    public static string FormatTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string NowText(RecoveryPathCheck check) => check.Status switch
    {
        RecoveryPathStatus.Missing => Tr.RecoveryDetailNowMissing,
        RecoveryPathStatus.Unreadable => Tr.RecoveryDetailNowUnreadable,
        _ => Tr.RecoveryDetailNowPresent(FormatSize(check.CurrentSize ?? 0), FormatTime(check.CurrentLastWriteUtc ?? default)),
    };

    public static string JournalText(JournalEntry entry) =>
        Tr.RecoveryDetailJournal(FormatSize(entry.Size), FormatTime(entry.LastWriteUtc));

    public static RecoveryPathView PathView(string title, RecoveryPathCheck check, JournalEntry entry, bool folderMissingNote) =>
        new(title, check.Path, Tr.RecoveryDetailStatus(PathStatusText(check.Status)), PathStatusTone(check.Status),
            NowText(check), JournalText(entry), folderMissingNote ? Tr.RecoveryDetailFolderMissing : null);

    /// <summary>Status line of one capture-group member in the details panel.</summary>
    public static string GroupStatusText(RecoveryVerdict verdict, JournalGroupMember member,
        RecoveryPathCheck source, RecoveryPathCheck? destination)
    {
        var state = verdict switch
        {
            RecoveryVerdict.AlreadyDone => Tr.RecoveryGroupExists,
            RecoveryVerdict.CanRetry => Tr.RecoveryGroupSourceStatus,
            // The file is still at its original path (the delete never reached it) - not "missing".
            RecoveryVerdict.NotRecycled => Tr.RecoveryGroupOnDisk,
            RecoveryVerdict.RecycleUnverifiable => Tr.RecoveryGroupMissing,
            RecoveryVerdict.SourceChanged or RecoveryVerdict.DestinationChanged => Tr.RecoveryGroupChanged,
            RecoveryVerdict.Conflict => Tr.RecoveryGroupConflict,
            RecoveryVerdict.Lost => Tr.RecoveryGroupLost,
            RecoveryVerdict.Unknown => Tr.RecoveryGroupUnreadable,
            _ => VerdictText(verdict),
        };
        if (member.Destination is null) return state;
        return $"{state} · {Tr.RecoveryDetailSource}: {PathStatusText(source.Status)}"
            + $" · {Tr.RecoveryDetailDestination}: {PathStatusText(destination?.Status ?? RecoveryPathStatus.Missing)}";
    }

    /// <summary>
    /// Confirmation text of the Retry button. A group Delete retry that will delete members PERMANENTLY (journaled Permanent, on
    /// a drive without a Recycle Bin, allowed by the current setting) says so explicitly, like the first-run confirmation
    /// does; a retried Undo of a Delete restores from the Recycle Bin and is worded that way.
    /// </summary>
    public static string RetryConfirmText(JournalEntry entry, RecoveryCheckResult? check, bool allowPermanentDelete)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The user cancelled this operation: a retry finishes it, so say exactly that (the retry service refuses otherwise).
        if (string.Equals(entry.ErrorCode, JournalErrors.CancelledByUser, StringComparison.Ordinal))
        {
            var cancelledCount = entry.GroupMembers is { Count: > 0 } cancelled ? cancelled.Count : 1;
            // A cancelled group Delete can still retry members journaled Permanent: the warning must not be lost.
            var cancelledPermanent = entry.GroupMembers is { Count: > 0 } cancelledMembers
                && entry.Type == FileOperationType.Recycle && entry.Undo != true && allowPermanentDelete
                ? PendingPermanentCount(cancelledMembers, check)
                : 0;
            return cancelledPermanent > 0
                ? Tr.RecoveryRetryConfirmCancelledPermanent(OperationText(entry.Type), cancelledCount, cancelledPermanent)
                : Tr.RecoveryRetryConfirmCancelled(OperationText(entry.Type), cancelledCount);
        }
        if (entry.GroupMembers is not { Count: > 0 } members)
            return Tr.DialogConfirmRetryMessage(OperationText(entry.Type), System.IO.Path.GetFileName(entry.Source));
        if (entry.Type == FileOperationType.Recycle && entry.Undo == true)
            return Tr.RecoveryGroupRetryConfirmRestore(members.Count);
        if (entry.Type == FileOperationType.Recycle && allowPermanentDelete)
        {
            var permanent = PendingPermanentCount(members, check);
            if (permanent > 0)
                return Tr.RecoveryGroupRetryConfirmPermanent(OperationText(entry.Type), members.Count, permanent);
        }
        return Tr.RecoveryGroupRetryConfirm(OperationText(entry.Type), members.Count);
    }

    /// <summary>Members a Delete retry deletes permanently: the ones still on disk (NotRecycled); without a check result all count.</summary>
    private static int PendingPermanentCount(IReadOnlyList<JournalGroupMember> members, RecoveryCheckResult? check)
    {
        var pending = check?.GroupMembers is { } checks
            ? checks.Where(item => item.Verdict == RecoveryVerdict.NotRecycled).Select(item => item.Member)
            : members;
        return pending.Count(member => member.Permanent);
    }

    public static string OperationText(FileOperationType type) => type switch
    {
        FileOperationType.Move => Tr.EnumFileOperationMove,
        FileOperationType.Copy => Tr.EnumFileOperationCopy,
        FileOperationType.Recycle => Tr.EnumFileOperationRecycle,
        _ => type.ToString(),
    };

    public static string StateText(JournalState state) => state switch
    {
        JournalState.Prepared => Tr.EnumJournalStatePrepared,
        JournalState.Committed => Tr.EnumJournalStateCommitted,
        JournalState.Failed => Tr.EnumJournalStateFailed,
        _ => state.ToString(),
    };

    /// <summary>Nearest existing ancestor folder of <paramref name="path"/>, or null when none exists.</summary>
    public static string? NearestExistingFolder(string path, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        string? current;
        try { current = System.IO.Path.GetDirectoryName(path); }
        catch (ArgumentException) { return null; }
        while (!string.IsNullOrEmpty(current))
        {
            if (directoryExists(current)) return current;
            current = System.IO.Path.GetDirectoryName(current);
        }
        return null;
    }
}
