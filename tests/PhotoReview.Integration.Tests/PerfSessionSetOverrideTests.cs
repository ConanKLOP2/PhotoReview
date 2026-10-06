using PhotoReview.Benchmark.Cli;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Integration.Tests;

/// <summary>perf(harness): <c>--perf-session --set Key=Value</c> parsing, whitelist, range checks and the in-memory apply step.</summary>
public sealed class PerfSessionSetOverrideTests
{
    private static string[] Session(params string[] extra) => ["--perf-session", "scenario.json", "C:/photos", "C:/out", .. extra];

    [Fact(DisplayName = "--set is repeatable and keeps command-line order")]
    public void Set_Repeated_AllCollected()
    {
        var options = PerfSession.ParseArgs(Session("--set", "PreloadWorkerCount=4", "--set", "ScalingQuality=Linear", "--set", "LoggingEnabled=on"));
        Assert.Equal(["PreloadWorkerCount", "ScalingQuality", "LoggingEnabled"], options.SettingOverrides.Select(o => o.Key));
    }

    [Fact(DisplayName = "no --set leaves the override list empty (config.json wins)")]
    public void Set_Absent_Empty() => Assert.Empty(PerfSession.ParseArgs(Session()).SettingOverrides);

    [Theory(DisplayName = "--set accepts every whitelisted key with a valid value; key match is case-insensitive")]
    [InlineData("PreloadWorkerCount=12")]
    [InlineData("preloadforwardcount=64")]
    [InlineData("PreloadBackwardCount=0")]
    [InlineData("ImageCacheRamPercent=75")]
    [InlineData("UseSourceBytesCache=true")]
    [InlineData("SourceBytesCapacityBytes=4294967296")]
    [InlineData("PreviewDiskCacheCapacityBytes=0")]
    [InlineData("PreloadMemoryLoadLimit=0.95")]
    [InlineData("MemoryReserveBytes=1073741824")]
    [InlineData("ScalingQuality=Linear")]
    [InlineData("LoggingEnabled=false")]
    public void Set_Valid(string pair) => Assert.Single(PerfSession.ParseArgs(Session("--set", pair)).SettingOverrides);

    [Theory(DisplayName = "--set rejects keys outside the whitelist (including real but non-whitelisted AppSettings properties) and malformed pairs")]
    [InlineData("NoSuchSetting=1")]
    [InlineData("JournalDurability=Fast")]
    [InlineData("DecoderBackend=Wpf")]
    [InlineData("PreloadWorkerCount")]
    [InlineData("=4")]
    [InlineData("PreloadWorkerCount=")]
    public void Set_UnknownKeyOrMalformed_Throws(string pair) =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--set", pair)));

    [Fact(DisplayName = "--set without a value argument throws")]
    public void Set_MissingValue_Throws() => Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--set")));

    [Theory(DisplayName = "--set rejects bad and out-of-range values instead of letting the normalizer clamp them")]
    [InlineData("PreloadWorkerCount=0")]
    [InlineData("PreloadWorkerCount=65")]
    [InlineData("PreloadWorkerCount=-1")]
    [InlineData("PreloadWorkerCount=abc")]
    [InlineData("PreloadWorkerCount=4.5")]
    [InlineData("PreloadForwardCount=0")]
    [InlineData("PreloadForwardCount=501")]
    [InlineData("PreloadBackwardCount=-1")]
    [InlineData("ImageCacheRamPercent=0")]
    [InlineData("ImageCacheRamPercent=91")]
    [InlineData("SourceBytesCapacityBytes=0")]
    [InlineData("PreviewDiskCacheCapacityBytes=-1")]
    [InlineData("PreloadMemoryLoadLimit=0")]
    [InlineData("PreloadMemoryLoadLimit=1.5")]
    [InlineData("PreloadMemoryLoadLimit=NaN")]
    [InlineData("MemoryReserveBytes=-5")]
    [InlineData("ScalingQuality=99")]
    [InlineData("ScalingQuality=Bogus")]
    [InlineData("LoggingEnabled=maybe")]
    [InlineData("UseSourceBytesCache=1")]
    public void Set_BadOrOutOfRangeValue_Throws(string pair) =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--set", pair)));

    [Fact(DisplayName = "--set with the same key twice is an error")]
    public void Set_DuplicateKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--set", "PreloadWorkerCount=4", "--set", "preloadworkercount=8")));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "--set UseSourceBytesCache conflicts with --source-bytes-cache")]
    public void Set_ConflictsWithSourceBytesCacheFlag() =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--source-bytes-cache", "on", "--set", "UseSourceBytesCache=false")));

    [Fact(DisplayName = "other options are still rejected when repeated (only --set is exempt)")]
    public void OtherOptions_StillRejectRepeats() =>
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--repeat", "2", "--repeat", "3")));

    [Fact(DisplayName = "--set values reach the applied AppSettings object and are reported as effective values (invariant culture)")]
    public void Apply_OverridesReachSettings_AndAreReported()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
            var options = PerfSession.ParseArgs(Session(
                "--set", "PreloadWorkerCount=3", "--set", "PreloadForwardCount=7", "--set", "PreloadBackwardCount=2",
                "--set", "ImageCacheRamPercent=33", "--set", "UseSourceBytesCache=on", "--set", "SourceBytesCapacityBytes=123456",
                "--set", "PreviewDiskCacheCapacityBytes=0", "--set", "PreloadMemoryLoadLimit=0.85", "--set", "MemoryReserveBytes=999",
                "--set", "ScalingQuality=Linear", "--set", "LoggingEnabled=true"));
            var settings = new AppSettings
            {
                PreloadWorkerCount = PerformanceOptions.PreloadWorkerCount,
                ScalingQuality = ScalingQuality.HighQuality,
            };

            var effective = PerfSettingOverrides.Apply(settings, options.SettingOverrides);

            Assert.Equal(3, settings.PreloadWorkerCount);
            Assert.Equal(7, settings.PreloadForwardCount);
            Assert.Equal(2, settings.PreloadBackwardCount);
            Assert.Equal(33, settings.ImageCacheRamPercent);
            Assert.True(settings.UseSourceBytesCache);
            Assert.Equal(123456L, settings.SourceBytesCapacityBytes);
            Assert.Equal(0L, settings.PreviewDiskCacheCapacityBytes);
            Assert.Equal(0.85, settings.PreloadMemoryLoadLimit);
            Assert.Equal(999L, settings.MemoryReserveBytes);
            Assert.Equal(ScalingQuality.Linear, settings.ScalingQuality);
            Assert.True(settings.LoggingEnabled);
            Assert.Equal(11, effective.Count);
            Assert.Equal("3", effective["PreloadWorkerCount"]);
            Assert.Equal("0.85", effective["PreloadMemoryLoadLimit"]);
            Assert.Equal("Linear", effective["ScalingQuality"]);
            Assert.Equal("True", effective["UseSourceBytesCache"]);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact(DisplayName = "Apply with no overrides leaves every setting untouched")]
    public void Apply_NoOverrides_NoChange()
    {
        var settings = new AppSettings();
        var effective = PerfSettingOverrides.Apply(settings, []);
        Assert.Empty(effective);
        Assert.Equal(PerformanceOptions.PreloadWorkerCount, settings.PreloadWorkerCount);
    }
}
