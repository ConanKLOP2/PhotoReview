using System.IO;
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
    RecoveryVerdict Verdict)
{
    /// <summary>True when the destination is missing and its parent folder no longer exists either.</summary>
    public bool DestinationFolderMissing => Destination is { Status: RecoveryPathStatus.Missing, FolderExists: false };
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
        _ => "Unknown",
    };

    public RecoveryCheckResult Check(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var source = CheckPath(entry.Source, entry.Size, entry.LastWriteUtc, compareLastWrite: true);
        var destination = string.IsNullOrWhiteSpace(entry.Destination)
            ? null
            : CheckPath(entry.Destination, entry.Size, entry.LastWriteUtc, compareLastWrite: false);
        return new RecoveryCheckResult(entry, source, destination, Decide(entry, source, destination));
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
