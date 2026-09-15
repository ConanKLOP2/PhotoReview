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
            foreach (BenchmarkProfile profile in _profiles.SelectedItems.Cast<BenchmarkProfile>().ToArray())
            {
            var progress = new Progress<BenchmarkProgress>(p => _status.Text = $"{p.ProfileId}: {p.Completed}/{p.Total} — {p.Message}");
            var report = await engine.RunAsync(_folder.Text, profile, async (_, _, _, ct) =>
            {
                var file = Directory.EnumerateFiles(_folder.Text, "*.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(p => IsImage(p));
                if (file is null) return (false, (ReviewMetricsSnapshot?)null);
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                var buffer = new byte[64 * 1024];
                while (await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct) > 0) { }
                return (true, (ReviewMetricsSnapshot?)null);
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
}
