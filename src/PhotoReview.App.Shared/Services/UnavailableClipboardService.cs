namespace PhotoReview.App.Services;

/// <summary>
/// The clipboard of a <c>MainViewModel</c> built without an injected <see cref="IClipboardService"/> (unit tests, tools): every write
/// reports "unavailable", so the caller shows its status message. The shipped hosts always register a real service (WP-09).
/// </summary>
internal sealed class UnavailableClipboardService : IClipboardService
{
    public static readonly UnavailableClipboardService Instance = new();

    public bool TrySetText(string text) => false;
}
