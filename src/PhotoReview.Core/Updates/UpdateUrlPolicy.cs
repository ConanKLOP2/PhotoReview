namespace PhotoReview.Core.Updates;

/// <summary>Only project release pages on github.com may be opened from the update check.</summary>
public static class UpdateUrlPolicy
{
    public const string ReleasesPageUrl = "https://github.com/ConanKLOP2/PhotoReview/releases";
    private const string ProjectPath = "/ConanKLOP2/PhotoReview";

    /// <summary>Returns the normalized absolute URL when <paramref name="url"/> is https://github.com/ConanKLOP2/PhotoReview[/...]; otherwise null.</summary>
    public static string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        var path = uri.AbsolutePath;
        var ok = string.Equals(path, ProjectPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(ProjectPath + "/", StringComparison.OrdinalIgnoreCase);
        return ok ? uri.AbsoluteUri : null;
    }
}
