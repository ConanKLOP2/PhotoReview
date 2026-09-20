using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

public sealed record RecoveryRetryResult(
    bool Succeeded,
    string Message,
    JournalEntry? Entry,
    bool JournalPersisted = true,
    string? JournalError = null);

public sealed class RecoveryRetryService
{
    private readonly OperationJournal _journal;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;

    public RecoveryRetryService()
        : this(new OperationJournal(), new PhysicalFileSystem(), new SystemClock())
    {
    }

    public RecoveryRetryService(OperationJournal journal, IFileSystem fileSystem, IClock clock)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public RecoveryRetryResult RetryMoveOrCopy(JournalEntry failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        if (failed.Type is not (FileOperationType.Move or FileOperationType.Copy))
            return new(false, "Chỉ cho phép retry Move/Copy; Recycle Bin không được retry tự động.", null);
        if (string.IsNullOrWhiteSpace(failed.Destination))
            return new(false, "Operation không có đích.", null);
        if (!_fileSystem.FileExists(failed.Source))
            return new(false, "Nguồn không còn tồn tại.", null);

        var sourceStat = _fileSystem.GetFileStat(failed.Source);
        if (sourceStat is null || sourceStat.Length != failed.Size || sourceStat.LastWriteUtc != failed.LastWriteUtc)
            return new(false, "Nguồn đã thay đổi; từ chối retry để bảo vệ dữ liệu.", null);
        if (_fileSystem.FileExists(failed.Destination))
            return new(false, "Đích đã tồn tại; không ghi đè.", null);

        var prepared = new JournalEntry(
            failed.Id,
            failed.Type,
            JournalState.Prepared,
            failed.Source,
            failed.Destination,
            sourceStat.Length,
            sourceStat.LastWriteUtc,
            _clock.UtcNow);

        var mutationCompleted = false;
        try
        {
            _journal.Append(prepared);
            var destDir = Path.GetDirectoryName(failed.Destination);
            if (!string.IsNullOrEmpty(destDir))
            {
                _fileSystem.CreateDirectory(destDir);
            }

            if (failed.Type == FileOperationType.Copy)
                _fileSystem.Copy(failed.Source, failed.Destination);
            else
                _fileSystem.Move(failed.Source, failed.Destination);

            var destinationStat = _fileSystem.GetFileStat(failed.Destination);
            if (destinationStat is null || destinationStat.Length != prepared.Size)
                throw new IOException("Kiểm tra đích sau retry thất bại.");
            mutationCompleted = true;

            var committed = prepared with { State = JournalState.Committed, TimestampUtc = _clock.UtcNow };
            try
            {
                _journal.Append(committed);
                return new(true, "Retry thành công.", committed);
            }
            catch (Exception journalException)
            {
                return new(true, "Retry đã hoàn tất nhưng không ghi được nhật ký.", committed,
                    JournalPersisted: false, JournalError: journalException.Message);
            }
        }
        catch (Exception ex)
        {
            var error = prepared with { State = JournalState.Failed, TimestampUtc = _clock.UtcNow, Error = ex.Message };
            try
            {
                _journal.Append(error);
                return new(false, ex.Message, error);
            }
            catch (Exception journalException)
            {
                return new(mutationCompleted, mutationCompleted
                    ? "Thao tác đã hoàn tất nhưng không ghi được nhật ký thất bại."
                    : ex.Message, error, JournalPersisted: false, JournalError: journalException.Message);
            }
        }
    }

    public static RecoveryRetryResult RetryMoveOrCopy(JournalEntry failed, OperationJournal journal)
    {
        var service = new RecoveryRetryService(journal, new PhysicalFileSystem(), new SystemClock());
        return service.RetryMoveOrCopy(failed);
    }
}
