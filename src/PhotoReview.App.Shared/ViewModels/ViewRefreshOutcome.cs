using PhotoReview.App.Coordinators;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Q-TOUCHPAD-REFRESH: what the Refresh command changed on screen (<see cref="MainViewModel.RefreshView"/>).
/// </summary>
/// <param name="ScalingChanged">The main image was not drawn with HighQuality scaling before (ScalingQuality setting Linear).</param>
/// <param name="Resolution">What happened about the number of source pixels on screen.</param>
public sealed record ViewRefreshOutcome(bool ScalingChanged, ZoomDetailRefreshResult Resolution);
