using System.Globalization;
using System.Reflection;

namespace PhotoReview.App;

/// <summary>
/// Version/build text for the UI, from the attributes written by Directory.Build.targets:
/// InformationalVersion "2.0.41+1a2b3c4d[.dirty]" and AssemblyMetadata "BuildTime" in local time with offset.
/// </summary>
internal static class BuildInfo
{
    public static string Describe(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var buildTime = GetMetadata(assembly, "BuildTime");
        return Describe(informational, buildTime);
    }

    internal static string Describe(string? informationalVersion, string? buildTime)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "Phiên bản không xác định";

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = plus < 0 ? informationalVersion : informationalVersion[..plus];
        var commit = plus < 0 ? null : informationalVersion[(plus + 1)..];

        var parts = new List<string> { $"Phiên bản {version}" };
        if (DateTimeOffset.TryParseExact(buildTime, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var built))
        {
            // Shown in the machine's local time (same clock as file and log timestamps).
            parts.Add($"build {built.ToLocalTime().ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrEmpty(commit)) parts.Add(commit);
        return string.Join(" · ", parts);
    }

    private static string? GetMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))?.Value;
}
