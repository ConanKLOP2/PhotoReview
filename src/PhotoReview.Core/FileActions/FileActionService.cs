using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Dịch vụ điều phối thực thi các thao tác tệp tin (Move, Copy, Recycle) kèm ghi nhật ký (OperationJournal)
/// và cổng bảo vệ tránh thao tác đồng thời (INV-4).
/// </summary>
public sealed class FileActionService
{
    private readonly OperationJournal _journal;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly IRecycleBin _recycleBin;
    private readonly Func<string, string, Task>? _moveOverride;

    private int _inProgress;

    public FileActionService(
        OperationJournal journal,
        IFileSystem fileSystem,
        IClock clock,
        IRecycleBin recycleBin,
        Func<string, string, Task>? moveOverride = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _moveOverride = moveOverride;
    }

    /// <summary>
    /// Cho biết hiện tại có thao tác tệp tin nào đang chạy hay không.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _inProgress) != 0;

    /// <summary>
    /// Thử khóa cổng thao tác tệp tin (INV-4). Trả về true nếu thành công khóa cổng.
    /// </summary>
    public bool TryBegin() => Interlocked.CompareExchange(ref _inProgress, 1, 0) == 0;

    /// <summary>
    /// Mở khóa cổng thao tác tệp tin sau khi hoàn thành.
    /// </summary>
    public void End() => Volatile.Write(ref _inProgress, 0);

    /// <summary>
    /// Q-R8: true when a Recycle of <paramref name="path"/> cannot go to a Recycle Bin (removable/network/unknown drive), i.e.
    /// it would be a PERMANENT delete. Callers use it to decide whether the user must confirm (and whether the setting applies).
    /// </summary>
    public bool LacksRecycleBin(string path) => !_recycleBin.CanRecycle(path);

    /// <summary>
    /// Thực thi yêu cầu thao tác tệp tin không đồng bộ.
    /// </summary>
    public async Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryBegin())
        {
            return new FileActionResult(
                Succeeded: false,
                Operation: request.Operation,
                Source: request.Source,
                DestinationPath: null,
                Size: 0,
                LastWriteUtc: DateTime.MinValue,
                Error: Tr.CoreFileActionBusy,
                Rejected: true);
        }

        var operationId = Guid.NewGuid().ToString("N");
        JournalTransaction? tx = null;
        string? destinationPath = null;
        long sourceSize = 0;
        var sourceLastWriteUtc = DateTime.MinValue;
        var permanent = false;

        try
        {
            if (request.Operation is FileOperationType.Move or FileOperationType.Copy)
            {
                if (string.IsNullOrWhiteSpace(request.Destination))
                    throw new IOException(Tr.CoreFileActionNoDestination);

                // Enforce the whole ActionDestinationPolicy at run time, not only what Settings validated (review r7):
                // drive-relative "a:b" and rooted-without-drive "\photos" resolve against whatever drive/current
                // directory the process has, i.e. neither an explicit absolute path nor a folder inside the photo folder.
                if (ActionDestinationPolicy.Validate(request.Destination) != ActionDestinationCheck.Ok)
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);

                var source = request.Source;
                var destinationFolder = Path.IsPathRooted(request.Destination)
                    ? request.Destination
                    : Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, request.Destination);

                destinationFolder = Path.GetFullPath(destinationFolder);
                var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source) ?? string.Empty);

                if (IsSamePath(destinationFolder, sourceFolder))
                    throw new IOException(Tr.CoreFileActionSameFolder);

                if (!Path.IsPathRooted(request.Destination)
                    && !destinationFolder.StartsWith(sourceFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);

                _fileSystem.CreateDirectory(destinationFolder);
                destinationPath = Path.Combine(destinationFolder, Path.GetFileName(source));

                if (_fileSystem.FileExists(destinationPath))
                    throw new IOException(Tr.CoreFileActionDestinationExists(destinationPath));

                var sourceStat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(source), source);

                sourceSize = sourceStat.Length;
                sourceLastWriteUtc = sourceStat.LastWriteUtc;

                tx = new JournalTransaction(_journal, _clock, new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Prepared,
                    source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow));
                await tx.BeginAsync().ConfigureAwait(false);

                if (request.Operation == FileOperationType.Copy)
                {
                    await Task.Run(() => _fileSystem.Copy(source, destinationPath), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (_moveOverride is not null)
                    {
                        await _moveOverride(source, destinationPath).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Run(() => _fileSystem.Move(source, destinationPath), cancellationToken).ConfigureAwait(false);
                    }
                }

                // Journaled after Prepared: persisted as a code + English, shown via ex.Message (UI language).
                tx.VerifyDestination(_fileSystem, destinationPath, JournalErrors.VerifySizeChanged);

                _ = tx.Commit(out var journalError);
                if (journalError is not null)
                {
                    return new FileActionResult(true, request.Operation, source, destinationPath,
                        sourceSize, sourceLastWriteUtc, null, JournalPersisted: false,
                        JournalError: journalError);
                }

                return new FileActionResult(
                    Succeeded: true,
                    Operation: request.Operation,
                    Source: source,
                    DestinationPath: destinationPath,
                    Size: sourceSize,
                    LastWriteUtc: sourceLastWriteUtc,
                    Error: null);
            }
            else if (request.Operation == FileOperationType.Recycle)
            {
                var source = request.Source;
                var sourceStat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(source), source);

                sourceSize = sourceStat.Length;
                sourceLastWriteUtc = sourceStat.LastWriteUtc;

                // Q-R8: on a drive without a Recycle Bin the file is only deleted (permanently) when the caller opted in
                // (setting + confirmation). Otherwise refuse BEFORE anything is journaled, exactly as R2-F-05 requires.
                permanent = !_recycleBin.CanRecycle(source);
                if (permanent && !request.AllowPermanentDelete)
                    throw new IOException(Tr.CoreRecycleUnsupportedDrive(Path.GetFileName(source)));

                tx = new JournalTransaction(_journal, _clock, new JournalEntry(
                    operationId,
                    FileOperationType.Recycle,
                    JournalState.Prepared,
                    source,
                    null,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow,
                    Permanent: permanent ? true : null));
                await tx.BeginAsync().ConfigureAwait(false);

                if (permanent)
                    await Task.Run(() => _recycleBin.DeletePermanently(source), cancellationToken).ConfigureAwait(false);
                else
                    await Task.Run(() => _recycleBin.SendToRecycleBin(source), cancellationToken).ConfigureAwait(false);
                tx.MarkMutationCompleted();

                _ = tx.Commit(out var journalError);
                if (journalError is not null)
                {
                    return new FileActionResult(true, FileOperationType.Recycle, source, null,
                        sourceSize, sourceLastWriteUtc, null, JournalPersisted: false,
                        JournalError: journalError, PermanentlyDeleted: permanent);
                }

                return new FileActionResult(
                    Succeeded: true,
                    Operation: FileOperationType.Recycle,
                    Source: source,
                    DestinationPath: null,
                    Size: sourceSize,
                    LastWriteUtc: sourceLastWriteUtc,
                    Error: null,
                    PermanentlyDeleted: permanent);
            }
            else
            {
                throw new NotSupportedException(Tr.CoreFileActionUnsupportedOperation(request.Operation));
            }
        }
        catch (Exception ex)
        {
            string? journalError = null;
            _ = tx?.Fail(ex, out journalError);
            var mutationCompleted = tx?.MutationCompleted ?? false;

            return new FileActionResult(
                Succeeded: mutationCompleted,
                Operation: request.Operation,
                Source: request.Source,
                DestinationPath: destinationPath,
                Size: sourceSize,
                LastWriteUtc: sourceLastWriteUtc,
                Error: mutationCompleted ? null : ex.Message,
                JournalPersisted: journalError is null,
                JournalError: journalError,
                PermanentlyDeleted: permanent && mutationCompleted);
        }
        finally
        {
            End();
        }
    }

    private static bool IsSamePath(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }
}
