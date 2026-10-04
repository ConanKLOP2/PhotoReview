using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>Result of <see cref="ActionDestinationPolicy.Validate"/>.</summary>
public enum ActionDestinationCheck
{
    Ok,
    Empty,
    InvalidChars,
    EscapesSourceFolder,
}

/// <summary>
/// Review 2026-09-25 CORE-03 / Q-R2: a relative action destination must stay inside the photo folder (no ".."
/// segment); an absolute (rooted) destination is an explicit user choice and stays allowed.
/// </summary>
public static class ActionDestinationPolicy
{
    private static readonly char[] s_separators = ['\\', '/'];

    // Path.GetInvalidPathChars() allocates per call and omits the wildcards '*' and '?', which a folder name can never
    // contain; without them "sel*" passed validation and only failed later inside CreateDirectory.
    private static readonly System.Buffers.SearchValues<char> s_invalidChars =
        System.Buffers.SearchValues.Create([.. Path.GetInvalidPathChars(), '*', '?']);

    public static ActionDestinationCheck Validate(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return ActionDestinationCheck.Empty;
        // The Win32 extended-length prefix \\?\ (long paths) legitimately contains '?'; only what follows it is checked.
        var body = destination.AsSpan();
        if (body.StartsWith(@"\\?\", StringComparison.Ordinal)) body = body[4..];
        if (body.IndexOfAny(s_invalidChars) >= 0) return ActionDestinationCheck.InvalidChars;

        // A drive-relative "a:b" is "rooted" to .NET but is neither a real absolute path nor a valid relative one.
        if (!Path.IsPathFullyQualified(destination) && destination.Contains(':', StringComparison.Ordinal))
            return ActionDestinationCheck.InvalidChars;

        // "\photos" / "/photos" is rooted but names no drive: it would resolve against whatever drive the process happens to be on.
        if (Path.IsPathRooted(destination) && !Path.IsPathFullyQualified(destination)) return ActionDestinationCheck.InvalidChars;

        if (Path.IsPathRooted(destination)) return ActionDestinationCheck.Ok;

        foreach (var segment in destination.Split(s_separators))
        {
            if (segment.Trim() == "..") return ActionDestinationCheck.EscapesSourceFolder;
        }

        return ActionDestinationCheck.Ok;
    }

    /// <summary>
    /// SEC-01: <see cref="Validate"/> only inspects the destination STRING (no "..", not rooted). It cannot see
    /// that an intermediate directory that already exists on disk is a symlink/junction whose real target is
    /// outside <paramref name="sourceFolder"/> — the actual Move/Copy would then land outside the photo folder
    /// even though the lexical path looked contained. This resolves every reparse point that already exists
    /// along <paramref name="destinationFolderOrPath"/> (via <see cref="IFileSystem.ResolveRealPath"/>, including
    /// the final path segment — a symlinked destination FILE is caught the same way) and checks the RESOLVED
    /// path is still inside the RESOLVED <paramref name="sourceFolder"/>. Only call this for a non-rooted
    /// (relative) destination: an absolute destination is an explicit user choice that is allowed to leave the
    /// photo folder (see <see cref="Validate"/> remarks) and must not be run through this check.
    /// </summary>
    public static ActionDestinationCheck ValidateNoEscapeViaReparsePoint(
        string sourceFolder, string destinationFolderOrPath, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolderOrPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string realSource, realDestination;
        try
        {
            realSource = Path.TrimEndingDirectorySeparator(fileSystem.ResolveRealPath(sourceFolder));
            realDestination = Path.TrimEndingDirectorySeparator(fileSystem.ResolveRealPath(destinationFolderOrPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // RV-S06: a reparse point that cannot be resolved (a link loop a -> b -> a, a target the user may not read)
            // means containment cannot be proven. SEC-01 fails CLOSED: treat it as an escape, never as "inside".
            return ActionDestinationCheck.EscapesSourceFolder;
        }

        if (string.Equals(realDestination, realSource, StringComparison.OrdinalIgnoreCase))
            return ActionDestinationCheck.Ok;

        // R32: a drive root ("C:\") keeps its separator after TrimEndingDirectorySeparator; appending another one ("C:\\")
        // would never match a child path, so a relative destination under a drive-root photo folder was rejected.
        var sourcePrefix = Path.EndsInDirectorySeparator(realSource) ? realSource : realSource + Path.DirectorySeparatorChar;
        return realDestination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? ActionDestinationCheck.Ok
            : ActionDestinationCheck.EscapesSourceFolder;
    }

    /// <summary>
    /// "Move to… / Copy to…": checks a folder chosen in the folder picker (or reused from the last time) for the photo in
    /// <paramref name="photoFolder"/>. Same rules as an absolute action destination, plus: it must not be the photo folder
    /// itself and it must already exist (the picker never creates folders; a remembered one may have been deleted).
    /// </summary>
    public static PickedFolderCheck ValidatePickedFolder(string? destination, string photoFolder, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(photoFolder);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (string.IsNullOrWhiteSpace(destination)) return PickedFolderCheck.Empty;
        if (Validate(destination) != ActionDestinationCheck.Ok || !Path.IsPathFullyQualified(destination)) return PickedFolderCheck.NotAbsolute;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var photo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(photoFolder));
        if (string.Equals(full, photo, StringComparison.OrdinalIgnoreCase)) return PickedFolderCheck.SameAsPhotoFolder;
        return directoryExists(full) ? PickedFolderCheck.Ok : PickedFolderCheck.Missing;
    }
}

/// <summary>Result of <see cref="ActionDestinationPolicy.ValidatePickedFolder"/>.</summary>
public enum PickedFolderCheck
{
    Ok,
    Empty,
    NotAbsolute,
    SameAsPhotoFolder,
    Missing,
}
