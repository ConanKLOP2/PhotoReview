namespace PhotoReview.App.Services;

/// <summary>Writes text to the system clipboard; the seam lets view-model tests run without a real (shared, lockable) clipboard.</summary>
public interface IClipboardService
{
    /// <summary>Returns true when the text is on the clipboard; false when the clipboard stayed unavailable (never throws for that).</summary>
    bool TrySetText(string text);
}
