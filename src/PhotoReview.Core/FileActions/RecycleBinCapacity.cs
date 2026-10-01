using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>Shared F-WIN-2 capacity rule for group Recycle (first run and Recovery retry).</summary>
internal static class RecycleBinCapacity
{
    /// <summary>
    /// The bin of a volume must hold ALL of that volume's (non-permanent) members together, not just each one alone,
    /// otherwise the shell deletes the overflow permanently while the journal says "recycle". Returns the first member of
    /// the first volume that does not fit, or null when every volume fits.
    /// </summary>
    public static JournalGroupMember? FirstOverflow(IRecycleBin recycleBin, IEnumerable<JournalGroupMember> members)
    {
        foreach (var volume in members.Where(member => !member.Permanent)
            .GroupBy(member => Path.GetPathRoot(member.Source) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var first = volume.First();
            if (!recycleBin.FitsInRecycleBin(first.Source, volume.Sum(member => member.Size))) return first;
        }
        return null;
    }
}
