namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc đưa tệp tin vào thùng rác hệ thống (Recycle Bin) và khôi phục.
/// </summary>
public interface IRecycleBin
{
    /// <summary>Chuyển tệp tin vào thùng rác với khả năng khôi phục.</summary>
    void SendToRecycleBin(string path);

    /// <summary>
    /// True when <paramref name="path"/> lives on a volume that really has a Recycle Bin (local fixed drive). Removable, network,
    /// UNC and unknown volumes return false: <see cref="SendToRecycleBin"/> refuses them (R2-F-05). Default true for fakes.
    /// </summary>
    bool CanRecycle(string path) => true;

    /// <summary>
    /// Deletes <paramref name="path"/> permanently (no Recycle Bin, cannot be restored). Only called after the user enabled
    /// "allow permanent delete" AND confirmed (Q-R8); never for a path where <see cref="CanRecycle"/> is true.
    /// </summary>
    void DeletePermanently(string path) => throw new NotSupportedException();

    /// <summary>Thử khôi phục tệp tin từ thùng rác.</summary>
    bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc);
}
