using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Presenters;

/// <summary>What the Benchmark presenter needs from a window (WPF today, Win32 later). Called on the UI thread only.</summary>
internal interface IBenchmarkView
{
    /// <summary>The status line under the progress bar.</summary>
    void SetStatus(string text);

    /// <summary>The progress bar, 0..1.</summary>
    void SetProgress(double fraction);

    /// <summary>True while a run is active: Run, Quick check, Browse, the profile list, the limit and folder boxes are
    /// disabled and Cancel is enabled; false restores the idle state.</summary>
    void SetRunning(bool running);

    /// <summary>The result rows changed in place (best marker, language switch): repaint the grid.</summary>
    void RefreshRows();

    /// <summary>Opens Explorer with <paramref name="arguments"/> (a folder, or <c>/select,"file"</c>).</summary>
    void OpenInExplorer(string arguments);
}

/// <summary>One profile's measurement as the presenter drives it: created before the run, disposed after it (also when it fails or is cancelled).</summary>
internal interface IBenchmarkProfileSession : IAsyncDisposable
{
    Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken);
}

/// <summary>Creates the session for <paramref name="profile"/> over <paramref name="files"/> (the first thing a profile does, outside its failure handling).</summary>
internal delegate IBenchmarkProfileSession BenchmarkSessionFactory(BenchmarkProfile profile, IReadOnlyList<string> files);

/// <summary>Background scan of the folder (see <see cref="BenchmarkPresenter.EnumerateImageFiles"/>).</summary>
internal delegate string[] BenchmarkFileScan(string folder, int imageLimit, CancellationToken cancellationToken);

/// <summary>Writes the session's reports and returns the path of the summary file.</summary>
internal delegate Task<string> BenchmarkSummarySave(IReadOnlyList<BenchmarkReport> reports);

/// <summary>
/// WP-35 G2a: the Benchmark window's behaviour without a window -- profile list, Run / Quick check / Cancel, progress,
/// the result grid (ranking, best marker), report saving, and a close in the middle of a run. Moved from the WPF
/// code-behind <b>unchanged in order and conditions</b>; the window is modeless, so progress arrives from background
/// threads and is posted through <see cref="IUiScheduler"/>.
/// </summary>
/// <remarks>
/// Close/Dispose cancels the running benchmark; the run then ends through the normal cancel path (the token is captured
/// once, so a close between two profiles is "Canceled", never a bogus Fail row). A second Start while a run is active is
/// ignored. Progress that arrives after the run ended or was superseded is dropped, so it can never overwrite the final status.
/// </remarks>
internal sealed class BenchmarkPresenter : IDisposable
{
    /// <summary>"Quick check" always runs this profile with a shortened iteration count, whatever is selected.</summary>
    internal const string QuickCheckProfileId = "fast-sequential";
    internal const int QuickCheckIterations = 10;

    /// <summary>Default first-N image cap (mirrors the CLI's <c>--benchmark-all</c> <c>Take(64)</c>); 0 means "all".</summary>
    internal const int DefaultImageLimit = 64;

    /// <summary>How many top-level entries <see cref="EnumerateImageFiles"/> checks between cancellation polls: fine enough
    /// that Cancel/Close lands promptly on a huge or slow/NAS folder, coarse enough that the check is not a per-file cost.</summary>
    internal const int EnumerationCancellationCheckInterval = 256;

    private readonly IBenchmarkView _view;
    private readonly IUiScheduler _ui;
    private readonly BenchmarkSessionFactory _sessionFactory;
    private readonly BenchmarkFileScan _scan;
    private readonly BenchmarkSummarySave _save;
    private readonly ObservableCollection<BenchmarkResultRow> _rows = [];
    private readonly List<BenchmarkProfileItem> _profileItems = [.. BenchmarkProfiles.All.Select(p => new BenchmarkProfileItem(p))];
    private CancellationTokenSource? _cts;
    private string? _lastReport;
    private int _runId;

    public BenchmarkPresenter(IBenchmarkView view, IUiScheduler ui, BenchmarkSessionFactory sessionFactory,
        BenchmarkFileScan? scan = null, BenchmarkSummarySave? save = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _view = view;
        _ui = ui;
        _sessionFactory = sessionFactory;
        _scan = scan ?? EnumerateImageFiles;
        _save = save ?? (reports => BenchmarkReportStore.SaveSessionAsync(reports, BenchmarkReportStore.DefaultRoot, DateTime.UtcNow));
    }

    // ---- State the view binds to ----

    public IReadOnlyList<BenchmarkProfileItem> ProfileItems => _profileItems;

    /// <summary>The profile selected when the window opens.</summary>
    public BenchmarkProfileItem DefaultSelection => _profileItems.First(i => i.Profile.Id == QuickCheckProfileId);

    public ObservableCollection<BenchmarkResultRow> Rows => _rows;

    public bool IsRunning { get; private set; }

    public string? LastReportPath => _lastReport;

    /// <summary>The folder box's initial text.</summary>
    public static string InitialFolder(string? initialFolder) =>
        initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    public static string DefaultImageLimitText => DefaultImageLimit.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses the image-limit box: 0 (or blank/invalid/negative) means "all"; otherwise the first-N cap.</summary>
    public static int ParseImageLimit(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

    /// <summary>First-N cap applied to the folder's supported files, in the order the caller enumerated them; 0 (or a
    /// limit &gt;= the file count) means "all".</summary>
    internal static string[] ApplyImageLimit(IReadOnlyList<string> files, int limit) =>
        limit > 0 && files.Count > limit ? [.. files.Take(limit)] : [.. files];

    /// <summary>The exact profile "Quick check" runs: fast-sequential with a shortened iteration count (1 warm-up, its default).</summary>
    internal static BenchmarkProfile? BuildQuickCheckProfile() =>
        BenchmarkProfiles.Find(QuickCheckProfileId) is { } profile ? profile with { Iterations = QuickCheckIterations } : null;

    // ---- Commands ----

    /// <summary>Run: the selected profiles over the folder. No selection reports a status and starts nothing; a run in
    /// progress ignores the call.</summary>
    public Task RunSelectedAsync(IReadOnlyList<BenchmarkProfile> profiles, string folder, string? imageLimitText)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (IsRunning) return Task.CompletedTask;
        if (profiles.Count == 0)
        {
            _view.SetStatus(Tr.BenchmarkStatusNoProfileSelected);
            return Task.CompletedTask;
        }
        return RunAsync([.. profiles], folder, imageLimitText);
    }

    /// <summary>Quick check: a fixed fast smoke run that never looks at the profile selection.</summary>
    public Task QuickCheckAsync(string folder, string? imageLimitText)
    {
        if (IsRunning) return Task.CompletedTask;
        var profile = BuildQuickCheckProfile();
        if (profile is null)
        {
            _view.SetStatus(Tr.BenchErrorUnexpected($"Unknown profile '{QuickCheckProfileId}'"));
            return Task.CompletedTask;
        }
        return RunAsync([profile], folder, imageLimitText);
    }

    /// <summary>Cancel: stops the running benchmark (or its folder scan); nothing happens when idle.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>The window closed (or was disposed): cancel the run so the background work does not outlive it.</summary>
    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>The UI language changed: profile names/descriptions and the grid cells are code-provided texts.</summary>
    public void NotifyLanguageChanged()
    {
        foreach (var item in _profileItems) item.NotifyTextsChanged();
        _view.RefreshRows();
    }

    /// <summary>Open-report button: Explorer selects the last summary file when it still exists.</summary>
    public void OpenReport()
    {
        if (_lastReport is not null && File.Exists(_lastReport)) _view.OpenInExplorer($"/select,\"{_lastReport}\"");
    }

    /// <summary>Open-folder button: the last report's folder, or the temp folder before any run.</summary>
    public void OpenResultFolder()
    {
        var dir = _lastReport is null ? Path.GetTempPath() : Path.GetDirectoryName(_lastReport)!;
        _view.OpenInExplorer(dir);
    }

    // ---- Folder scan ----

    /// <summary>
    /// Background-thread scan: folder existence, the top-level file listing filtered to supported image types (enumeration
    /// order) and the image-count cap. <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> has no
    /// cancellation overload, so the token is polled every <see cref="EnumerationCancellationCheckInterval"/> entries.
    /// Camera RAW files are never listed even with RAW support on: the benchmark executor builds its preview service
    /// without the RAW-routed decoder, so a RAW would fail or measure the WIC fallback.
    /// </summary>
    internal static string[] EnumerateImageFiles(string folder, int imageLimit, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var supported = new List<string>();
        var seen = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (++seen % EnumerationCancellationCheckInterval == 0) cancellationToken.ThrowIfCancellationRequested();
            if (ImageFileTypes.IsSupported(path, rawEnabled: false)) supported.Add(path);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ApplyImageLimit(supported, imageLimit);
    }

    // ---- The run ----

    private async Task RunAsync(BenchmarkProfile[] profiles, string folder, string? imageLimitText)
    {
        // UI-thread reads were captured by the caller; DirectoryNotFoundException (thrown by the scan when the folder is
        // gone) is a subtype of IOException, so it must be caught ahead of the generic IOException clause below.
        var imageLimit = ParseImageLimit(imageLimitText);
        var runId = ++_runId;
        IsRunning = true;
        // Created before the scan so Cancel/Close can interrupt a slow enumeration too, not only the profile run.
        var cts = new CancellationTokenSource();
        _cts = cts;
        // Captured once: Dispose() (window closed mid-run) nulls _cts, and re-reading it per profile would throw an NRE
        // reported as a bogus Fail row. A cancelled token instead ends the run through the normal cancel path.
        var runToken = cts.Token;
        _view.SetRunning(true);
        _rows.Clear();
        _view.SetProgress(0);
        var sessionReports = new List<BenchmarkReport>();
        try
        {
            string[] files;
            try
            {
                files = await Task.Run(() => _scan(folder, imageLimit, runToken), runToken);
            }
            catch (DirectoryNotFoundException) { _view.SetStatus(Tr.BenchmarkStatusFolderMissing); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _view.SetStatus(Tr.BenchmarkStatusCannotReadFolder(ex.Message));
                return;
            }
            if (files.Length == 0) { _view.SetStatus(Tr.BenchmarkStatusNoImages); return; }

            var profileIndex = 0;
            foreach (var profile in profiles)
            {
                profileIndex++;
                var progress = new RunProgress(this, runId, profileIndex, profiles.Length);
                // Outside the profile's failure handling, as before: a factory failure ends the whole run.
                var session = _sessionFactory(profile, files);
                try
                {
                    var report = await session.RunAsync(folder, progress, runToken);
                    sessionReports.Add(report);
                    foreach (var phase in report.Phases) _rows.Add(new BenchmarkResultRow(profile, phase));
                    RecomputeBest();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Error($"Benchmark profile failed: {profile.Id}", ex);
                    _rows.Add(new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, profile.Workload, [], BenchmarkResultStatus.Fail, ex.Message), ex));
                    RecomputeBest();
                    _view.SetStatus(Tr.BenchmarkStatusProfileFailed(BenchmarkText.ProfileName(profile), BenchmarkText.Describe(ex)));
                }
                finally
                {
                    await session.DisposeAsync();
                }
            }
            if (sessionReports.Count > 0)
            {
                _lastReport = await _save(sessionReports);
                _view.SetStatus(Tr.BenchmarkStatusDone(sessionReports.Count, profiles.Length, _lastReport));
            }
        }
        catch (OperationCanceledException) { _view.SetStatus(Tr.BenchmarkStatusCanceled); }
        catch (Exception ex) { _view.SetStatus(Tr.BenchStatusFailed(BenchmarkText.Describe(ex))); }
        finally
        {
            IsRunning = false;
            _view.SetRunning(false);
            Dispose();
        }
    }

    /// <summary>Progress from the engine (a background thread): posted to the UI and dropped when its run is over.</summary>
    private sealed class RunProgress(BenchmarkPresenter owner, int runId, int index, int count) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => owner._ui.Post(() => owner.ApplyProgress(runId, index, count, value));
    }

    private void ApplyProgress(int runId, int index, int count, BenchmarkProgress p)
    {
        if (!IsRunning || runId != _runId) return;
        _view.SetProgress(p.Total == 0 ? 0 : (double)p.Completed / p.Total);
        _view.SetStatus(Tr.BenchmarkStatusProgress(index, count, p.ProfileId, p.Completed, p.Total, BenchmarkText.ProgressMessage(p.Message)));
    }

    /// <summary>Marks the lowest-P95 Pass row within each Workload group so profiles that share a workload (the actual
    /// "which mode is fastest" comparison) are visibly ranked, reusing the ordering the CLI/report ranking uses.</summary>
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
        _view.RefreshRows();
    }

    // ---- Export ----

    /// <summary>The grid as CSV (header + one line per row, cells as displayed, RFC 4180 quoting).</summary>
    public string ResultsToCsv()
    {
        string[] header =
        [
            BenchmarkResultRow.BestSymbol, Tr.BenchmarkColumnProfile, Tr.BenchmarkColumnLoadingMode, Tr.BenchmarkColumnWorkload,
            Tr.BenchmarkColumnSamples, "P50", "P95", "P99", "Max", Tr.BenchmarkColumnStatus,
        ];
        var sb = new StringBuilder();
        AppendCsvLine(sb, header);
        foreach (var row in _rows) AppendCsvLine(sb, row.CellTexts);
        return sb.ToString();
    }

    private static void AppendCsvLine(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var cell = cells[i];
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0) sb.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else sb.Append(cell);
        }
        sb.Append("\r\n");
    }
}

/// <summary>Source-generated (App.Shared is AOT-compatible) JSON for <c>summary.json</c>: indented, default names, as before.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(IReadOnlyList<BenchmarkReport>))]
internal sealed partial class BenchmarkSummaryJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Where and how a session's reports are written (temp folder, one file per profile plus <c>summary.json</c>).</summary>
internal static class BenchmarkReportStore
{
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports");

    /// <summary>File name of one profile's report; a report with no phases (nothing measured) must not throw here.</summary>
    public static string ReportFileName(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"{(report.Phases.Count > 0 ? report.Phases[0].ProfileId : "report")}-{report.RunId}.json";
    }

    /// <summary>Writes one file per report and <c>summary.json</c> under <c>&lt;root&gt;\yyyyMMdd-HHmmss</c>; returns the summary path.</summary>
    public static async Task<string> SaveSessionAsync(IReadOnlyList<BenchmarkReport> reports, string root, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var dir = Path.Combine(root, utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        foreach (var report in reports)
            await File.WriteAllTextAsync(Path.Combine(dir, ReportFileName(report)), report.ToJson());
        var summaryPath = Path.Combine(dir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(reports, BenchmarkSummaryJsonContext.Default.IReadOnlyListBenchmarkReport));
        return summaryPath;
    }
}

/// <summary>
/// The production session: the real image executor for one profile, with the profile's logging switch applied around the
/// run (without it the logging-on/off profiles measured whatever the ambient setting was). The per-iteration workload
/// lives in <see cref="BenchmarkWorkloadRunner"/> and is shared with the CLI.
/// </summary>
internal sealed class BenchmarkProfileSession : IBenchmarkProfileSession
{
    private readonly BenchmarkProfile _profile;
    private readonly string[] _files;
    private readonly BenchmarkImageExecutor _executor;
    private readonly Random _random;
    private readonly IDisposable _loggingScope;

    public BenchmarkProfileSession(BenchmarkProfile profile, IReadOnlyList<string> files, IPlatformImageCodec codec, IImageDecoder decoder)
    {
        _profile = profile;
        _files = [.. files];
        _executor = new BenchmarkImageExecutor(profile, _files, codec, decoder: decoder);
        _random = BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id);
        _loggingScope = BenchmarkProfileScope.ApplyLogging(profile, () => AppLog.Enabled, enabled => AppLog.Enabled = enabled, () => AppLog.EnabledWriteCount);
    }

    public static BenchmarkSessionFactory Factory(IPlatformImageCodec codec, Func<IImageDecoder> decoderFactory) =>
        (profile, files) => new BenchmarkProfileSession(profile, files, codec, decoderFactory());

    public Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken) =>
        BenchmarkEngine.RunPreparedAsync(folder, _profile,
            (_, workload, iteration, ct) => BenchmarkWorkloadRunner.PrepareIterationAsync(_executor, _files, _profile, workload, iteration, _random,
                BenchmarkRecycleBin.Create(() => WindowsRecycleBin.Instance), ct),
            progress, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Nested: a throwing logging restore must not skip the executor's disposal.
        try { _loggingScope.Dispose(); }
        finally { await _executor.DisposeAsync(); }
    }
}
