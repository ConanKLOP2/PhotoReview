using System.Globalization;
using System.IO;
using System.Windows.Media;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

/// <summary>Verdict filter offered by the Recovery window. Order matches the ComboBox items.</summary>
public enum RecoveryFilter
{
    All,
    CanRetry,
    AlreadyDone,
    Problems,
}

/// <summary>What the details panel shows for one path (source or destination).</summary>
internal sealed record RecoveryPathView(
    string Title,
    string Path,
    string StatusText,
    Brush StatusBrush,
    string NowText,
    string JournalText,
    string? Note);

/// <summary>
/// Pure mapping from <see cref="RecoveryCheckResult"/> to localized text and colors for the Recovery window
/// (no WPF windows involved, so it is unit-testable). A verdict is always shown as text; the color is only a hint.
/// </summary>
internal static class RecoveryPresenter
{
    private static readonly Brush Good = Frozen(0x6C, 0xCB, 0x7A);
    private static readonly Brush Info = Frozen(0x6F, 0xB1, 0xFF);
    private static readonly Brush Warn = Frozen(0xF0, 0xB0, 0x40);
    private static readonly Brush Bad = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly Brush Neutral = Frozen(0xB0, 0xB0, 0xB0);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush NeutralBrush => Neutral;

    public static Brush VerdictBrush(RecoveryVerdict verdict) => verdict switch
    {
        RecoveryVerdict.CanRetry => Good,
        RecoveryVerdict.AlreadyDone => Info,
        RecoveryVerdict.Conflict or RecoveryVerdict.SourceChanged or RecoveryVerdict.DestinationChanged or RecoveryVerdict.NotRecycled
            or RecoveryVerdict.PartiallyPermanentlyDeleted => Warn,
        RecoveryVerdict.PermanentlyDeleted or RecoveryVerdict.Lost => Bad,
        _ => Neutral,
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

    private static Brush PathStatusBrush(RecoveryPathStatus status) => status switch
    {
        RecoveryPathStatus.Exists => Good,
        RecoveryPathStatus.Changed => Warn,
        RecoveryPathStatus.Missing => Neutral,
        _ => Bad,
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
        new(title, check.Path, Tr.RecoveryDetailStatus(PathStatusText(check.Status)), PathStatusBrush(check.Status),
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
            return Tr.RecoveryRetryConfirmCancelled(OperationText(entry.Type), entry.GroupMembers is { Count: > 0 } cancelled ? cancelled.Count : 1);
        if (entry.GroupMembers is not { Count: > 0 } members)
            return Tr.DialogConfirmRetryMessage(OperationText(entry.Type), Path.GetFileName(entry.Source));
        if (entry.Type == FileOperationType.Recycle && entry.Undo == true)
            return Tr.RecoveryGroupRetryConfirmRestore(members.Count);
        if (entry.Type == FileOperationType.Recycle && allowPermanentDelete)
        {
            // The members the retry deletes: the ones still on disk (NotRecycled). Without a check result assume all.
            var pending = check?.GroupMembers is { } checks
                ? checks.Where(item => item.Verdict == RecoveryVerdict.NotRecycled).Select(item => item.Member)
                : members;
            var permanent = pending.Count(member => member.Permanent);
            if (permanent > 0)
                return Tr.RecoveryGroupRetryConfirmPermanent(OperationText(entry.Type), members.Count, permanent);
        }
        return Tr.RecoveryGroupRetryConfirm(OperationText(entry.Type), members.Count);
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
        try { current = Path.GetDirectoryName(path); }
        catch (ArgumentException) { return null; }
        while (!string.IsNullOrEmpty(current))
        {
            if (directoryExists(current)) return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
