using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Kết quả thực thi thao tác tệp tin.
/// </summary>
public sealed record FileActionResult(
    bool Succeeded,
    FileOperationType Operation,
    string Source,
    string? DestinationPath,
    long Size,
    DateTime LastWriteUtc,
    string? Error,
    bool Rejected = false,
    bool JournalPersisted = true,
    string? JournalError = null,
    bool PermanentlyDeleted = false,
    // F3: a FAILED Move whose source no longer exists on disk (e.g. the size differed after the move, so the result is
    // unverified). The file is at DestinationPath; the caller must not put Source back into the review catalog.
    // Always computed from the file system after the failure, never assumed; false for Copy/Recycle and successes.
    bool SourceRemoved = false);
