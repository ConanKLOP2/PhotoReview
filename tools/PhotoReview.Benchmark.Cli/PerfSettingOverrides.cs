using System.Globalization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// perf(harness) <c>--perf-session --set Key=Value</c> (repeatable): an in-memory override of one whitelisted
/// <see cref="AppSettings"/> property for the device-tuning matrix (docs/refactoring/perf/PLAN-device-config-bench.md).
/// Never touches config.json. Limits mirror <see cref="SettingsNormalizer"/> / <see cref="PerformanceOptions"/>:
/// a value the normalizer would silently clamp or reset is a hard error here, so a run can never measure
/// something other than what its command line says.
/// </summary>
internal static class PerfSettingOverrides
{
    private sealed record Spec(string Name, Func<string, Action<AppSettings>> Parse);

    private static readonly Spec[] Whitelist =
    [
        new(nameof(AppSettings.PreloadWorkerCount), v => Int(v, nameof(AppSettings.PreloadWorkerCount), 1, PerformanceOptions.MaxPreloadWorkerCount, (s, n) => s.PreloadWorkerCount = n)),
        new(nameof(AppSettings.PreloadForwardCount), v => Int(v, nameof(AppSettings.PreloadForwardCount), PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount, (s, n) => s.PreloadForwardCount = n)),
        new(nameof(AppSettings.PreloadBackwardCount), v => Int(v, nameof(AppSettings.PreloadBackwardCount), PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount, (s, n) => s.PreloadBackwardCount = n)),
        new(nameof(AppSettings.ImageCacheRamPercent), v => Int(v, nameof(AppSettings.ImageCacheRamPercent), PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent, (s, n) => s.ImageCacheRamPercent = n)),
        new(nameof(AppSettings.UseSourceBytesCache), v => Bool(v, nameof(AppSettings.UseSourceBytesCache), (s, b) => s.UseSourceBytesCache = b)),
        new(nameof(AppSettings.SourceBytesCapacityBytes), v => Long(v, nameof(AppSettings.SourceBytesCapacityBytes), 1, (s, n) => s.SourceBytesCapacityBytes = n)),
        new(nameof(AppSettings.PreviewDiskCacheCapacityBytes), v => Long(v, nameof(AppSettings.PreviewDiskCacheCapacityBytes), 0, (s, n) => s.PreviewDiskCacheCapacityBytes = n)),
        new(nameof(AppSettings.PreloadMemoryLoadLimit), v => Limit(v, nameof(AppSettings.PreloadMemoryLoadLimit), (s, d) => s.PreloadMemoryLoadLimit = d)),
        new(nameof(AppSettings.MemoryReserveBytes), v => Long(v, nameof(AppSettings.MemoryReserveBytes), 0, (s, n) => s.MemoryReserveBytes = n)),
        new(nameof(AppSettings.ScalingQuality), v =>
        {
            var q = BenchmarkCliArguments.ParseDefinedEnum<ScalingQuality>(v, nameof(AppSettings.ScalingQuality));
            return s => s.ScalingQuality = q;
        }),
        new(nameof(AppSettings.LoggingEnabled), v => Bool(v, nameof(AppSettings.LoggingEnabled), (s, b) => s.LoggingEnabled = b)),
    ];

    /// <summary>Whitelisted property names, in documentation order.</summary>
    internal static IReadOnlyList<string> AllowedKeys => Whitelist.Select(s => s.Name).ToArray();

    /// <summary>
    /// Parses one <c>Key=Value</c> argument. Throws <see cref="ArgumentException"/> for a malformed pair, a key
    /// outside the whitelist, or a bad/out-of-range value. Returns the canonical key and the in-memory apply action.
    /// </summary>
    internal static (string Key, Action<AppSettings> Apply) Parse(string pair)
    {
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0 || eq == pair.Length - 1)
            throw new ArgumentException($"--set expects Key=Value (got '{pair}')");
        var key = pair[..eq].Trim();
        var value = pair[(eq + 1)..].Trim();
        var spec = Whitelist.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"--set key '{key}' is not overridable (allowed: {string.Join(", ", AllowedKeys)})");
        return (spec.Name, spec.Parse(value));
    }

    /// <summary>Applies every override to <paramref name="settings"/> (in memory) and returns the EFFECTIVE value of each overridden key, read back from the settings object.</summary>
    internal static IReadOnlyDictionary<string, string> Apply(AppSettings settings, IEnumerable<KeyValuePair<string, Action<AppSettings>>> overrides)
    {
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, apply) in overrides)
        {
            apply(settings);
            var property = typeof(AppSettings).GetProperty(key)!;
            effective[key] = Convert.ToString(property.GetValue(settings), CultureInfo.InvariantCulture) ?? "";
        }
        return effective;
    }

    private static Action<AppSettings> Int(string text, string name, int min, int max, Action<AppSettings, int> set)
    {
        var n = BenchmarkCliArguments.ParseIntInRange(text, name, min, max);
        return s => set(s, n);
    }

    private static Action<AppSettings> Long(string text, string name, long min, Action<AppSettings, long> set)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min)
            throw new ArgumentException($"{name} must be a whole number >= {min} (got '{text}')");
        return s => set(s, n);
    }

    private static Action<AppSettings> Limit(string text, string name, Action<AppSettings, double> set)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !(d > 0 && d <= 1))
            throw new ArgumentException($"{name} must be a number in (0, 1] (got '{text}')");
        return s => set(s, d);
    }

    private static Action<AppSettings> Bool(string text, string name, Action<AppSettings, bool> set)
    {
        var b = text.ToLowerInvariant() switch
        {
            "true" or "on" => true,
            "false" or "off" => false,
            _ => throw new ArgumentException($"{name} must be true|false|on|off (got '{text}')"),
        };
        return s => set(s, b);
    }
}
