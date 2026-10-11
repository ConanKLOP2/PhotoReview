using System.Windows;

namespace PhotoReview.App.Services;

/// <summary>
/// <see cref="IZoomPromptService"/> (C-13) cua app WPF: hop thoai "Click zoom level" &gt; "Custom..." (modal, owner = cua so
/// chinh) qua <see cref="SecondaryWindowFactory.PromptCustomZoom"/>. Thay <c>new ClickZoomCustomDialog</c> trong MainWindow (WP-19b).
/// </summary>
internal sealed class WpfZoomPromptService : IZoomPromptService
{
    private readonly Func<Window?> _ownerProvider;

    public WpfZoomPromptService(Func<Window?> ownerProvider) => _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));

    public int? PromptCustomZoom(int currentPercent) => SecondaryWindowFactory.PromptCustomZoom(_ownerProvider(), currentPercent);
}
