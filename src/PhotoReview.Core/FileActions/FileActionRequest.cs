using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Yêu cầu thực thi thao tác tệp tin (Move, Copy, Recycle).
/// </summary>
public sealed record FileActionRequest(
    string Source,
    FileOperationType Operation,
    string? Destination = null);
