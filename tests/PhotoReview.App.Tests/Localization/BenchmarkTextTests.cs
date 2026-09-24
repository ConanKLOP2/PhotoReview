using PhotoReview.Benchmarking;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Localization;

// L07 (Q-L4): the Benchmarking library keeps fixed-English profile texts (reports, CLI); the Benchmark
// window shows catalog text looked up by profile id. Switches the ambient localizer → GlobalState.
[Collection("GlobalState")]
public sealed class BenchmarkTextTests : IDisposable
{
    public void Dispose() => TestLocalization.UseVietnamese();

    [Theory]
    [InlineData("fast-sequential", "name", "benchmark.profile.fastSequential.name")]
    [InlineData("preview-high-quality", "description", "benchmark.profile.previewHighQuality.description")]
    [InlineData("ssd-throughput", "name", "benchmark.profile.ssdThroughput.name")]
    public void ProfileKey_CamelCasesTheId(string id, string field, string expected) =>
        Assert.Equal(expected, BenchmarkText.ProfileKey(id, field));

    [Fact]
    public void EveryProfile_HasCatalogKeys_AndEnglishMatchesTheLibraryText()
    {
        var english = TestLocalization.English;
        foreach (var profile in BenchmarkProfiles.All)
        {
            var nameKey = BenchmarkText.ProfileKey(profile.Id, "name");
            var descriptionKey = BenchmarkText.ProfileKey(profile.Id, "description");
            Assert.True(english.Contains(nameKey), $"en.json is missing {nameKey}");
            Assert.True(english.Contains(descriptionKey), $"en.json is missing {descriptionKey}");
            Assert.Equal(profile.Name, english.Get(nameKey));
            Assert.Equal(profile.Description, english.Get(descriptionKey));
        }
    }

    [Fact]
    public void ProfileTexts_FollowTheCurrentLanguage()
    {
        var profile = BenchmarkProfiles.Find("fast-sequential")!;

        TestLocalization.UseVietnamese();
        Assert.Equal("Liên tục chuyển sang ảnh tiếp theo", BenchmarkText.ProfileDescription(profile));
        Assert.Equal("Fast Sequential", BenchmarkText.ProfileName(profile));

        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Equal("Continuous Next", BenchmarkText.ProfileDescription(profile));
        }
    }

    [Fact]
    public void ProfileWithoutCatalogKey_FallsBackToTheLibraryText()
    {
        var profile = BenchmarkProfiles.All[0] with { Id = "custom-probe", Name = "Custom Probe", Description = "Library text" };

        Assert.Equal("Custom Probe", BenchmarkText.ProfileName(profile));
        Assert.Equal("Library text", BenchmarkText.ProfileDescription(profile));
    }

    [Fact]
    public void LibraryMessages_MapToCatalogText_OtherTextPassesThrough()
    {
        TestLocalization.UseVietnamese();
        Assert.Equal(TestLocalization.Vietnamese.Get(TrKeys.BenchmarkProgressOk), BenchmarkText.ProgressMessage(BenchmarkEngine.ProgressOk));
        Assert.Equal("boom", BenchmarkText.ProgressMessage("boom"));

        var profile = BenchmarkProfiles.Find("fast-sequential")!;
        var failed = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [], BenchmarkResultStatus.Fail, BenchmarkEngine.ResultCorrectnessFailed));
        var passed = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [1.0], BenchmarkResultStatus.Pass, null));
        Assert.Equal(Tr.BenchmarkResultCorrectnessFailed, failed.StatusText);
        Assert.Equal("Đạt", passed.StatusText);
        Assert.Equal(Tr.EnumBenchmarkWorkloadSequential, passed.WorkloadText);
        Assert.Equal(Tr.EnumLoadingModeFast, passed.LoadingModeText);
        Assert.Equal(LoadingMode.Fast, passed.LoadingMode);
    }
}
