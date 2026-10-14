namespace PhotoReview.Core.FileActions;

/// <summary>
/// Rename of ONE file inside its own folder (FEAT-RENAME-DESIGN). <paramref name="NewFileName"/> is the COMPLETE new file name
/// (stem + extension, one path component, no folder), normally the <see cref="RenameNameCheck.NewFileName"/> of
/// <see cref="RenameNamePolicy.Check"/>. The service re-validates it; it never overwrites an existing file.
/// </summary>
public sealed record RenameRequest(string Source, string NewFileName);
