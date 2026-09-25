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
    bool PermanentlyDeleted = false);
