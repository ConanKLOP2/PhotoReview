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

    public static ActionDestinationCheck Validate(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return ActionDestinationCheck.Empty;
        if (destination.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return ActionDestinationCheck.InvalidChars;

        // A drive-relative "a:b" is "rooted" to .NET but is neither a real absolute path nor a valid relative one.
        if (!Path.IsPathFullyQualified(destination) && destination.Contains(':', StringComparison.Ordinal))
            return ActionDestinationCheck.InvalidChars;

        if (Path.IsPathRooted(destination)) return ActionDestinationCheck.Ok;

        foreach (var segment in destination.Split(s_separators))
        {
            if (segment.Trim() == "..") return ActionDestinationCheck.EscapesSourceFolder;
        }

        return ActionDestinationCheck.Ok;
    }
}
