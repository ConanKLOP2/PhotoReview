using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// FEAT-RENAME-DESIGN section 3: which new file name every member of a JPEG + RAW [+ XMP] capture gets for a typed stem. Pure.
/// Each image keeps its own extension; an XMP sidecar follows the member that owns it: <c>&lt;base&gt;.xmp</c> becomes
/// <c>&lt;new stem&gt;.xmp</c>, <c>&lt;file&gt;.&lt;ext&gt;.xmp</c> becomes <c>&lt;new file name&gt;.xmp</c>. A sidecar whose name matches
/// neither form makes the whole plan invalid (it is never guessed and never left behind half-renamed).
/// </summary>
public static class CaptureGroupRenamePlan
{
    /// <summary>The verdict and, when valid, the source path -> new file name map for <see cref="CaptureGroupActionRequest.DestinationFileNames"/>.</summary>
    public readonly record struct Plan(RenameNameCheck Check, IReadOnlyDictionary<string, string>? FileNames);

    /// <param name="group">The capture.</param>
    /// <param name="typedStem">What the user typed (name part; a typed trailing extension of the JPEG is dropped like for a single file).</param>
    /// <param name="longPathAware">Lifts the MAX_PATH check of <see cref="RenameNamePolicy.Check"/>.</param>
    public static Plan Build(CaptureGroup group, string? typedStem, bool longPathAware = false)
    {
        ArgumentNullException.ThrowIfNull(group);

        // The JPEG decides what a typed extension is; every member is then validated with the same stem (longest path wins).
        var jpegCheck = RenameNamePolicy.Check(typedStem, group.JpegPath, longPathAware);
        if (!jpegCheck.IsValid) return new Plan(jpegCheck, null);
        // Check appended the JPEG's own extension once; the stem is what is left. The other members get stem + THEIR extension
        // directly (never a second extension-strip: a typed "a.cr3" must stay one stem for the whole capture).
        var stem = jpegCheck.NewFileName[..^Path.GetExtension(group.JpegPath).Length];
        var rawName = stem + Path.GetExtension(group.RawPath);
        var rawProblem = RenameNamePolicy.CheckFileName(rawName);
        if (rawProblem == RenameNameProblem.None && RenameNamePolicy.ExceedsPathBudget(group.RawPath, rawName, longPathAware))
            rawProblem = RenameNameProblem.PathTooLong;
        if (rawProblem != RenameNameProblem.None) return new Plan(new RenameNameCheck(rawProblem, string.Empty), null);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [group.JpegPath] = jpegCheck.NewFileName,
            [group.RawPath] = rawName,
        };

        if (group.XmpPath is { } xmpPath)
        {
            var xmpName = Path.GetFileName(xmpPath);
            var xmpExtension = Path.GetExtension(xmpName);
            var owner = xmpName[..^xmpExtension.Length];
            string? newXmp = null;
            if (string.Equals(owner, Path.GetFileNameWithoutExtension(group.JpegPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(owner, Path.GetFileNameWithoutExtension(group.RawPath), StringComparison.OrdinalIgnoreCase))
                newXmp = stem + xmpExtension;
            else if (string.Equals(owner, Path.GetFileName(group.JpegPath), StringComparison.OrdinalIgnoreCase))
                newXmp = jpegCheck.NewFileName + xmpExtension;
            else if (string.Equals(owner, Path.GetFileName(group.RawPath), StringComparison.OrdinalIgnoreCase))
                newXmp = rawName + xmpExtension;

            if (newXmp is null) return new Plan(new RenameNameCheck(RenameNameProblem.SidecarNotMappable, string.Empty), null);
            var sidecarProblem = RenameNamePolicy.CheckFileName(newXmp);
            if (sidecarProblem == RenameNameProblem.None && RenameNamePolicy.ExceedsPathBudget(xmpPath, newXmp, longPathAware))
                sidecarProblem = RenameNameProblem.PathTooLong;
            if (sidecarProblem != RenameNameProblem.None) return new Plan(new RenameNameCheck(sidecarProblem, string.Empty), null);
            names[xmpPath] = newXmp;
        }

        var unchanged = names.All(pair => string.Equals(Path.GetFileName(pair.Key), pair.Value, StringComparison.Ordinal));
        var caseOnly = !unchanged && names.All(pair =>
            string.Equals(Path.GetFileName(pair.Key), pair.Value, StringComparison.OrdinalIgnoreCase));
        return new Plan(jpegCheck with { IsUnchanged = unchanged, DiffersOnlyByCase = caseOnly }, names);
    }
}
