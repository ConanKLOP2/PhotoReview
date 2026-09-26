namespace PhotoReview.Core.Updates;

public enum UpdateCheckStatus { UpToDate, UpdateAvailable, Failed }

/// <summary>Why a manual update check failed. Mapped to a translated message by the UI.</summary>
public enum UpdateFailure { None, Offline, Timeout, RateLimited, BadResponse, InvalidVersion }

/// <summary>Outcome of <see cref="IUpdateChecker.CheckAsync"/>. Never carries user data.</summary>
public sealed record UpdateCheckResult
{
    public UpdateCheckStatus Status { get; private init; }
    /// <summary>Latest published version (numeric "major.minor.patch") for UpToDate/UpdateAvailable.</summary>
    public string? LatestVersion { get; private init; }
    /// <summary>Download page for UpdateAvailable; always an allow-listed project URL.</summary>
    public string? DownloadUrl { get; private init; }
    public UpdateFailure Failure { get; private init; }

    public static UpdateCheckResult UpToDate(string latestVersion) =>
        new() { Status = UpdateCheckStatus.UpToDate, LatestVersion = latestVersion };

    public static UpdateCheckResult UpdateAvailable(string latestVersion, string downloadUrl) =>
        new() { Status = UpdateCheckStatus.UpdateAvailable, LatestVersion = latestVersion, DownloadUrl = downloadUrl };

    public static UpdateCheckResult Failed(UpdateFailure failure) =>
        new() { Status = UpdateCheckStatus.Failed, Failure = failure };
}
