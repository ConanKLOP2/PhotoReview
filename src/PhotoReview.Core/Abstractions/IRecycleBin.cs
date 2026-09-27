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

    /// <summary>
    /// F-WIN-2: true only when the Recycle Bin of <paramref name="path"/>'s volume is known to accept a file of
    /// <paramref name="fileSize"/> bytes. False when that bin is turned off ("Don't move files to the Recycle Bin", policy),
    /// is smaller than the file, or its settings cannot be read: the shell would then delete the file PERMANENTLY without
    /// asking, so the caller must refuse instead of calling <see cref="SendToRecycleBin"/>. Only asked for paths where
    /// <see cref="CanRecycle"/> is true; the size is the caller's already-known stat (no extra file-system read).
    /// Default true for fakes.
    /// </summary>
    bool FitsInRecycleBin(string path, long fileSize) => true;
}
