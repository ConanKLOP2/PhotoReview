using System.IO;

namespace PhotoReview.App;

public sealed record RecoveryRetryResult(bool Succeeded, string Message, JournalEntry? Entry);

public static class RecoveryRetryService
{
    public static RecoveryRetryResult RetryMoveOrCopy(JournalEntry failed, OperationJournal journal)
    {
        if (failed.Type is not ("Move" or "Copy"))
            return new(false, "Chỉ cho phép retry Move/Copy; Recycle Bin không được retry tự động.", null);
        if (string.IsNullOrWhiteSpace(failed.Destination))
            return new(false, "Operation không có đích.", null);
        if (!File.Exists(failed.Source))
            return new(false, "Nguồn không còn tồn tại.", null);
        var sourceInfo = new FileInfo(failed.Source);
        if (sourceInfo.Length != failed.Size || sourceInfo.LastWriteTimeUtc != failed.LastWriteUtc)
            return new(false, "Nguồn đã thay đổi; từ chối retry để bảo vệ dữ liệu.", null);
        if (File.Exists(failed.Destination))
            return new(false, "Đích đã tồn tại; không ghi đè.", null);

        var prepared = new JournalEntry(failed.Id, failed.Type, "Prepared", failed.Source, failed.Destination, sourceInfo.Length, sourceInfo.LastWriteTimeUtc, DateTime.UtcNow);
        try
        {
            journal.Append(prepared);
            Directory.CreateDirectory(Path.GetDirectoryName(failed.Destination)!);
            if (failed.Type == "Copy") File.Copy(failed.Source, failed.Destination);
            else File.Move(failed.Source, failed.Destination);
            var destinationInfo = new FileInfo(failed.Destination);
            if (destinationInfo.Length != prepared.Size) throw new IOException("Kiểm tra đích sau retry thất bại.");
            var committed = prepared with { State = "Committed", TimestampUtc = DateTime.UtcNow };
            journal.Append(committed);
            return new(true, "Retry thành công.", committed);
        }
        catch (Exception ex)
        {
            var error = prepared with { State = "Failed", TimestampUtc = DateTime.UtcNow, Error = ex.Message };
            journal.Append(error);
            return new(false, ex.Message, error);
        }
    }
}
