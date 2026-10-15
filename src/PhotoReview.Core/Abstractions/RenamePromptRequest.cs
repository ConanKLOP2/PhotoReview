namespace PhotoReview.Core.Abstractions;

/// <summary>
/// FEAT-RENAME (PR3): what <see cref="IDialogService.PromptRename"/> asks the user. The dialog shows <see cref="InitialName"/>
/// (the name part WITHOUT extension) selected in a one-line box, <see cref="Extension"/> fixed beside it, and calls
/// <see cref="Validate"/> on every change (<c>isCommit: false</c>, must stay cheap: no disk access) and once more when the user
/// confirms (<c>isCommit: true</c>, may look at the disk). A non-null result is the localized reason to refuse the name: it is
/// shown in place and the dialog stays open.
/// </summary>
/// <param name="InitialName">The current file name without its extension.</param>
/// <param name="Extension">The current extension with its dot (may be empty); it is kept by the rename.</param>
/// <param name="Validate">(typed name, isCommit) to the refusal text, or null when the name can be used.</param>
public sealed record RenamePromptRequest(string InitialName, string Extension, Func<string, bool, string?> Validate);
