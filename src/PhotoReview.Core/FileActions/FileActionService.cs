using System.IO;
using PhotoReview.Core.Abstractions;
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

    private int _inProgress;

    public FileActionService(
        OperationJournal journal,
        IFileSystem fileSystem,
        IClock clock,
        IRecycleBin recycleBin)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
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
                Error: "Thao tác trước đó đang thực hiện.",
                Rejected: true);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var prepared = false;
        string? destinationPath = null;
        long sourceSize = 0;
        var sourceLastWriteUtc = DateTime.MinValue;

        try
        {
            if (request.Operation is FileOperationType.Move or FileOperationType.Copy)
            {
                if (string.IsNullOrWhiteSpace(request.Destination))
                    throw new IOException("Action chưa có thư mục đích.");

                var source = request.Source;
                var destinationFolder = Path.IsPathRooted(request.Destination)
                    ? request.Destination
                    : Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, request.Destination);

                destinationFolder = Path.GetFullPath(destinationFolder);
                var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source) ?? string.Empty);

                if (IsSamePath(destinationFolder, sourceFolder))
                    throw new IOException("Không thể Move/Copy vào chính folder nguồn.");

                _fileSystem.CreateDirectory(destinationFolder);
                destinationPath = Path.Combine(destinationFolder, Path.GetFileName(source));

                if (_fileSystem.FileExists(destinationPath))
                    throw new IOException($"Đích đã tồn tại: {destinationPath}");

                var sourceStat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException($"Nguồn không tồn tại: {source}", source);

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
                    await Task.Run(() => _fileSystem.Move(source, destinationPath), cancellationToken).ConfigureAwait(false);
                }

                var destStat = _fileSystem.GetFileStat(destinationPath);
                if (destStat is null || destStat.Length != sourceSize)
                {
                    throw new IOException("Kiểm tra sau thao tác thất bại: kích thước đích thay đổi.");
                }

                _journal.Append(new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Committed,
                    source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow));

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
                    ?? throw new FileNotFoundException($"Nguồn không tồn tại: {source}", source);

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

                _journal.Append(new JournalEntry(
                    operationId,
                    FileOperationType.Recycle,
                    JournalState.Committed,
                    source,
                    null,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow));

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
                throw new NotSupportedException($"Operation không được hỗ trợ: {request.Operation}");
            }
        }
        catch (Exception ex)
        {
            if (prepared)
            {
                _journal.Append(new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Failed,
                    request.Source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow,
                    ex.Message));
            }

            return new FileActionResult(
                Succeeded: false,
                Operation: request.Operation,
                Source: request.Source,
                DestinationPath: destinationPath,
                Size: sourceSize,
                LastWriteUtc: sourceLastWriteUtc,
                Error: ex.Message);
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
