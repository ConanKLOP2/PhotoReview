using System.IO;
using System.Windows;
using Controls = System.Windows.Controls;
using System.Diagnostics;

namespace PhotoReview.App;

public sealed class BenchmarkWindow : Window
{
    private readonly Controls.TextBox _folder = new() { MinWidth = 520 };
    private readonly Controls.ListBox _profiles = new() { Height = 90, SelectionMode = Controls.SelectionMode.Multiple };
    private readonly Controls.TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Controls.Button _run = new() { Content = "Run Benchmark", Padding = new Thickness(12, 5, 12, 5) };
    private string? _lastReport;

    public BenchmarkWindow(string? initialFolder)
    {
        Title = "PhotoReview Benchmark"; Width = 700; Height = 220; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _folder.Text = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        foreach (var p in BenchmarkProfiles.All) _profiles.Items.Add(p);
        _profiles.DisplayMemberPath = "Name";
        _profiles.SelectedItems.Add(BenchmarkProfiles.All.First(p => p.Id == "fast-sequential"));
        _run.Click += async (_, _) => await RunAsync();
        var panel = new Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new Controls.TextBlock { Text = "Folder ảnh thật (read-only):" });
        panel.Children.Add(_folder);
        panel.Children.Add(_profiles);
        panel.Children.Add(_run);
        var open = new Controls.Button { Content = "Open last report", Margin = new Thickness(5,0,0,0) };
        open.Click += (_, _) => { if (_lastReport is not null && File.Exists(_lastReport)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastReport}\"") { UseShellExecute = true }); };
        panel.Children.Add(open);
        var openFolder = new Controls.Button { Content = "Open result folder", Margin = new Thickness(5,0,0,0) };
        openFolder.Click += (_, _) => { var dir = _lastReport is null ? Path.GetTempPath() : Path.GetDirectoryName(_lastReport)!; Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); };
        panel.Children.Add(openFolder);
        panel.Children.Add(_status);
        Content = panel;
    }

    private async Task RunAsync()
    {
        if (!Directory.Exists(_folder.Text)) { _status.Text = "Folder không tồn tại."; return; }
        _run.IsEnabled = false;
        try
        {
            var engine = new BenchmarkEngine();
            var files = Directory.EnumerateFiles(_folder.Text, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).ToArray();
            if (files.Length == 0) { _status.Text = "Không tìm thấy ảnh."; return; }
            foreach (BenchmarkProfile profile in _profiles.SelectedItems.Cast<BenchmarkProfile>().ToArray())
            {
            var progress = new Progress<BenchmarkProgress>(p => _status.Text = $"{p.ProfileId}: {p.Completed}/{p.Total} — {p.Message}");
            var executor = new BenchmarkImageExecutor();
            var random = new Random(profile.Id.GetHashCode());
            var report = await engine.RunAsync(_folder.Text, profile, async (_, workload, iteration, ct) =>
            {
                if (workload == BenchmarkWorkload.FileAction)
                {
                    var temp = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Action-" + Guid.NewGuid().ToString("N") + ".bin");
                    try
                    {
                        await File.WriteAllBytesAsync(temp, await File.ReadAllBytesAsync(files[iteration % files.Length], ct), ct);
                        var (actionImage, _) = await executor.DecodeAsync(temp, profile, ct);
                        var moved = temp + ".moved";
                        File.Move(temp, moved);
                        File.Delete(moved);
                        return (actionImage.PixelWidth > 0, (ReviewMetricsSnapshot?)null);
                    }
                    finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                }
                var path = SelectFile(files, workload, iteration, random);
                var (image, _) = await executor.DecodeAsync(path, profile, ct);
                return (image.PixelWidth > 0 && image.PixelHeight > 0, (ReviewMetricsSnapshot?)null);
            }, progress);
            var reportPath = Path.Combine(Path.GetTempPath(), $"photoreview-benchmark-{report.RunId}.json");
            await File.WriteAllTextAsync(reportPath, report.ToJson());
            _lastReport = reportPath;
            _status.Text = $"{profile.Name}: P50 {report.Phases[0].P50:F0} ms, P95 {report.Phases[0].P95:F0} ms — {reportPath}";
            }
        }
        catch (OperationCanceledException) { _status.Text = "Đã hủy."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _run.IsEnabled = true; }
    }

    private static bool IsImage(string path) => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    private static string SelectFile(string[] files, BenchmarkWorkload workload, int iteration, Random random) => workload switch
    {
        BenchmarkWorkload.FirstFrame => files[0],
        BenchmarkWorkload.Random => files[random.Next(files.Length)],
        BenchmarkWorkload.WarmNext => files[iteration % 2 == 0 ? iteration / 2 % files.Length : files.Length - 1 - iteration / 2 % files.Length],
        BenchmarkWorkload.Preload => files[iteration * 2 % files.Length],
        _ => files[iteration % files.Length],
    };
}
