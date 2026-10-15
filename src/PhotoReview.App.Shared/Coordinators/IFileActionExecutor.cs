using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// The part of <see cref="FileActionService"/> the controllers use. A seam only: <see cref="FileActionService"/> is concrete and
/// turns its own failures into results, so the controllers' "unexpected exception" paths could not otherwise be exercised.
/// </summary>
internal interface IFileActionExecutor
{
    bool IsBusy { get; }

    bool LacksRecycleBin(string path);

    Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default);

    Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default);

    /// <summary>FEAT-RENAME: renames one file inside its folder (<see cref="FileActionService.RenameAsync"/>).</summary>
    Task<FileActionResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <summary>FEAT-RENAME: renames a whole capture (<see cref="FileActionService.RenameGroupAsync"/>).</summary>
    Task<CaptureGroupActionResult> RenameGroupAsync(CaptureGroup group, string typedStem, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>Production executor: forwards to the real <see cref="FileActionService"/>.</summary>
internal sealed class FileActionServiceExecutor(FileActionService service) : IFileActionExecutor
{
    public bool IsBusy => service.IsBusy;

    public bool LacksRecycleBin(string path) => service.LacksRecycleBin(path);

    public Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default)
        => service.ExecuteAsync(request, cancellationToken);

    public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default)
        => service.ExecuteGroupAsync(request, cancellationToken);

    public Task<FileActionResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken = default)
        => service.RenameAsync(request, cancellationToken);

    public Task<CaptureGroupActionResult> RenameGroupAsync(CaptureGroup group, string typedStem, CancellationToken cancellationToken = default)
        => service.RenameGroupAsync(group, typedStem, cancellationToken);
}
