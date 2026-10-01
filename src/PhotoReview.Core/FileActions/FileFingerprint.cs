using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// RV-D1 (option A): identity check for a file that was MOVED to another place and is compared there against the stamp the
/// source had. The destination volume may store the last-write time with less precision (FAT: 2 s, exFAT: 10 ms), and a
/// file moved back keeps that rounded stamp, so an exact compare would call the moved file "changed" (Ctrl+Z refused, a
/// group compensation stuck). Same size and a write time within <see cref="Tolerance"/> is the same file. Compares of a
/// file that never left its volume stay exact (callers use plain equality there).
/// </summary>
internal static class FileFingerprint
{
    /// <summary>The coarsest rounding of a Windows volume (FAT/FAT32 2 s); exFAT (10 ms) and NTFS fall well inside.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>True when <paramref name="stat"/> is the moved file recorded as <paramref name="size"/> bytes written at
    /// <paramref name="recordedUtc"/>, allowing for a destination volume that rounds the write time.</summary>
    public static bool MatchesMovedDestination(FileStat stat, long size, DateTime recordedUtc)
    {
        ArgumentNullException.ThrowIfNull(stat);
        return stat.Length == size && Math.Abs((stat.LastWriteUtc - recordedUtc).Ticks) <= Tolerance.Ticks;
    }
}
