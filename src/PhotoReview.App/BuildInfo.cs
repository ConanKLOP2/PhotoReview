using System.Globalization;
using System.Reflection;
using PhotoReview.Core.Localization;

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
        if (string.IsNullOrWhiteSpace(informationalVersion)) return Tr.SettingsVersionUnknown;

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = plus < 0 ? informationalVersion : informationalVersion[..plus];
        var commit = plus < 0 ? null : informationalVersion[(plus + 1)..];

        // The " · " separator and the commit hash are language-neutral; only the labelled parts are translated.
        var parts = new List<string> { Tr.SettingsVersionNumber(version) };
        if (DateTimeOffset.TryParseExact(buildTime, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var built))
        {
            // Shown in the machine's local time (same clock as file and log timestamps).
            parts.Add(Tr.SettingsVersionBuild(built.ToLocalTime().ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrEmpty(commit)) parts.Add(commit);
        return string.Join(" · ", parts);
    }

    private static string? GetMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))?.Value;
}
