using PhotoReview.Benchmarking;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

public partial class BenchmarkWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ObservableCollection<BenchmarkResultRow> _rows = [];
    private readonly List<BenchmarkProfileItem> _profileItems = [.. BenchmarkProfiles.All.Select(p => new BenchmarkProfileItem(p))];
    private CancellationTokenSource? _cts;
    private string? _lastReport;

    public BenchmarkWindow(string? initialFolder)
    {
        InitializeComponent();
        FolderText.Text = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        foreach (var item in _profileItems) ProfilesList.Items.Add(item);
        ProfilesList.SelectedItems.Add(_profileItems.First(i => i.Profile.Id == "fast-sequential"));
        ResultsGrid.ItemsSource = _rows;
        Localizer.CurrentChanged += OnLanguageChanged;
    }

    // XAML texts follow a language switch through {loc:Tr}; the profile list and the grid cells are
    // code-provided, so refresh them here (the window is modeless and can stay open across a switch).
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        foreach (var item in _profileItems) item.NotifyTextsChanged();
        ResultsGrid.Items.Refresh();
    });

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Tr.BenchmarkPickFolderTitle };
        if (Directory.Exists(FolderText.Text)) dialog.InitialDirectory = FolderText.Text;
        if (dialog.ShowDialog(this) == true) FolderText.Text = dialog.FolderName;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ProfilesList.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => ProfilesList.UnselectAll();

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // This window is opened modelessly (dialog.Show() in MainWindow), not via ShowDialog(),
    // so IsCancel/DialogResult cannot be used on the Close button -- WPF throws
    // InvalidOperationException when a non-dialog Window's DialogResult is set. Cancel any
    // running benchmark before closing so the background run doesn't outlive the window.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task RunAsync()
    {
        if (!Directory.Exists(FolderText.Text)) { StatusText.Text = Tr.BenchmarkStatusFolderMissing; return; }
        var profiles = ProfilesList.SelectedItems.Cast<BenchmarkProfileItem>().Select(i => i.Profile).ToArray();
        if (profiles.Length == 0) { StatusText.Text = Tr.BenchmarkStatusNoProfileSelected; return; }
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(FolderText.Text, "*.*", System.IO.SearchOption.TopDirectoryOnly).Where(ImageFileTypes.IsSupported).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Tr.BenchmarkStatusCannotReadFolder(ex.Message);
            return;
        }
        if (files.Length == 0) { StatusText.Text = Tr.BenchmarkStatusNoImages; return; }
        var totalSourceBytes = files.Sum(path => { try { return new FileInfo(path).Length; } catch { return 0L; } });

        RunButton.IsEnabled = false; CancelButton.IsEnabled = true; BrowseButton.IsEnabled = false; ProfilesList.IsEnabled = false;
        _rows.Clear();
        RunProgress.Value = 0;
        _cts = new CancellationTokenSource();
        var sessionReports = new List<BenchmarkReport>();
        try
        {
            var profileIndex = 0;
            foreach (var profile in profiles)
            {
                profileIndex++;
                var currentIndex = profileIndex;
                var progress = new Progress<BenchmarkProgress>(p =>
                {
                    RunProgress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
                    StatusText.Text = Tr.BenchmarkStatusProgress(currentIndex, profiles.Length, p.ProfileId, p.Completed, p.Total, BenchmarkText.ProgressMessage(p.Message));
                });
                var executor = new BenchmarkImageExecutor(profile, files, totalSourceBytes);
                var random = BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id);
                // DetailedLogging distinguishes the logging-on/logging-off profiles: without
                // toggling AppLog around the run, both profiles measured identical (whatever
                // the app's ambient setting happened to be) logging overhead.
                var previousLogEnabled = AppLog.Enabled;
                AppLog.Enabled = profile.DetailedLogging;
                try
                {
                    // The per-iteration workload logic (correctness guard, file-action mapping,
                    // preload warm-up) lives in BenchmarkWorkloadRunner and is shared with the
                    // CLI runner (PhotoReview.Benchmark.Cli/Program.cs) so both front ends exercise the
                    // same real behavior instead of the CLI running a decode-only stand-in.
                    var report = await BenchmarkEngine.RunAsync(FolderText.Text, profile,
                        (_, workload, iteration, ct) => BenchmarkWorkloadRunner.RunIterationAsync(executor, files, profile, workload, iteration, random, ct),
                        progress, _cts.Token);
                    sessionReports.Add(report);
                    foreach (var phase in report.Phases) _rows.Add(new BenchmarkResultRow(profile, phase));
                    RecomputeBest();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Error($"Benchmark profile failed: {profile.Id}", ex);
                    _rows.Add(new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [], BenchmarkResultStatus.Fail, ex.Message)));
                    RecomputeBest();
                    StatusText.Text = Tr.BenchmarkStatusProfileFailed(BenchmarkText.ProfileName(profile), ex.Message);
                }
                finally { AppLog.Enabled = previousLogEnabled; await executor.DisposeAsync(); }
            }
            if (sessionReports.Count > 0)
            {
                await SaveSessionSummaryAsync(sessionReports);
                StatusText.Text = Tr.BenchmarkStatusDone(sessionReports.Count, profiles.Length, _lastReport);
            }
        }
        catch (OperationCanceledException) { StatusText.Text = Tr.BenchmarkStatusCanceled; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            RunButton.IsEnabled = true; CancelButton.IsEnabled = false; BrowseButton.IsEnabled = true; ProfilesList.IsEnabled = true;
            Dispose();
        }
    }

    /// <summary>Marks the lowest-P95 Pass row within each Workload group so profiles
    /// that share a workload (the actual "which mode is fastest" comparison) are
    /// visibly ranked, reusing the same ordering the CLI/report ranking uses.</summary>
    private void RecomputeBest()
    {
        foreach (var row in _rows) row.IsBest = false;
        var phases = _rows.Select(r => r.Phase).ToArray();
        foreach (var workload in _rows.Select(r => r.Workload).Distinct())
        {
            var ranked = BenchmarkRanking.Rank(phases, workload);
            var winnerId = ranked.Count > 0 ? ranked[0].ProfileId : null;
            if (winnerId is null) continue;
            var winner = _rows.FirstOrDefault(r => r.Workload == workload && r.Phase.ProfileId == winnerId);
            if (winner is not null) winner.IsBest = true;
        }
        ResultsGrid.Items.Refresh();
    }

    private async Task SaveSessionSummaryAsync(IReadOnlyList<BenchmarkReport> reports)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        foreach (var report in reports)
            await File.WriteAllTextAsync(Path.Combine(dir, $"{report.Phases[0].ProfileId}-{report.RunId}.json"), report.ToJson());
        var summaryPath = Path.Combine(dir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(reports, JsonOptions));
        _lastReport = summaryPath;
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is not null && File.Exists(_lastReport)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastReport}\"") { UseShellExecute = true });
    }

    private void OpenResultFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastReport is null ? Path.GetTempPath() : Path.GetDirectoryName(_lastReport)!;
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }
}

/// <summary>Presentation row for one profile's result in the comparison grid.
/// Wraps the profile and its raw <see cref="BenchmarkPhaseResult"/> so ranking
/// can reuse <see cref="BenchmarkRanking"/> instead of re-deriving P50/P95 order.</summary>
public sealed class BenchmarkResultRow(BenchmarkProfile profile, BenchmarkPhaseResult phase)
{
    public BenchmarkProfile Profile { get; } = profile;
    /// <summary>Trophy marker for the best row; also the (language-neutral) column header.</summary>
    public const string BestSymbol = "🏆";

    public BenchmarkPhaseResult Phase { get; } = phase;
    public bool IsBest { get; set; }

    public string ProfileName => BenchmarkText.ProfileName(Profile);
    public LoadingMode LoadingMode => Profile.LoadingMode;
    public string LoadingModeText => BenchmarkText.LoadingModeText(LoadingMode);
    public BenchmarkWorkload Workload => Phase.Workload;
    public string WorkloadText => BenchmarkText.WorkloadText(Workload);
    public int Count => Phase.Count;
    public BenchmarkResultStatus Status => Phase.Status;
    public string BestMarker => IsBest ? BestSymbol : "";
    public string P50Text => Count == 0 ? "—" : $"{Phase.P50:F0} ms";
    public string P95Text => Count == 0 ? "—" : $"{Phase.P95:F0} ms";
    public string P99Text => Count == 0 ? "—" : $"{Phase.P99:F0} ms";
    public string MaxText => Count == 0 ? "—" : $"{Phase.Max:F0} ms";
    public string StatusText => Status switch
    {
        BenchmarkResultStatus.Pass => Tr.EnumBenchmarkResultStatusPass,
        BenchmarkResultStatus.Warn => Tr.EnumBenchmarkResultStatusWarn,
        BenchmarkResultStatus.Fail => Phase.Message switch
        {
            null => Tr.EnumBenchmarkResultStatusFail,
            BenchmarkEngine.ResultCorrectnessFailed => Tr.BenchmarkResultCorrectnessFailed,
            var message => message, // exception text (pass-through)
        },
        _ => Tr.EnumBenchmarkResultStatusInsufficientData,
    };
}

/// <summary>List item for one profile: the library keeps the English name/description (reports, CLI —
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

    private static string Lookup(string key, string fallback)
    {
        var localizer = Localizer.Current;
        return localizer.Contains(key) ? localizer.Get(key) : fallback;
    }
}
