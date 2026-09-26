namespace PhotoReview.Core.Updates;

/// <summary>
/// Manual "Check for updates". The only code in the app that touches the network, and only when the user clicks.
/// </summary>
public interface IUpdateChecker
{
    /// <param name="currentVersion">Running version, e.g. "2.0.93+1a2b3c4d" (metadata and a leading 'v' are tolerated).</param>
    Task<UpdateCheckResult> CheckAsync(string? currentVersion, CancellationToken cancellationToken);
}
