using PhotoReview.Core.FileActions;

namespace PhotoReview.App.Coordinators;

/// <summary>How a "rename the photo being viewed" request ended (FEAT-RENAME, PR2 gate; the dialog that asks for the name is PR3).</summary>
public enum FileRenameStatus
{
    /// <summary>The file (or the whole capture) has its new name; it is registered for Undo.</summary>
    Renamed = 0,

    /// <summary>The typed name is the file's current name: nothing was done, nothing was journaled.</summary>
    NoChange,

    /// <summary>The typed name is refused by <see cref="RenameNamePolicy"/> (see <see cref="FileRenameResult.Problem"/>): nothing was done.</summary>
    Invalid,

    /// <summary>The rename did not happen (name taken, file locked, I/O error ...): <see cref="FileRenameResult.Error"/> says why.</summary>
    Failed,

    /// <summary>Another file action or undo holds the gate (INV-4), or the folder changed while the request waited: nothing was done.</summary>
    Busy,

    /// <summary>There is no photo to rename.</summary>
    NothingToRename,
}

/// <summary>Outcome of <see cref="FileActionController.RenameAsync"/>.</summary>
/// <param name="NewPath">The new full path of the file shown (the JPEG/representative of a capture) when <see cref="FileRenameStatus.Renamed"/>.</param>
/// <param name="Problem">Why the name was refused when <see cref="FileRenameStatus.Invalid"/>; the UI turns the code into text.</param>
/// <param name="InvalidCharacter">The first offending character for <see cref="RenameNameProblem.InvalidCharacter"/>.</param>
/// <param name="Error">The failure text when <see cref="FileRenameStatus.Failed"/>.</param>
public sealed record FileRenameResult(
    FileRenameStatus Status,
    string? NewPath = null,
    RenameNameProblem Problem = RenameNameProblem.None,
    char? InvalidCharacter = null,
    string? Error = null)
{
    internal static FileRenameResult Busy { get; } = new(FileRenameStatus.Busy);

    internal static FileRenameResult Nothing { get; } = new(FileRenameStatus.NothingToRename);
}
