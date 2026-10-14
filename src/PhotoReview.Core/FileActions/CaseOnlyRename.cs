using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// FEAT-RENAME-DESIGN 2.4: a rename that changes only the letter case (<c>a.jpg</c> to <c>A.jpg</c>). On NTFS this is ONE atomic
/// rename of ONE file, yet <see cref="IFileSystem.FileExists"/> / <see cref="IFileSystem.GetFileStat"/> answer "exists" for both
/// spellings, so every "is the source gone" / "is the destination free" check of a Move gives the wrong answer. All such checks
/// that can see a same-folder journal pair go through these helpers, which ask the folder listing which spelling is really stored
/// (<see cref="IFileSystem.GetStoredFileNames"/>). For any pair that is NOT case-only they are exactly the plain calls (one stat).
/// Never uses a temporary name: a crash cannot strand the file under a name the journal does not know.
/// </summary>
internal static class CaseOnlyRename
{
    /// <summary>True when the two paths name the same folder entry spelled differently: same folder and same name ignoring case,
    /// but not the same string.</summary>
    public static bool IsCaseOnlyPair(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        if (string.Equals(first, second, StringComparison.Ordinal)) return false;
        // Journal and request paths are already full paths (Path.Combine of a full folder); no GetFullPath here, so a path the
        // file system would reject can never make a verdict throw.
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stat of <paramref name="path"/> AS THAT SPELLING: null when no file is stored under exactly this spelling although the
    /// case-insensitive stat finds one (it is stored under the case-only <paramref name="twin"/> spelling). Without a case-only
    /// twin this is plain <see cref="IFileSystem.GetFileStat"/>. A file stored under a THIRD spelling counts as present under
    /// both (fail closed: a verdict never becomes Committed/Lost from an unproven name).
    /// </summary>
    public static FileStat? StatAs(IFileSystem fileSystem, string path, string? twin)
    {
        var stat = fileSystem.GetFileStat(path);
        if (stat is null || !IsCaseOnlyPair(path, twin)) return stat;
        return IsStoredAs(fileSystem, path, twin!) ? stat : null;
    }

    /// <summary><see cref="StatAs"/> reduced to existence (same file-system call count as <see cref="IFileSystem.FileExists"/> for a plain pair).</summary>
    public static bool ExistsAs(IFileSystem fileSystem, string path, string? twin)
    {
        if (!fileSystem.FileExists(path)) return false;
        if (!IsCaseOnlyPair(path, twin)) return true;
        return IsStoredAs(fileSystem, path, twin!);
    }

    /// <summary>
    /// The folder lists the entry under <paramref name="path"/>'s own spelling. Exactly one entry: it must be this spelling, or a
    /// third one (not the twin). Two entries (a case-sensitive folder holding two different files): both spellings exist.
    /// </summary>
    private static bool IsStoredAs(IFileSystem fileSystem, string path, string twin)
    {
        var stored = fileSystem.GetStoredFileNames(path);
        if (stored.Count == 0) return false; // vanished between the stat and the listing
        if (stored.Count > 1) return true;
        var mine = Path.GetFileName(path);
        var other = Path.GetFileName(twin);
        return string.Equals(stored[0], mine, StringComparison.Ordinal)
            || !string.Equals(stored[0], other, StringComparison.Ordinal);
    }

    /// <summary>
    /// Preflight for the TARGET of a rename. <paramref name="conflict"/> = the name is taken by something that is not the file being
    /// renamed (another file with the same name, or two case variants in a case-sensitive folder). <paramref name="alreadyNamed"/> =
    /// the file already carries exactly the requested spelling (a case-only rename with nothing to do).
    /// </summary>
    public static (bool Conflict, bool AlreadyNamed) ProbeTarget(IFileSystem fileSystem, string source, string destination)
    {
        // The very same spelling: nothing to rename (the target is the file itself, not another file in the way).
        if (string.Equals(source, destination, StringComparison.Ordinal)) return (false, true);
        if (!IsCaseOnlyPair(source, destination))
            return (fileSystem.FileExists(destination) || fileSystem.DirectoryExists(destination), false);
        var stored = fileSystem.GetStoredFileNames(destination);
        if (stored.Count > 1) return (true, false);
        if (stored.Count == 1 && string.Equals(stored[0], Path.GetFileName(destination), StringComparison.Ordinal))
            return (false, true);
        return (false, false);
    }
}
