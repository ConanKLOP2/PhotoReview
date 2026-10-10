namespace PhotoReview.App.Services;

/// <summary>
/// C-13 (NO-WPF-EXEC-PLAN mục 5): hỏi mức zoom tuỳ chọn; thay <c>new ClickZoomCustomDialog</c> trong MainWindow.
/// Trả null khi người dùng huỷ. Thực thi: WP-19a (native) / WP-19b (qua WpfBridge).
/// </summary>
public interface IZoomPromptService
{
    int? PromptCustomZoom(int currentPercent);
}
