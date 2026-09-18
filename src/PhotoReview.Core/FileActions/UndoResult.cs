using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Kết quả thực thi hoàn tác thao tác tệp tin (Move hoặc Recycle).
/// </summary>
public sealed record UndoResult(
    bool Succeeded,
    FileOperationType? Operation,
    string Source,
    string? Destination,
    string? ErrorMessage,
    bool Rejected = false);