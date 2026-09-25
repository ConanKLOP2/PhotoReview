using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Yêu cầu thực thi thao tác tệp tin (Move, Copy, Recycle). <paramref name="AllowPermanentDelete"/> (Q-R8): chỉ đặt true sau khi người dùng
/// bật cài đặt và xác nhận; cho phép Recycle xóa vĩnh viễn trên ổ không có Thùng rác (mặc định false = từ chối như trước).
/// </summary>
public sealed record FileActionRequest(
    string Source,
    FileOperationType Operation,
    string? Destination = null,
    bool AllowPermanentDelete = false);
