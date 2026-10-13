using PhotoReview.App.Services;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// <see cref="IZoomPromptService"/> (C-13) cho shell Win32 (WP-19b): hộp thoại zoom tuỳ chọn là cửa sổ WPF nạp muộn qua
/// <see cref="ISecondaryWindowPort"/> (owner = cửa sổ shell). Cầu vắng = null (như người dùng huỷ).
/// </summary>
internal sealed class Win32ZoomPromptService : IZoomPromptService
{
    private readonly Func<nint> _ownerProvider;
    private readonly Lazy<ISecondaryWindowPort?> _secondary;

    public Win32ZoomPromptService(Func<nint> ownerProvider, Lazy<ISecondaryWindowPort?> secondary)
    {
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
    }

    public int? PromptCustomZoom(int currentPercent)
    {
        try
        {
            return _secondary.Value?.PromptCustomZoom(_ownerProvider(), currentPercent);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null; // cầu hỏng/đã dọn: như người dùng huỷ (không đổi zoom)
        }
    }
}
