using System.IO;

namespace PhotoReview.App;

/// <summary>
/// Command-line paths as Explorer's "Browse with PhotoReview" (and drag onto the exe) hand them over. A folder argument is made
/// canonical (absolute, no trailing separator except for a drive root) so the instance lock, the Explorer prefetch and the folder
/// load all see one spelling; a quoted drive root such as <c>"D:\"</c> reaches the process as <c>D:"</c> (the backslash escapes the
/// closing quote) and is repaired. Anything that is not an existing folder is returned unchanged.
/// </summary>
internal static class LaunchArguments
{
    public static string[] Normalize(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return [.. args.Select(NormalizeOne)];
    }

    internal static string NormalizeOne(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return arg;
        var candidate = arg;
        if (arg.Length > 1 && arg[^1] == '"')
        {
            var repaired = arg[..^1] + "\\";
            if (Directory.Exists(repaired)) candidate = repaired;
        }
        if (!Directory.Exists(candidate)) return arg;
        var full = Path.GetFullPath(candidate);
        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        return full.Length > rootLength ? full.TrimEnd('\\', '/') : full;
    }
}
