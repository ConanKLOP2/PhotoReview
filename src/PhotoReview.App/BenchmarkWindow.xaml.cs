using PhotoReview.Benchmarking;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

public partial class BenchmarkWindow : Window
{
    private readonly ObservableCollection<BenchmarkResultRow> _rows = [];
    private CancellationTokenSource? _cts;
    private string? _lastReport;

    public BenchmarkWindow(string? initialFolder)
    {
        InitializeComponent();
        FolderText.Text = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        foreach (var p in BenchmarkProfiles.All) ProfilesList.Items.Add(p);
        ProfilesList.SelectedItems.Add(BenchmarkProfiles.All.First(p => p.Id == "fast-sequential"));
        ResultsGrid.ItemsSource = _rows;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Chọn folder ảnh thật để benchmark", SelectedPath = Directory.Exists(FolderText.Text) ? FolderText.Text : null };
        if (dialog.ShowDialog(new WindowHandle(this)) == Forms.DialogResult.OK) FolderText.Text = dialog.SelectedPath;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ProfilesList.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => ProfilesList.UnselectAll();

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // This window is opened modelessly (dialog.Show() in MainWindow), not via ShowDialog(),
    // so IsCancel/DialogResult cannot be used on the Đóng button -- WPF throws
    // InvalidOperationException when a non-dialog Window's DialogResult is set. Cancel any
    // running benchmark before closing so the background run doesn't outlive the window.
private void Close_Click(object sender, RoutedEventArgs e) => Close();

protected override void OnClosed(EventArgs e)
{
    _cts?.Cancel();
    base.OnClosed(e);
}

    private async Task RunAsync()
    {
        if (!Directory.Exists(FolderText.Text)) { StatusText.Text = "Folder không tồn tại."; return; }
        var profiles = ProfilesList.SelectedItems.Cast<BenchmarkProfile>().ToArray();
        if (profiles.Length == 0) { StatusText.Text = "Chọn ít nhất một cấu hình để chạy."; return; }
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(FolderText.Text, "*.*", System.IO.SearchOption.TopDirectoryOnly).Where(ImageFileTypes.IsSupported).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Không thể đọc folder: {ex.Message}";
            return;
        }
        if (files.Length == 0) { StatusText.Text = "Không tìm thấy ảnh."; return; }
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
                    StatusText.Text = $"[{currentIndex}/{profiles.Length}] {p.ProfileId}: {p.Completed}/{p.Total} — {p.Message}";
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
                    var engine = new BenchmarkEngine();
                    // The per-iteration workload logic (correctness guard, file-action mapping,
                    // preload warm-up) lives in BenchmarkWorkloadRunner and is shared with the
                    // CLI runner (PhotoReview.Tests/Program.cs) so both front ends exercise the
                    // same real behavior instead of the CLI running a decode-only stand-in.
                    var report = await engine.RunAsync(FolderText.Text, profile,
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
                    StatusText.Text = $"{profile.Name}: {ex.Message}";
                }
                finally { AppLog.Enabled = previousLogEnabled; await executor.DisposeAsync(); }
            }
            if (sessionReports.Count > 0)
            {
                await SaveSessionSummaryAsync(sessionReports);
                StatusText.Text = $"Hoàn tất {sessionReports.Count}/{profiles.Length} cấu hình — báo cáo: {_lastReport}";
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã hủy."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            RunButton.IsEnabled = true; CancelButton.IsEnabled = false; BrowseButton.IsEnabled = true; ProfilesList.IsEnabled = true;
            _cts?.Dispose(); _cts = null;
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
            var winnerId = BenchmarkRanking.Rank(phases, workload).FirstOrDefault()?.ProfileId;
            if (winnerId is null) continue;
            var winner = _rows.FirstOrDefault(r => r.Workload == workload && r.Phase.ProfileId == winnerId);
            if (winner is not null) winner.IsBest = true;
        }
        ResultsGrid.Items.Refresh();
    }

    private async Task SaveSessionSummaryAsync(IReadOnlyList<BenchmarkReport> reports)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        foreach (var report in reports)
            await File.WriteAllTextAsync(Path.Combine(dir, $"{report.Phases[0].ProfileId}-{report.RunId}.json"), report.ToJson());
        var summaryPath = Path.Combine(dir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
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

    private sealed class WindowHandle(Window window) : Forms.IWin32Window
    {
        public IntPtr Handle => new WindowInteropHelper(window).Handle;
    }
}

/// <summary>Presentation row for one profile's result in the comparison grid.
/// Wraps the profile and its raw <see cref="BenchmarkPhaseResult"/> so ranking
/// can reuse <see cref="BenchmarkRanking"/> instead of re-deriving P50/P95 order.</summary>
public sealed class BenchmarkResultRow(BenchmarkProfile profile, BenchmarkPhaseResult phase)
{
    public BenchmarkProfile Profile { get; } = profile;
    public BenchmarkPhaseResult Phase { get; } = phase;
    public bool IsBest { get; set; }

    public string ProfileName => Profile.Name;
    public LoadingMode LoadingMode => Profile.LoadingMode;
    public BenchmarkWorkload Workload => Phase.Workload;
    public int Count => Phase.Count;
    public BenchmarkResultStatus Status => Phase.Status;
    public string BestMarker => IsBest ? "🏆" : "";
    public string P50Text => Count == 0 ? "—" : $"{Phase.P50:F0} ms";
    public string P95Text => Count == 0 ? "—" : $"{Phase.P95:F0} ms";
    public string P99Text => Count == 0 ? "—" : $"{Phase.P99:F0} ms";
    public string MaxText => Count == 0 ? "—" : $"{Phase.Max:F0} ms";
    public string StatusText => Status switch
    {
        BenchmarkResultStatus.Pass => "Đạt",
        BenchmarkResultStatus.Warn => "Cảnh báo",
        BenchmarkResultStatus.Fail => Phase.Message ?? "Lỗi",
        _ => "Thiếu dữ liệu",
    };
}
