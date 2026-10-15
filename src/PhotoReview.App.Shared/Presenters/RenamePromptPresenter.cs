using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Presenters;

/// <summary>
/// FEAT-RENAME (PR3): the rules behind the "Rename file" box, without UI. The user edits the NAME PART; the extension stays
/// (<see cref="RenameNamePolicy"/> decides what the typed text becomes). <see cref="TryAccept"/> maps the policy's problem code
/// to the localized text shown under the box; "the new name is taken" is a separate, injected question so that typing never
/// touches the disk (only the confirming call may).
/// </summary>
internal sealed class RenamePromptPresenter
{
    private readonly Func<string, RenameNameCheck> _check;
    private readonly Func<string, bool, bool>? _nameTaken;

    /// <param name="currentPath">Full path of the file being renamed.</param>
    /// <param name="check">Typed text to the policy verdict (single file or capture group: the caller picks the plan).</param>
    /// <param name="nameTaken">(complete new file name, isCommit) to "another file already has this name"; null = never taken.</param>
    public RenamePromptPresenter(string currentPath, Func<string, RenameNameCheck> check, Func<string, bool, bool>? nameTaken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _nameTaken = nameTaken;
        InitialText = Path.GetFileNameWithoutExtension(currentPath);
        Extension = Path.GetExtension(currentPath);
    }

    /// <summary>The name part the box starts with (the whole of it is selected).</summary>
    public string InitialText { get; }

    /// <summary>The extension shown fixed beside the box (with its dot, may be empty).</summary>
    public string Extension { get; }

    /// <summary>The request for <see cref="IDialogService.PromptRename"/>.</summary>
    public RenamePromptRequest ToRequest() => new(InitialText, Extension, TryAccept);

    /// <summary>The localized refusal for <paramref name="typed"/>, or null when it can be used.</summary>
    /// <param name="isCommit">true when the user confirmed: the answer may then also look at the disk.</param>
    public string? TryAccept(string? typed, bool isCommit = false)
    {
        var check = _check(typed ?? string.Empty);
        switch (check.Problem)
        {
            case RenameNameProblem.None:
                break;
            case RenameNameProblem.Empty:
                return Tr.DialogRenameErrorEmpty;
            case RenameNameProblem.InvalidCharacter:
                return Tr.DialogRenameErrorInvalidCharacter;
            case RenameNameProblem.TrailingDotOrSpace:
                return Tr.DialogRenameErrorTrailingDot;
            case RenameNameProblem.ReservedDeviceName:
                return Tr.DialogRenameErrorReservedName;
            case RenameNameProblem.NameTooLong:
                return Tr.DialogRenameErrorTooLong;
            case RenameNameProblem.PathTooLong:
                return Tr.DialogRenameErrorPathTooLong;
            case RenameNameProblem.SidecarNotMappable:
                return Tr.DialogRenameErrorSidecarNotMappable;
            default:
                return Tr.DialogRenameErrorInvalidCharacter;
        }

        // The same name (or a change of letter case only) is the file itself, not a clash.
        if (check.IsUnchanged || check.DiffersOnlyByCase) return null;
        return _nameTaken is not null && _nameTaken(check.NewFileName, isCommit)
            ? Tr.DialogRenameErrorExists(check.NewFileName)
            : null;
    }
}
