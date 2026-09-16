using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

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

    private async Task RunAsync()
    {
        if (!Directory.Exists(FolderText.Text)) { StatusText.Text = "Folder không tồn tại."; return; }
        var profiles = ProfilesList.SelectedItems.Cast<BenchmarkProfile>().ToArray();
        if (profiles.Length == 0) { StatusText.Text = "Chọn ít nhất một cấu hình để chạy."; return; }
        var files = Directory.EnumerateFiles(FolderText.Text, "*.*", System.IO.SearchOption.TopDirectoryOnly).Where(ImageFileTypes.IsSupported).ToArray();
        if (files.Length == 0) { StatusText.Text = "Không tìm thấy ảnh."; return; }

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
                var executor = new BenchmarkImageExecutor();
                var random = new Random(profile.Id.GetHashCode());
                // DetailedLogging distinguishes the logging-on/logging-off profiles: without
                // toggling AppLog around the run, both profiles measured identical (whatever
                // the app's ambient setting happened to be) logging overhead.
                var previousLogEnabled = AppLog.Enabled;
                AppLog.Enabled = profile.DetailedLogging;
                try
                {
                    var engine = new BenchmarkEngine();
                    var report = await engine.RunAsync(FolderText.Text, profile, async (_, workload, iteration, ct) =>
                    {
                        if (workload == BenchmarkWorkload.Correctness && profile.Id is "explorer-reindex" or "cache-recovery")
                        {
                            // These profiles decoded a plain sequential file with no check of
                            // Explorer native order or of cache clear/rebuild behavior, so they
                            // could report Pass without ever exercising what their name claims.
                            // Refuse to run rather than keep reporting a misleading Pass; a real
                            // check needs to drive ExplorerOrderService/ClearCache from here.
                            throw new NotSupportedException(
                                $"Benchmark profile '{profile.Id}' does not implement a real {profile.Name} check yet; " +
                                "a decode-only stand-in would misreport results, so it refuses to run.");
                        }
                        if (workload == BenchmarkWorkload.FileAction)
                        {
                            var temp = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Action-" + Guid.NewGuid().ToString("N") + ".bin");
                            var moved = temp + ".moved";
                            var copied = temp + ".copy";
                            try
                            {
                                await File.WriteAllBytesAsync(temp, await File.ReadAllBytesAsync(files[iteration % files.Length], ct), ct);
                                var (actionImage, _) = await executor.DecodeAsync(temp, profile, ct);
                                // Each action profile now performs the operation its name promises
                                // instead of every Move/Delete/Copy/Interleaved profile running the
                                // same move+delete regardless of Id.
                                var op = profile.Id switch
                                {
                                    "action-delete" => "delete",
                                    "action-copy" => "copy",
                                    "action-interleaved" => (iteration % 3) switch { 0 => "move", 1 => "delete", _ => "copy" },
                                    _ => "move",
                                };
                                var sourceExistsAfter = true;
                                switch (op)
                                {
                                    case "move": File.Move(temp, moved); File.Delete(moved); break;
                                    case "delete": FileSystem.DeleteFile(temp, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); break;
                                    case "copy":
                                        File.Copy(temp, copied, overwrite: true);
                                        sourceExistsAfter = File.Exists(temp);
                                        break;
                                }
                                return (actionImage.PixelWidth > 0 && sourceExistsAfter, (ReviewMetricsSnapshot?)null);
                            }
                            finally
                            {
                                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                                try { if (File.Exists(moved)) File.Delete(moved); } catch { }
                                try { if (File.Exists(copied)) File.Delete(copied); } catch { }
                            }
                        }
                        if (workload == BenchmarkWorkload.FirstFrame)
                        {
                            var path = SelectFile(files, workload, iteration, random);
                            executor.EvictForColdDecode(path, profile);
                            var (image, _) = await executor.DecodeAsync(path, profile, ct);
                            return (image.PixelWidth > 0 && image.PixelHeight > 0, (ReviewMetricsSnapshot?)null);
                        }
                        // Workers now actually drives concurrent decodes for the remaining
                        // workloads (Sequential/Random/WarmNext/Preload/Correctness), instead of
                        // being parsed into the profile and never read by the runner.
                        var count = Math.Min(Math.Max(1, profile.Workers), files.Length);
                        var selected = Enumerable.Range(0, count).Select(o => SelectFile(files, workload, iteration + o, random)).ToArray();
                        var results = new bool[selected.Length];
                        await Parallel.ForEachAsync(Enumerable.Range(0, selected.Length),
                            new ParallelOptions { MaxDegreeOfParallelism = count, CancellationToken = ct },
                            async (idx, ct2) =>
                            {
                                var (image, _) = await executor.DecodeAsync(selected[idx], profile, ct2);
                                results[idx] = image.PixelWidth > 0 && image.PixelHeight > 0;
                            });
                        return (results.All(r => r), (ReviewMetricsSnapshot?)null);
                    }, progress, _cts.Token);
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
                finally { AppLog.Enabled = previousLogEnabled; }
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

    private static string SelectFile(string[] files, BenchmarkWorkload workload, int iteration, Random random) => workload switch
    {
        BenchmarkWorkload.FirstFrame => files[0],
        BenchmarkWorkload.Random => files[random.Next(files.Length)],
        BenchmarkWorkload.WarmNext => files[iteration % 2 == 0 ? iteration / 2 % files.Length : files.Length - 1 - iteration / 2 % files.Length],
        BenchmarkWorkload.Preload => files[iteration * 2 % files.Length],
        _ => files[iteration % files.Length],
    };

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
    public string LoadingMode => Profile.LoadingMode;
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
