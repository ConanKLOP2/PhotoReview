using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Shell.Integration.Tests.Input;

/// <summary>
/// WP-22 / G-INPUT + G-KEY trên shell THẬT: golden do WP-10 ghi từ MainWindow WPF thật chạy lại qua thông điệp Win32 vào HWND
/// thật (khác WP-16 chạy thẳng controller). Kích thước client, DPI, dấu thời gian, phím chỉnh do test đặt: không phụ thuộc màn
/// hình runner hay khung vô hình của GetWindowRect.
/// </summary>
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class InputGoldenShellTests
{
    [Fact]
    public async Task GInput_EveryRecordedScript_ReplaysThroughRealMessages_ToTheSameCheckpoints()
    {
        var document = GoldenFile.Read(GoldenFile.InputScripts, GoldenJsonContext.Default.GoldenDocumentGoldenInputScript);
        Assert.True(document.Items.Count >= 60, $"G-INPUT cần >= 60 kịch bản (NO-WPF-EXEC-PLAN 7.3), file có {document.Items.Count}");
        await using var ui = await UiThread.StartAsync();
        var failures = new List<string>();

        await ui.RunAsync(async () =>
        {
            foreach (var script in document.Items)
            {
                List<GoldenCheckpoint> actual;
                try
                {
                    actual = await InputGoldenReplay.RunAsync(ui, script);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add($"{script.Name}: ngoại lệ {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                var byStep = actual.ToDictionary(c => c.AfterStep);
                foreach (var expected in script.Expected)
                {
                    var diffs = byStep.TryGetValue(expected.AfterStep, out var got)
                        ? InputGoldenReplay.Diff(expected, got).ToList()
                        : ["thiếu checkpoint"];
                    if (diffs.Count > 0)
                    {
                        failures.Add($"{script.Name} sau bước {expected.AfterStep}: {string.Join(", ", diffs)}");
                    }
                }
            }
        });

        Assert.True(failures.Count == 0, $"{failures.Count} checkpoint lệch:\n" + string.Join('\n', failures.Take(40)));
    }
}
