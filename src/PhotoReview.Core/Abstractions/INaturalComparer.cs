namespace PhotoReview.Core.Abstractions;

/// <summary>
/// So sánh chuỗi theo thứ tự tự nhiên (ví dụ a2 nhỏ hơn a10), dùng để sắp xếp tệp tin giống Explorer.
/// </summary>
public interface INaturalComparer : IComparer<string?>
{
}
