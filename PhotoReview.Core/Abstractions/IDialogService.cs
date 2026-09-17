namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc hiển thị hộp thoại xác nhận và thông báo cho người dùng.
/// </summary>
public interface IDialogService
{
    /// <summary>Hiển thị hộp thoại hỏi Có/Không hoặc Đồng ý/Hủy.</summary>
    bool ShowConfirmation(string title, string message);

    /// <summary>Hiển thị hộp thoại thông báo.</summary>
    void ShowMessage(string title, string message);
}
