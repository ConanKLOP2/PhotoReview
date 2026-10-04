using PhotoReview.Core.Settings;

namespace PhotoReview.Imaging;

/// <summary>
/// User-configurable preload window: how many images are decoded ahead of / behind the current one
/// while the user reviews (feat/preload-window-setting). Replaces the hard-coded
/// <see cref="PreloadOrderService.ForwardLookahead"/>/<see cref="PreloadOrderService.BackwardLookahead"/>
/// pair everywhere the scheduler, the order builder and the RAM-percent floor need a window size.
/// </summary>
public readonly record struct PreloadWindow(int Forward, int Backward)
{
    /// <summary>The historical hard-coded window (<see cref="PreloadOrderService.ForwardLookahead"/> / <see cref="PreloadOrderService.BackwardLookahead"/>).</summary>
    public static PreloadWindow Default => new(PreloadOrderService.ForwardLookahead, PreloadOrderService.BackwardLookahead);

    /// <summary>Current image plus both lookaheads -- the number of previews the window must be able to hold.</summary>
    public int ImageCount => Forward + Backward + 1;

    /// <summary>
    /// Builds the window from the user's settings (already normalized/clamped by <see cref="SettingsNormalizer"/>
    /// into the valid range), so composition (<c>App.xaml.cs</c>) has one pure, testable place to turn
    /// <see cref="AppSettings"/> into a <see cref="PreloadWindow"/>.
    /// </summary>
    public static PreloadWindow FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PreloadWindow(settings.PreloadForwardCount, settings.PreloadBackwardCount);
    }
}
