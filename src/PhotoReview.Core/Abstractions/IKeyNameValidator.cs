namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc kiểm tra tính hợp lệ của tên phím bấm.
/// </summary>
public interface IKeyNameValidator
{
    /// <summary>
    /// Kiểm tra xem tên phím có hợp lệ trên nền tảng UI hay không.
    /// </summary>
    bool IsValidKeyName(string keyName);
}
