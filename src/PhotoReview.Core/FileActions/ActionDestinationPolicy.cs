using System.IO;

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
        if (destination.AsSpan().IndexOfAny(s_invalidChars) >= 0) return ActionDestinationCheck.InvalidChars;

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
