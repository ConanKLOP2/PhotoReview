using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using PhotoReview.App;

namespace PhotoReview.App.Tests;

[Trait("Category", "HotPath")]
public sealed class BuildInfoTests
{
    [Fact]
    public void AppAssembly_InformationalVersion_IsGitDerivedVersionPlusShortSha()
    {
        var info = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // 2.0.N or 2.0.N-dev.A, then +8-char sha, optional .dirty (Directory.Build.targets).
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-dev\.\d+)?\+[0-9a-f]{8}(\.dirty)?$", RegexOptions.CultureInvariant), info);
    }

    [Fact]
    public void AppAssembly_BuildTime_IsLocalTimeWithOffset()
    {
        var buildTime = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "BuildTime").Value;

        var parsed = DateTimeOffset.ParseExact(buildTime!, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(parsed), parsed.Offset);
    }

    [Fact]
    public void Describe_ReleaseBuild_ShowsVersionLocalBuildTimeAndSha()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 24, 7, 15, 30));
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var buildTime = $"2026-09-24 07:15:30 {sign}{offset:hh\\:mm}";

        var text = BuildInfo.Describe("2.0.41+1a2b3c4d", buildTime);

        Assert.Equal("Phiên bản 2.0.41 · build 24/09/2026 07:15 · 1a2b3c4d", text);
    }

    [Fact]
    public void Describe_NoBuildTimeOrSha_ShowsVersionOnly()
    {
        Assert.Equal("Phiên bản 2.0.0-nogit", BuildInfo.Describe("2.0.0-nogit", null));
        Assert.Equal("Phiên bản không xác định", BuildInfo.Describe(null, null));
    }
}
