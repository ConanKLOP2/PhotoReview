namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc đưa tệp tin vào thùng rác hệ thống (Recycle Bin) và khôi phục.
/// </summary>
public interface IRecycleBin
{
    /// <summary>Chuyển tệp tin vào thùng rác với khả năng khôi phục.</summary>
    void SendToRecycleBin(string path);

    /// <summary>Thử khôi phục tệp tin từ thùng rác.</summary>
    bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc);
}
