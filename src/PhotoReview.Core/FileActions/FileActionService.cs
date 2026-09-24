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
        var prepared = false;
        var mutationCompleted = false;
        string? destinationPath = null;
        long sourceSize = 0;
        var sourceLastWriteUtc = DateTime.MinValue;

        try
        {
            if (request.Operation is FileOperationType.Move or FileOperationType.Copy)
            {
                if (string.IsNullOrWhiteSpace(request.Destination))
                    throw new IOException(Tr.CoreFileActionNoDestination);

                var source = request.Source;
                var destinationFolder = Path.IsPathRooted(request.Destination)
                    ? request.Destination
                    : Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, request.Destination);

                destinationFolder = Path.GetFullPath(destinationFolder);
                var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source) ?? string.Empty);

                if (IsSamePath(destinationFolder, sourceFolder))
                    throw new IOException(Tr.CoreFileActionSameFolder);

                _fileSystem.CreateDirectory(destinationFolder);
                destinationPath = Path.Combine(destinationFolder, Path.GetFileName(source));

                if (_fileSystem.FileExists(destinationPath))
                    throw new IOException(Tr.CoreFileActionDestinationExists(destinationPath));

                var sourceStat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(source), source);

                sourceSize = sourceStat.Length;
                sourceLastWriteUtc = sourceStat.LastWriteUtc;

                _journal.Append(new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Prepared,
                    source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow));
                prepared = true;

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

                var destStat = _fileSystem.GetFileStat(destinationPath);
                if (destStat is null || destStat.Length != sourceSize)
                {
                    // Journaled after `prepared`: persisted as a code + English, shown via ex.Message (UI language).
                    throw new JournalCodedException(JournalErrors.VerifySizeChanged);
                }
                mutationCompleted = true;

                var committed = new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Committed,
                    source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow);
                try
                {
                    _journal.Append(committed);
                }
                catch (Exception journalException)
                {
                    return new FileActionResult(true, request.Operation, source, destinationPath,
                        sourceSize, sourceLastWriteUtc, null, JournalPersisted: false,
                        JournalError: journalException.Message);
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

                _journal.Append(new JournalEntry(
                    operationId,
                    FileOperationType.Recycle,
                    JournalState.Prepared,
                    source,
                    null,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow));
                prepared = true;

                await Task.Run(() => _recycleBin.SendToRecycleBin(source), cancellationToken).ConfigureAwait(false);
                mutationCompleted = true;

                var committed = new JournalEntry(
                    operationId,
                    FileOperationType.Recycle,
                    JournalState.Committed,
                    source,
                    null,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow);
                try
                {
                    _journal.Append(committed);
                }
                catch (Exception journalException)
                {
                    return new FileActionResult(true, FileOperationType.Recycle, source, null,
                        sourceSize, sourceLastWriteUtc, null, JournalPersisted: false,
                        JournalError: journalException.Message);
                }

                return new FileActionResult(
                    Succeeded: true,
                    Operation: FileOperationType.Recycle,
                    Source: source,
                    DestinationPath: null,
                    Size: sourceSize,
                    LastWriteUtc: sourceLastWriteUtc,
                    Error: null);
            }
            else
            {
                throw new NotSupportedException(Tr.CoreFileActionUnsupportedOperation(request.Operation));
            }
        }
        catch (Exception ex)
        {
            string? journalError = null;
            if (prepared)
            {
                try
                {
                    var (errorCode, errorText) = JournalErrors.ForJournal(ex);
                    _journal.Append(new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Failed,
                    request.Source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow,
                        errorText,
                        errorCode));
                }
                catch (Exception journalException)
                {
                    journalError = journalException.Message;
                }
            }

            return new FileActionResult(
                Succeeded: mutationCompleted,
                Operation: request.Operation,
                Source: request.Source,
                DestinationPath: destinationPath,
                Size: sourceSize,
                LastWriteUtc: sourceLastWriteUtc,
                Error: mutationCompleted ? null : ex.Message,
                JournalPersisted: journalError is null,
                JournalError: journalError);
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
