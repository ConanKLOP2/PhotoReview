using System.ComponentModel;
using System.Globalization;
using System.Text;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

/// <summary>Presentation row for one profile's result in the comparison grid (WP-35 G2a: moved unchanged from the WPF
/// code-behind to App.Shared; it has no UI type). Wraps the profile and its raw <see cref="BenchmarkPhaseResult"/> so
/// ranking can reuse <see cref="BenchmarkRanking"/> instead of re-deriving P50/P95 order.</summary>
public sealed class BenchmarkResultRow(BenchmarkProfile profile, BenchmarkPhaseResult phase, Exception? error = null)
{
    public BenchmarkProfile Profile { get; } = profile;
    /// <summary>Trophy marker for the best row; also the (language-neutral) column header.</summary>
    public const string BestSymbol = "🏆";

    public BenchmarkPhaseResult Phase { get; } = phase;
    public bool IsBest { get; set; }
    /// <summary>The exception behind a failed profile (null for library-reported results); used for translated text.</summary>
    public Exception? Error { get; } = error;

    public string ProfileName => BenchmarkText.ProfileName(Profile);
    public LoadingMode LoadingMode => Profile.LoadingMode;
    public string LoadingModeText => BenchmarkText.LoadingModeText(LoadingMode);
    public BenchmarkWorkload Workload => Phase.Workload;
    public string WorkloadText => BenchmarkText.WorkloadText(Workload);
    public int Count => Phase.Count;
    public BenchmarkResultStatus Status => Phase.Status;
    public string BestMarker => IsBest ? BestSymbol : "";
    public string P50Text => MillisecondsText(Phase.P50);
    public string P95Text => MillisecondsText(Phase.P95);
    public string P99Text => MillisecondsText(Phase.P99);
    public string MaxText => MillisecondsText(Phase.Max);
    private string MillisecondsText(double value) => Count == 0 ? Tr.BenchNoValue : Tr.UnitMilliseconds(value.ToString("F0", CultureInfo.CurrentCulture));
    public string StatusText => Status switch
    {
        BenchmarkResultStatus.Pass => Tr.EnumBenchmarkResultStatusPass,
        BenchmarkResultStatus.Warn => Tr.EnumBenchmarkResultStatusWarn,
        BenchmarkResultStatus.Fail => Phase.Message switch
        {
            null => Tr.EnumBenchmarkResultStatusFail,
            BenchmarkEngine.ResultCorrectnessFailed => Tr.BenchmarkResultCorrectnessFailed,
            var message => Tr.BenchResultFailedDetail(Error is null ? message : BenchmarkText.Describe(Error)),
        },
        _ => Tr.EnumBenchmarkResultStatusInsufficientData,
    };

    /// <summary>The ten grid cells as shown, in column order (best marker, profile, loading mode, workload, samples,
    /// P50, P95, P99, Max, status): what a non-WPF grid or the CSV export renders.</summary>
    public IReadOnlyList<string> CellTexts =>
    [
        BestMarker, ProfileName, LoadingModeText, WorkloadText, Count.ToString(CultureInfo.CurrentCulture),
        P50Text, P95Text, P99Text, MaxText, StatusText,
    ];
}

/// <summary>List item for one profile: the library keeps the English name/description (reports, CLI --
/// Q-L4); the window shows the catalog text for the current language.</summary>
public sealed class BenchmarkProfileItem(BenchmarkProfile profile) : INotifyPropertyChanged
{
    public BenchmarkProfile Profile { get; } = profile;
    public string Name => BenchmarkText.ProfileName(Profile);
    public string Description => BenchmarkText.ProfileDescription(Profile);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void NotifyTextsChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Display texts for the Benchmark window. Profile texts are looked up by id with the key pattern
/// <c>benchmark.profile.&lt;camelCaseId&gt;.name|description</c> (e.g. <c>fast-sequential</c> →
/// <c>benchmark.profile.fastSequential.name</c>); a profile without a key shows the library's English text.</summary>
public static class BenchmarkText
{
    public static string ProfileName(BenchmarkProfile profile) => Lookup(ProfileKey(profile.Id, "name"), profile.Name);

    public static string ProfileDescription(BenchmarkProfile profile) => Lookup(ProfileKey(profile.Id, "description"), profile.Description);

    /// <summary>"fast-sequential" + "name" → "benchmark.profile.fastSequential.name".</summary>
    public static string ProfileKey(string profileId, string field)
    {
        var sb = new StringBuilder("benchmark.profile.", 64);
        var upper = false;
        foreach (var c in profileId)
        {
            if (c == '-') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.Append('.').Append(field).ToString();
    }

    /// <summary>Maps the library's fixed-English progress messages to catalog text; others pass through.</summary>
    public static string ProgressMessage(string message) => message switch
    {
        BenchmarkEngine.ProgressOk => Tr.BenchmarkProgressOk,
        BenchmarkEngine.ProgressCorrectnessFailed => Tr.BenchmarkProgressCorrectnessFailed,
        _ => message,
    };

    public static string LoadingModeText(LoadingMode mode) => mode switch
    {
        LoadingMode.Fast => Tr.EnumLoadingModeFast,
        LoadingMode.Preview => Tr.EnumLoadingModePreview,
        LoadingMode.Original => Tr.EnumLoadingModeOriginal,
        _ => mode.ToString(),
    };

    public static string WorkloadText(BenchmarkWorkload workload) => workload switch
    {
        BenchmarkWorkload.FirstFrame => Tr.EnumBenchmarkWorkloadFirstFrame,
        BenchmarkWorkload.Sequential => Tr.EnumBenchmarkWorkloadSequential,
        BenchmarkWorkload.Random => Tr.EnumBenchmarkWorkloadRandom,
        BenchmarkWorkload.WarmNext => Tr.EnumBenchmarkWorkloadWarmNext,
        BenchmarkWorkload.Preload => Tr.EnumBenchmarkWorkloadPreload,
        BenchmarkWorkload.FileAction => Tr.EnumBenchmarkWorkloadFileAction,
        BenchmarkWorkload.Correctness => Tr.EnumBenchmarkWorkloadCorrectness,
        _ => workload.ToString(),
    };

    /// <summary>Translated sentence for a failed benchmark: known library problems map to catalog text, any other
    /// exception keeps its (OS/technical) message inside a translated wrapper.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            BenchmarkProfileException { Problem: BenchmarkProfileProblem.NotImplemented } p =>
                Tr.BenchErrorNotImplemented(BenchmarkProfiles.Find(p.ProfileId) is { } profile ? ProfileName(profile) : p.ProfileId),
            BenchmarkProfileException { Problem: BenchmarkProfileProblem.MissingIdOrMode } => Tr.BenchErrorProfileIncomplete,
            BenchmarkProfileException p => Tr.BenchErrorProfileSettings(p.ProfileId),
            _ => Tr.BenchErrorUnexpected(ex.Message),
        };
    }

    private static string Lookup(string key, string fallback)
    {
        var localizer = Localizer.Current;
        return localizer.Contains(key) ? localizer.Get(key) : fallback;
    }
}
