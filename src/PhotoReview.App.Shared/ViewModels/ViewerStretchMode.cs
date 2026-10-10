namespace PhotoReview.App.ViewModels;

/// <summary>
/// Chế độ hiển thị co giãn ảnh độc lập với WPF (tuân thủ quy tắc K-2).
/// Dời nguyên từ App/ViewModels/ViewerState.cs ở WP-01 vì hợp đồng C-08 (ViewportInput.Stretch) cần nó.
/// </summary>
public enum ViewerStretchMode
{
    None = 0,
    Uniform = 1
}
