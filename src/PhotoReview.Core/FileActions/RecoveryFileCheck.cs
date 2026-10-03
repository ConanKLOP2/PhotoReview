using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>Live state of one path named by a journal entry, compared with what the journal recorded.</summary>
public enum RecoveryPathStatus
{
    /// <summary>The file exists and matches the journal (source: size and last-write; destination: size).</summary>
    Exists,
    /// <summary>No file at the path.</summary>
    Missing,
    /// <summary>The file exists but its size (or, for the source, last-write time) differs from the journal.</summary>
    Changed,
    /// <summary>The path could not be inspected (access denied, I/O error, invalid path).</summary>
    Unreadable,
}

/// <summary>Overall conclusion for one entry. <see cref="RecoveryFileCheck.Code"/> gives the stable string.</summary>
public enum RecoveryVerdict
{
    CanRetry,
    AlreadyDone,
    Conflict,
    SourceChanged,
    DestinationChanged,
    Lost,
    Unknown,
    RecycleUnverifiable,
    NotRecycled,
    /// <summary>Q-R8: a Recycle that deleted the file permanently (drive without a Recycle Bin); it cannot be restored.</summary>
    PermanentlyDeleted,
    /// <summary>A Delete of a capture group where some files were deleted permanently (drive without a Recycle Bin) and the others went to the Recycle Bin: only part of the capture is unrecoverable.</summary>
    PartiallyPermanentlyDeleted,
}

/// <summary>Result of checking one path. Size/time are the current values (null when missing or unreadable).</summary>
public sealed record RecoveryPathCheck(
    string Path,
    RecoveryPathStatus Status,
    long? CurrentSize,
    DateTime? CurrentLastWriteUtc,
    bool FolderExists);

/// <summary>Result for one journal entry. <see cref="Destination"/> is null when the entry has none (Recycle).</summary>
public sealed record RecoveryCheckResult(
    JournalEntry Entry,
    RecoveryPathCheck Source,
    RecoveryPathCheck? Destination,
    RecoveryVerdict Verdict,
    IReadOnlyList<RecoveryGroupMemberCheck>? GroupMembers = null)
{
    /// <summary>True when the destination is missing and its parent folder no longer exists either.</summary>
    public bool DestinationFolderMissing => Destination is { Status: RecoveryPathStatus.Missing, FolderExists: false };

    public bool IsGroup => GroupMembers is { Count: > 0 };
}

public sealed record RecoveryGroupMemberCheck(JournalGroupMember Member, RecoveryCheckResult Check)
{
    public RecoveryPathCheck Source => Check.Source;
    public RecoveryPathCheck? Destination => Check.Destination;
    public RecoveryVerdict Verdict => Check.Verdict;
}

/// <summary>
/// Read-only "is this journal entry still valid?" check for the Recovery window. It only calls
/// <see cref="IFileSystem.GetFileStat"/> / <see cref="IFileSystem.DirectoryExists"/>: it never moves, writes or deletes,
/// and never touches the (append-only) journal. Access errors make a path <see cref="RecoveryPathStatus.Unreadable"/>.
/// <para>Path status: Missing = no file; Changed = size differs from the journal (source also: last-write differs);
/// Exists = otherwise. Destination last-write is not compared (copies across volumes may re-stamp it).</para>
/// <para>Verdict for Move/Copy in Prepared/Failed state (S = source, D = destination):</para>
/// <list type="table">
/// <item><term>S or D unreadable, or no destination</term><description>Unknown</description></item>
/// <item><term>S exists, D missing</term><description>CanRetry</description></item>
/// <item><term>S changed, D missing</term><description>SourceChanged</description></item>
/// <item><term>S missing, D exists</term><description>AlreadyDone (operation actually completed; dismiss)</description></item>
/// <item><term>S missing, D changed</term><description>DestinationChanged (partial or altered data)</description></item>
/// <item><term>S missing, D missing</term><description>Lost</description></item>
/// <item><term>Copy: S exists, D exists</term><description>AlreadyDone (the copy is complete)</description></item>
/// <item><term>other combinations with both present</term><description>Conflict</description></item>
/// </list>
/// <para>Committed: D exists = AlreadyDone; S and D missing = Lost; otherwise Unknown. Dismissed: Unknown.
/// Recycle (any state): S missing = RecycleUnverifiable (presumably in the Recycle Bin, cannot be verified);
/// S present = NotRecycled; unreadable = Unknown. A Recycle marked <see cref="JournalEntry.Permanent"/> with S missing
/// is PermanentlyDeleted (deleted for good, cannot be restored).</para>
/// </summary>
public sealed class RecoveryFileCheck
{
    private readonly IFileSystem _fileSystem;

    public RecoveryFileCheck(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>Stable, never-renamed code for a verdict (safe for logs and tests).</summary>
    public static string Code(RecoveryVerdict verdict) => verdict switch
    {
        RecoveryVerdict.CanRetry => "CanRetry",
        RecoveryVerdict.AlreadyDone => "AlreadyDone",
        RecoveryVerdict.Conflict => "Conflict",
        RecoveryVerdict.SourceChanged => "SourceChanged",
        RecoveryVerdict.DestinationChanged => "DestinationChanged",
        RecoveryVerdict.Lost => "Lost",
        RecoveryVerdict.RecycleUnverifiable => "RecycleUnverifiable",
        RecoveryVerdict.NotRecycled => "NotRecycled",
        RecoveryVerdict.PermanentlyDeleted => "PermanentlyDeleted",
        RecoveryVerdict.PartiallyPermanentlyDeleted => "PartiallyPermanentlyDeleted",
        _ => "Unknown",
    };

    public RecoveryCheckResult Check(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.GroupMembers is { Count: > 0 } members)
        {
            var checks = members.Select(member => CheckGroupMember(entry, member)).ToArray();
            var verdict = AggregateGroupVerdict(entry, checks);
            var first = checks[0].Check;
            return new RecoveryCheckResult(entry, first.Source, first.Destination, verdict, checks);
        }
        var source = CheckPath(entry.Source, entry.Size, entry.LastWriteUtc, compareLastWrite: true);
        var destination = string.IsNullOrWhiteSpace(entry.Destination)
            ? null
            : CheckPath(entry.Destination, entry.Size, entry.LastWriteUtc, compareLastWrite: false);
        return new RecoveryCheckResult(entry, source, destination, Decide(entry, source, destination));
    }

    private RecoveryGroupMemberCheck CheckGroupMember(JournalEntry group, JournalGroupMember member)
    {
        var single = Check(group with
        {
            Source = member.Source,
            Destination = member.Destination,
            Size = member.Size,
            LastWriteUtc = member.LastWriteUtc,
            Permanent = member.Permanent,
            GroupId = null,
            GroupMembers = null,
            Undo = null,
        });
        if (group.Undo == true && group.Type == FileOperationType.Recycle)
        {
            single = single with { Entry = group, Verdict = single.Source.Status switch
            {
                RecoveryPathStatus.Exists when single.Source.CurrentSize == member.Size
                    && single.Source.CurrentLastWriteUtc == member.LastWriteUtc => RecoveryVerdict.AlreadyDone,
                RecoveryPathStatus.Exists => RecoveryVerdict.Conflict,
                RecoveryPathStatus.Missing => RecoveryVerdict.RecycleUnverifiable,
                _ => RecoveryVerdict.Unknown,
            } };
        }
        return new(member, single);
    }

    private static RecoveryVerdict AggregateGroupVerdict(JournalEntry entry, IReadOnlyList<RecoveryGroupMemberCheck> members)
    {
        var verdicts = members.Select(member => member.Check.Verdict).ToArray();
        if (entry.Undo == true && entry.Type == FileOperationType.Move
            && verdicts.All(verdict => verdict is RecoveryVerdict.CanRetry or RecoveryVerdict.AlreadyDone)
            && verdicts.Contains(RecoveryVerdict.CanRetry)) return RecoveryVerdict.CanRetry;
        if (entry.Undo == true && entry.Type == FileOperationType.Recycle
            && verdicts.All(verdict => verdict is RecoveryVerdict.RecycleUnverifiable or RecoveryVerdict.AlreadyDone)
            && verdicts.Contains(RecoveryVerdict.RecycleUnverifiable)) return RecoveryVerdict.CanRetry;
        // A non-undo Delete (Recycle) group that stopped part-way: the members still on disk can be recycled again (the retry
        // re-checks each against its journaled fingerprint and never deletes permanently unless the member was journaled
        // Permanent), while the members already gone stay as they are. A present member that no longer matches the journal
        // blocks the retry.
        if (entry.Type == FileOperationType.Recycle && entry.Undo != true
            && verdicts.All(verdict => verdict is RecoveryVerdict.NotRecycled or RecoveryVerdict.RecycleUnverifiable or RecoveryVerdict.PermanentlyDeleted)
            && verdicts.Contains(RecoveryVerdict.NotRecycled))
        {
            return members.Where(member => member.Verdict == RecoveryVerdict.NotRecycled)
                .All(member => member.Source.Status == RecoveryPathStatus.Exists)
                ? RecoveryVerdict.CanRetry
                : RecoveryVerdict.NotRecycled;
        }
        if (verdicts.All(verdict => verdict is RecoveryVerdict.AlreadyDone or RecoveryVerdict.RecycleUnverifiable or RecoveryVerdict.PermanentlyDeleted))
        {
            // Per member: only when EVERY file was deleted permanently is the whole capture unrecoverable; a mix of permanent and
            // recycled members must not tell the user that files sitting in the Recycle Bin are gone.
            if (verdicts.All(verdict => verdict == RecoveryVerdict.PermanentlyDeleted)) return RecoveryVerdict.PermanentlyDeleted;
            if (verdicts.Contains(RecoveryVerdict.PermanentlyDeleted)) return RecoveryVerdict.PartiallyPermanentlyDeleted;
            return entry.Type == FileOperationType.Recycle ? RecoveryVerdict.RecycleUnverifiable : RecoveryVerdict.AlreadyDone;
        }
        if (verdicts.All(verdict => verdict is RecoveryVerdict.AlreadyDone or RecoveryVerdict.CanRetry)
            && verdicts.Contains(RecoveryVerdict.CanRetry))
            return RecoveryVerdict.CanRetry;
        if (verdicts.Contains(RecoveryVerdict.Conflict)) return RecoveryVerdict.Conflict;
        if (verdicts.Contains(RecoveryVerdict.SourceChanged)) return RecoveryVerdict.SourceChanged;
        if (verdicts.Contains(RecoveryVerdict.DestinationChanged)) return RecoveryVerdict.DestinationChanged;
        if (verdicts.Contains(RecoveryVerdict.Lost)) return RecoveryVerdict.Lost;
        if (verdicts.Contains(RecoveryVerdict.NotRecycled)) return RecoveryVerdict.NotRecycled;
        return RecoveryVerdict.Unknown;
    }

    internal static RecoveryVerdict Decide(JournalEntry entry, RecoveryPathCheck source, RecoveryPathCheck? destination)
    {
        var s = source.Status;
        if (entry.Type == FileOperationType.Recycle)
        {
            return s switch
            {
                RecoveryPathStatus.Missing => entry.Permanent == true ? RecoveryVerdict.PermanentlyDeleted : RecoveryVerdict.RecycleUnverifiable,
                RecoveryPathStatus.Unreadable => RecoveryVerdict.Unknown,
                _ => RecoveryVerdict.NotRecycled,
            };
        }

        if (destination is null) return RecoveryVerdict.Unknown;
        var d = destination.Status;

        if (entry.State == JournalState.Committed)
        {
            if (d == RecoveryPathStatus.Exists) return RecoveryVerdict.AlreadyDone;
            return d == RecoveryPathStatus.Missing && s == RecoveryPathStatus.Missing ? RecoveryVerdict.Lost : RecoveryVerdict.Unknown;
        }
        if (entry.State is not (JournalState.Prepared or JournalState.Failed)) return RecoveryVerdict.Unknown;
        if (s == RecoveryPathStatus.Unreadable || d == RecoveryPathStatus.Unreadable) return RecoveryVerdict.Unknown;

        var sourcePresent = s is RecoveryPathStatus.Exists or RecoveryPathStatus.Changed;
        var destinationPresent = d is RecoveryPathStatus.Exists or RecoveryPathStatus.Changed;

        if (!destinationPresent)
        {
            if (!sourcePresent) return RecoveryVerdict.Lost;
            return s == RecoveryPathStatus.Exists ? RecoveryVerdict.CanRetry : RecoveryVerdict.SourceChanged;
        }
        if (!sourcePresent)
            return d == RecoveryPathStatus.Exists ? RecoveryVerdict.AlreadyDone : RecoveryVerdict.DestinationChanged;
        if (entry.Type == FileOperationType.Copy && s == RecoveryPathStatus.Exists && d == RecoveryPathStatus.Exists)
            return RecoveryVerdict.AlreadyDone;
        return RecoveryVerdict.Conflict;
    }

    private RecoveryPathCheck CheckPath(string path, long recordedSize, DateTime recordedLastWriteUtc, bool compareLastWrite)
    {
        var folderExists = false;
        try
        {
            var folder = Path.GetDirectoryName(path);
            folderExists = !string.IsNullOrEmpty(folder) && _fileSystem.DirectoryExists(folder);
            var stat = _fileSystem.GetFileStat(path);
            if (stat is null) return new(path, RecoveryPathStatus.Missing, null, null, folderExists);
            var changed = stat.Length != recordedSize || (compareLastWrite && stat.LastWriteUtc != recordedLastWriteUtc);
            return new(path, changed ? RecoveryPathStatus.Changed : RecoveryPathStatus.Exists, stat.Length, stat.LastWriteUtc, folderExists);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return new(path, RecoveryPathStatus.Unreadable, null, null, folderExists);
        }
    }
}
