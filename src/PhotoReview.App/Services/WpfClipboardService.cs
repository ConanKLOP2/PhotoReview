using System.Runtime.InteropServices;

namespace PhotoReview.App.Services;

/// <summary>
/// Real clipboard. <c>SetDataObject(.., copy: true)</c> already retries a few times while another process holds the clipboard
/// (CLIPBRD_E_CANT_OPEN); if it still fails we report false so the caller shows a status message instead of crashing.
/// </summary>
internal sealed class WpfClipboardService : IClipboardService
{
    public bool TrySetText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
