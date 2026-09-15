using System.IO;
using System.Windows;
using Controls = System.Windows.Controls;

namespace PhotoReview.App;

public sealed class BenchmarkWindow : Window
{
    private readonly Controls.TextBox _folder = new() { MinWidth = 520 };
    private readonly Controls.TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Controls.Button _run = new() { Content = "Run Benchmark", Padding = new Thickness(12, 5, 12, 5) };

    public BenchmarkWindow(string? initialFolder)
    {
        Title = "PhotoReview Benchmark"; Width = 700; Height = 220; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _folder.Text = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        _run.Click += async (_, _) => await RunAsync();
        var panel = new Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new Controls.TextBlock { Text = "Folder ảnh thật (read-only):" });
        panel.Children.Add(_folder);
        panel.Children.Add(_run);
        panel.Children.Add(_status);
        Content = panel;
    }

    private async Task RunAsync()
    {
        if (!Directory.Exists(_folder.Text)) { _status.Text = "Folder không tồn tại."; return; }
        _run.IsEnabled = false;
        try
        {
            var profile = BenchmarkProfiles.All.First(p => p.Id == "fast-sequential");
            var engine = new BenchmarkEngine();
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
            _status.Text = $"Hoàn tất: P50 {report.Phases[0].P50:F0} ms, P95 {report.Phases[0].P95:F0} ms";
        }
        catch (OperationCanceledException) { _status.Text = "Đã hủy."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _run.IsEnabled = true; }
    }

    private static bool IsImage(string path) => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
}
