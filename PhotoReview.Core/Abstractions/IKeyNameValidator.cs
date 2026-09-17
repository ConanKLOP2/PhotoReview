namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc kiểm tra hợp lệ của tên phím tắt hoặc tên định danh.
/// </summary>
public interface IKeyNameValidator
{
    /// <summary>Kiểm tra xem chuỗi đại diện phím có hợp lệ hay không.</summary>
    bool IsValidKeyName(string keyName);
}
