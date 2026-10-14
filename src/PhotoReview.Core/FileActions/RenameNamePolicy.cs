namespace PhotoReview.Core.FileActions;

/// <summary>Why <see cref="RenameNamePolicy"/> refused a name (FEAT-RENAME-DESIGN 2.3). The UI localizes by this code; Core has no text.</summary>
public enum RenameNameProblem
{
    None = 0,
    /// <summary>Empty or only white space (after the unchanged extension was stripped).</summary>
    Empty,
    /// <summary>A character Windows forbids in a file name (<c>&lt; &gt; : " / \ | ? *</c>, a control character or a lone surrogate).</summary>
    InvalidCharacter,
    /// <summary>The name ends with a dot (Win32 drops it silently, so the file would get another name than typed).</summary>
    TrailingDotOrSpace,
    /// <summary>A DOS device name (CON, PRN, AUX, NUL, COM0-9, LPT0-9, also with an extension such as <c>CON.txt</c>).</summary>
    ReservedDeviceName,
    /// <summary>The file name is longer than 255 UTF-16 code units (NTFS component limit).</summary>
    NameTooLong,
    /// <summary>The full path would reach MAX_PATH (260) and is longer than the current path (the app is not long-path aware).</summary>
    PathTooLong,
    /// <summary>A capture group's XMP sidecar is named after neither the JPEG stem nor a member file name (never guessed).</summary>
    SidecarNotMappable,
}

/// <summary>Verdict of <see cref="RenameNamePolicy.Check"/>.</summary>
/// <param name="Problem"><see cref="RenameNameProblem.None"/> when the name can be used.</param>
/// <param name="NewFileName">The complete new file name (typed stem + the ORIGINAL extension); empty when invalid.</param>
/// <param name="InvalidCharacter">The first offending character when <see cref="Problem"/> is <see cref="RenameNameProblem.InvalidCharacter"/>.</param>
/// <param name="IsUnchanged">The new name is exactly the current one (nothing to do, nothing is journaled).</param>
/// <param name="DiffersOnlyByCase">The new name differs from the current one only by letter case (the case-only path, FEAT-RENAME-DESIGN 2.4).</param>
public readonly record struct RenameNameCheck(
    RenameNameProblem Problem,
    string NewFileName,
    char? InvalidCharacter = null,
    bool IsUnchanged = false,
    bool DiffersOnlyByCase = false)
{
    public bool IsValid => Problem == RenameNameProblem.None;
}

/// <summary>
/// Pure validation of a typed new name for "rename the photo being viewed" (FEAT-RENAME-DESIGN 2.3). The user types the NAME
/// PART (stem); the file's own extension is kept (a typed trailing copy of it is dropped, any other dotted tail stays part of the
/// stem: <c>a.v2</c> becomes <c>a.v2.jpg</c>). No text is normalized: the name is used exactly as typed (NFC and NFD forms are
/// different names on NTFS, rewriting the user's input would be surprising); only surrounding white space is trimmed.
/// Comparisons with existing names are <see cref="StringComparison.OrdinalIgnoreCase"/> (the NTFS rule).
/// </summary>
public static class RenameNamePolicy
{
    /// <summary>NTFS limit of one path component, in UTF-16 code units.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>First path length (including the terminating NUL budget) that Win32 non-long-path APIs refuse.</summary>
    public const int MaxPath = 260;

    private static readonly string[] s_deviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
    ];

    /// <summary>
    /// Validates <paramref name="typedStem"/> for the file at <paramref name="currentFullPath"/>.
    /// </summary>
    /// <param name="typedStem">What the user typed (with or without the current extension).</param>
    /// <param name="currentFullPath">Full path of the file being renamed (its folder and extension decide the result).</param>
    /// <param name="longPathAware">true lifts the MAX_PATH check (the process opted in to long paths).</param>
    public static RenameNameCheck Check(string? typedStem, string currentFullPath, bool longPathAware = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFullPath);
        var currentName = Path.GetFileName(currentFullPath);
        var extension = Path.GetExtension(currentName);

        var stem = (typedStem ?? string.Empty).Trim();
        // The user may type the file's own extension (as Explorer shows it); a stem equal to the extension alone stays an empty name.
        if (extension.Length > 0 && stem.Length > extension.Length
            && stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^extension.Length].TrimEnd();
        }
        else if (extension.Length > 0 && string.Equals(stem, extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = string.Empty;
        }

        if (stem.Length == 0) return Invalid(RenameNameProblem.Empty);
        if (FirstInvalidCharacter(stem) is { } bad) return Invalid(RenameNameProblem.InvalidCharacter, bad);
        if (stem[^1] == '.') return Invalid(RenameNameProblem.TrailingDotOrSpace);

        var fileName = stem + extension;
        if (fileName.Length > MaxFileNameLength) return Invalid(RenameNameProblem.NameTooLong);
        if (IsReservedDeviceName(fileName)) return Invalid(RenameNameProblem.ReservedDeviceName);

        if (ExceedsPathBudget(currentFullPath, fileName, longPathAware)) return Invalid(RenameNameProblem.PathTooLong);

        var unchanged = string.Equals(fileName, currentName, StringComparison.Ordinal);
        var caseOnly = !unchanged && string.Equals(fileName, currentName, StringComparison.OrdinalIgnoreCase);
        return new RenameNameCheck(RenameNameProblem.None, fileName, null, unchanged, caseOnly);

        static RenameNameCheck Invalid(RenameNameProblem problem, char? character = null) =>
            new(problem, string.Empty, character);
    }

    /// <summary>
    /// Structural check of a COMPLETE file name that is about to be used as a rename target (the service re-checks what the UI
    /// already checked: the name must be one path component and valid). Returns <see cref="RenameNameProblem.None"/> when usable.
    /// </summary>
    public static RenameNameProblem CheckFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return RenameNameProblem.Empty;
        if (FirstInvalidCharacter(fileName) is not null) return RenameNameProblem.InvalidCharacter;
        if (fileName[^1] is '.' or ' ') return RenameNameProblem.TrailingDotOrSpace;
        if (fileName.Length > MaxFileNameLength) return RenameNameProblem.NameTooLong;
        if (IsReservedDeviceName(fileName)) return RenameNameProblem.ReservedDeviceName;
        return RenameNameProblem.None;
    }

    /// <summary>
    /// True when renaming the file at <paramref name="currentFullPath"/> to <paramref name="newFileName"/> would make its full path
    /// reach MAX_PATH AND be longer than the current path (a file already living at a long path may keep or shorten it).
    /// </summary>
    public static bool ExceedsPathBudget(string currentFullPath, string newFileName, bool longPathAware = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFullPath);
        ArgumentNullException.ThrowIfNull(newFileName);
        if (longPathAware) return false;
        var folder = Path.GetDirectoryName(currentFullPath) ?? string.Empty;
        if (folder.Length == 0) return false;
        var newPathLength = Path.TrimEndingDirectorySeparator(folder).Length + 1 + newFileName.Length;
        return newPathLength >= MaxPath && newPathLength > currentFullPath.Length;
    }

    /// <summary>True when the part of <paramref name="fileName"/> before its first dot is a DOS device name (Win32 resolves
    /// <c>CON.txt</c> and even <c>CON .txt</c> to the device).</summary>
    public static bool IsReservedDeviceName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var head = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        foreach (var device in s_deviceNames)
        {
            if (string.Equals(head, device, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (head.Length != 4) return false;
        var prefix = head[..3];
        var isCom = string.Equals(prefix, "COM", StringComparison.OrdinalIgnoreCase);
        var isLpt = string.Equals(prefix, "LPT", StringComparison.OrdinalIgnoreCase);
        if (!isCom && !isLpt) return false;
        // 0-9 and the superscript digits (COM1 ... COM9, COM-superscript-1/2/3) are all device names.
        return head[3] is >= '0' and <= '9' or '¹' or '²' or '³';
    }

    private static char? FirstInvalidCharacter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < ' ' || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') return c;
            if (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))) return c;
            if (char.IsHighSurrogate(c) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))) return c;
        }

        return null;
    }
}
