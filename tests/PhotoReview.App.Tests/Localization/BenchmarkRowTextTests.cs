using System.ComponentModel;
using System.IO;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Localization;

/// <summary>
/// The code-provided texts of the Benchmark window: grid rows (<see cref="BenchmarkResultRow"/>), list items and the
/// <see cref="BenchmarkText"/> mappings. English is pinned for the duration of each test so the literal sentences can be
/// asserted; the ambient localizer is process-wide, hence GlobalState.
/// </summary>
[Collection("GlobalState")]
public sealed class BenchmarkRowTextTests : IDisposable
{
    private readonly IDisposable _english = TestLocalization.Use(TestLocalization.English);

    public void Dispose()
    {
        _english.Dispose();
        TestLocalization.UseVietnamese();
    }

    private static BenchmarkProfile Profile => BenchmarkProfiles.All[0];

    private static BenchmarkPhaseResult Phase(BenchmarkResultStatus status, string? message = null, double[]? samples = null, BenchmarkWorkload workload = BenchmarkWorkload.Sequential)
        => new(Profile.Id, workload, samples ?? [10, 20, 30, 40], status, message);

    [Fact]
    public void LoadingModeText_MapsEveryModeToItsOwnText_AndUnknownToItsName()
    {
        Assert.Equal(Tr.EnumLoadingModeFast, BenchmarkText.LoadingModeText(LoadingMode.Fast));
        Assert.Equal(Tr.EnumLoadingModePreview, BenchmarkText.LoadingModeText(LoadingMode.Preview));
        Assert.Equal(Tr.EnumLoadingModeOriginal, BenchmarkText.LoadingModeText(LoadingMode.Original));
        Assert.Equal(3, new[] { LoadingMode.Fast, LoadingMode.Preview, LoadingMode.Original }.Select(BenchmarkText.LoadingModeText).Distinct().Count());
        Assert.Equal("99", BenchmarkText.LoadingModeText((LoadingMode)99));
    }

    [Fact]
    public void WorkloadText_MapsEveryWorkloadToItsOwnText_AndUnknownToItsName()
    {
        var expected = new Dictionary<BenchmarkWorkload, string>
        {
            [BenchmarkWorkload.FirstFrame] = Tr.EnumBenchmarkWorkloadFirstFrame,
            [BenchmarkWorkload.Sequential] = Tr.EnumBenchmarkWorkloadSequential,
            [BenchmarkWorkload.Random] = Tr.EnumBenchmarkWorkloadRandom,
            [BenchmarkWorkload.WarmNext] = Tr.EnumBenchmarkWorkloadWarmNext,
            [BenchmarkWorkload.Preload] = Tr.EnumBenchmarkWorkloadPreload,
            [BenchmarkWorkload.FileAction] = Tr.EnumBenchmarkWorkloadFileAction,
            [BenchmarkWorkload.Correctness] = Tr.EnumBenchmarkWorkloadCorrectness,
        };

        foreach (var (workload, text) in expected) Assert.Equal(text, BenchmarkText.WorkloadText(workload));
        Assert.Equal(expected.Count, expected.Values.Distinct().Count());
        Assert.Equal("99", BenchmarkText.WorkloadText((BenchmarkWorkload)99));
    }

    [Fact]
    public void Describe_KnownProfileProblems_MapToTranslatedSentences()
    {
        var known = Profile;

        Assert.Equal(Tr.BenchErrorNotImplemented(BenchmarkText.ProfileName(known)),
            BenchmarkText.Describe(new BenchmarkProfileException(BenchmarkProfileProblem.NotImplemented, known.Id, "english")));
        Assert.Equal(Tr.BenchErrorNotImplemented("no-such-profile"),
            BenchmarkText.Describe(new BenchmarkProfileException(BenchmarkProfileProblem.NotImplemented, "no-such-profile", "english")));
        Assert.Equal(Tr.BenchErrorProfileIncomplete,
            BenchmarkText.Describe(new BenchmarkProfileException(BenchmarkProfileProblem.MissingIdOrMode, "", "english")));
        Assert.Equal(Tr.BenchErrorProfileSettings("p1"),
            BenchmarkText.Describe(new BenchmarkProfileException(BenchmarkProfileProblem.InvalidSettings, "p1", "english")));
        Assert.Equal("Unexpected error: disk on fire", BenchmarkText.Describe(new IOException("disk on fire")));
        Assert.Throws<ArgumentNullException>(() => BenchmarkText.Describe(null!));
    }

    [Fact]
    public void Row_ExposesTheProfileAndPhaseWithTranslatedTexts()
    {
        var phase = Phase(BenchmarkResultStatus.Pass, workload: BenchmarkWorkload.Random);

        var row = new BenchmarkResultRow(Profile, phase);

        Assert.Same(Profile, row.Profile);
        Assert.Equal(BenchmarkText.ProfileName(Profile), row.ProfileName);
        Assert.Equal(Profile.LoadingMode, row.LoadingMode);
        Assert.Equal(BenchmarkText.LoadingModeText(Profile.LoadingMode), row.LoadingModeText);
        Assert.Equal(BenchmarkWorkload.Random, row.Workload);
        Assert.Equal(BenchmarkText.WorkloadText(BenchmarkWorkload.Random), row.WorkloadText);
        Assert.Equal(4, row.Count);
        Assert.Equal(BenchmarkResultStatus.Pass, row.Status);
        Assert.Null(row.Error);
    }

    [Fact]
    public void Row_BestMarker_IsTheTrophyOnlyForTheBestRow()
    {
        var row = new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Pass));

        Assert.Equal("", row.BestMarker);
        row.IsBest = true;
        Assert.Equal(BenchmarkResultRow.BestSymbol, row.BestMarker);
        Assert.NotEmpty(BenchmarkResultRow.BestSymbol);
    }

    [Fact]
    public void Row_Percentiles_AreWholeMilliseconds_AndNoValueWithoutSamples()
    {
        var phase = Phase(BenchmarkResultStatus.Pass, samples: [.. Enumerable.Range(1, 100).Select(i => (double)i)]);
        var row = new BenchmarkResultRow(Profile, phase);
        string Ms(double value) => Tr.UnitMilliseconds(value.ToString("F0", System.Globalization.CultureInfo.CurrentCulture));

        Assert.Equal(Ms(phase.P50), row.P50Text);
        Assert.Equal(Ms(phase.P95), row.P95Text);
        Assert.Equal(Ms(phase.P99), row.P99Text);
        Assert.Equal(Ms(100), row.MaxText);
        Assert.Equal(4, new[] { row.P50Text, row.P95Text, row.P99Text, row.MaxText }.Distinct().Count()); // each column shows its own statistic

        var empty = new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.InsufficientData, samples: []));
        Assert.All(new[] { empty.P50Text, empty.P95Text, empty.P99Text, empty.MaxText }, text => Assert.Equal(Tr.BenchNoValue, text));
    }

    [Fact]
    public void Row_StatusText_MapsEachStatus()
    {
        Assert.Equal(Tr.EnumBenchmarkResultStatusPass, new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Pass)).StatusText);
        Assert.Equal(Tr.EnumBenchmarkResultStatusWarn, new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Warn)).StatusText);
        Assert.Equal(Tr.EnumBenchmarkResultStatusInsufficientData, new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.InsufficientData)).StatusText);
    }

    [Fact]
    public void Row_StatusText_ForAFailure_DependsOnTheMessageAndTheError()
    {
        Assert.Equal(Tr.EnumBenchmarkResultStatusFail,
            new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Fail)).StatusText);
        Assert.Equal(Tr.BenchmarkResultCorrectnessFailed,
            new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Fail, BenchmarkEngine.ResultCorrectnessFailed)).StatusText);
        // A library-reported message without an exception is shown as given, inside the translated wrapper.
        Assert.Equal(Tr.BenchResultFailedDetail("library said so"),
            new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Fail, "library said so")).StatusText);
        // With the exception behind it, the translated description replaces the English message.
        var error = new IOException("disk on fire");
        var withError = new BenchmarkResultRow(Profile, Phase(BenchmarkResultStatus.Fail, "english message"), error);
        Assert.Same(error, withError.Error);
        Assert.Equal(Tr.BenchResultFailedDetail(BenchmarkText.Describe(error)), withError.StatusText);
    }

    [Fact]
    public void ProfileItem_ShowsCatalogTexts_AndRaisesAnAllPropertiesChangeOnRefresh()
    {
        var item = new BenchmarkProfileItem(Profile);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.NotifyTextsChanged();

        Assert.Same(Profile, item.Profile);
        Assert.Equal(BenchmarkText.ProfileName(Profile), item.Name);
        Assert.Equal(BenchmarkText.ProfileDescription(Profile), item.Description);
        Assert.Equal([string.Empty], changed); // empty name = every property changed
    }
}
