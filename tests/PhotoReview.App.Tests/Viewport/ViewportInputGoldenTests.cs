using System.IO;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.App.Tests.Viewport;

/// <summary>
/// WP-16 / G-INPUT: kịch bản <c>input-scripts.v1.json</c> (WP-10, ghi trên MainWindow WPF thật) chạy lại qua controller thật trên
/// engine thuần phải khớp 100 % checkpoint (zoom 0,001, độ dài 0,5 DIP). Tự Skip khi golden chưa có. Ở App.Tests (không phải
/// Shell.Tests như thẻ) vì PointerInputController/FitViewController còn ở App tới WP-09. Hai test còn lại kiểm runner.
/// </summary>
public sealed class ViewportInputGoldenTests
{
    [InputGoldenFact(InputGoldenFixture.InputScriptsFile)]
    public void EveryRecordedScript_ReplaysOnTheEngine_ToTheSameCheckpoints()
    {
        var scripts = InputGoldenFixture.ReadCases<GoldenInputScript>(File.ReadAllText(InputGoldenFixture.Find(InputGoldenFixture.InputScriptsFile)!));
        Assert.True(scripts.Count >= 60, $"G-INPUT cần >= 60 kịch bản (NO-WPF-EXEC-PLAN 7.3), file có {scripts.Count}");
        var failures = new List<string>();
        foreach (var script in scripts)
        {
            var actual = InputScriptRunner.Run(script).ToDictionary(c => c.AfterStep);
            foreach (var expected in script.Expected)
            {
                var diffs = actual.TryGetValue(expected.AfterStep, out var got)
                    ? InputScriptRunner.Diff(expected, got).ToList()
                    : ["thiếu checkpoint"];
                if (diffs.Count > 0) failures.Add($"{script.Name} sau bước {expected.AfterStep}: {string.Join(", ", diffs)}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} checkpoint lệch:\n" + string.Join('\n', failures.Take(30)));
    }

    [Fact]
    public void Runner_WheelFromFit_MatchesTheHandComputedCheckpoint()
    {
        // 6000x4000 trong 1280x720: Fit 1080x720 ở x = 100, FitZoom 0,18; bước mặc định 10 % -> 0,28 -> 1680x1120, hai thanh.
        // Điểm dưới (400, 300): (300/1080, 300/720) của ảnh -> offset (0,2778 x 1680 - 400, 0,4167 x 1120 - 300).
        var script = new GoldenInputScript("wheel-from-fit", new GoldenSetup(1280, 720, 1.0, 6000, 4000, """{"MouseWheelAction":"Zoom"}"""),
            [new GoldenInputStep("wheel", 400, 300, 120, null, null, 0, null)],
            [new GoldenCheckpoint(-1, 1.0, true, 0, 0, 1080, 720, 1280, 720, 18),
             new GoldenCheckpoint(0, 0.28, false, (1680 * 300 / 1080.0) - 400, (1120 * 300 / 720.0) - 300, 1680, 1120, 1270, 710, 28)]);

        var actual = InputScriptRunner.Run(script);

        Assert.Equal(2, actual.Count);
        for (var i = 0; i < actual.Count; i++) Assert.Empty(InputScriptRunner.Diff(script.Expected[i], actual[i]));
        Assert.Single(InputScriptRunner.Diff(script.Expected[1], actual[1] with { HorizontalOffset = actual[1].HorizontalOffset + 0.51 }));
        Assert.Single(InputScriptRunner.Diff(script.Expected[1], actual[1] with { Zoom = 0.2812 }));
        Assert.Single(InputScriptRunner.Diff(script.Expected[1], actual[1] with { IsFit = true }));
        Assert.Single(InputScriptRunner.Diff(script.Expected[1], actual[1] with { DisplayZoomPercent = 29 }));
    }

    [Fact]
    public void Runner_CoversEveryStepKind_AndRejectsUnknownOnes()
    {
        var steps = new List<GoldenInputStep>
        {
            new("command", 0, 0, 0, null, null, 0, "ActualSize"),
            new("press", 600, 400, 0, null, null, 0, null),
            new("move", 500, 350, 0, null, null, 16, null),
            new("release", 500, 350, 0, null, null, 32, null),
            new("frame", 0, 0, 0, null, null, 48, null),
            new("key", 0, 0, 0, "Right", null, 64, null),
            new("hwheel", 600, 400, 120, null, null, 80, null),
            new("wheel", 600, 400, -120, null, "Control", 96, null),
            new("resize", 1000, 700, 0, null, null, 112, null),
            new("command", 0, 0, 0, null, null, 128, "FitWidth"),
            new("command", 0, 0, 0, null, null, 144, "FitWidth2"),
            new("command", 0, 0, 0, null, null, 160, "FitHeight"),
            new("command", 0, 0, 0, null, null, 176, "ZoomIn"),
            new("command", 0, 0, 0, null, null, 192, "ZoomOut"),
            new("command", 0, 0, 200, null, null, 208, "ClickZoomLevel"),
            new("command", 0, 0, 0, null, null, 224, "ClickZoom"),
            new("command", 0, 0, 0, null, null, 240, "Fit"),
        };
        var expected = steps.Select((_, i) => new GoldenCheckpoint(i, 0, false, 0, 0, 0, 0, 0, 0, 0)).ToList();
        var script = new GoldenInputScript("all-kinds", new GoldenSetup(1280, 720, 1.25, 4000, 6000, ""), steps, expected);

        var actual = InputScriptRunner.Run(script);

        Assert.Equal(steps.Count, actual.Count);
        Assert.Equal(1.0, actual[0].Zoom, 9);
        Assert.True(actual[^1].IsFit);
        foreach (var c in actual)
        {
            Assert.InRange(c.HorizontalOffset, 0, Math.Max(0, c.ExtentWidth - c.ViewportWidth));
            Assert.InRange(c.VerticalOffset, 0, Math.Max(0, c.ExtentHeight - c.ViewportHeight));
        }
        Assert.Equal(1000, actual[8].ViewportWidth + (actual[8].ExtentHeight > actual[8].ViewportHeight + 0.5 ? 10 : 0), 9);

        foreach (var bad in new[]
        {
            new GoldenInputStep("tap", 0, 0, 0, null, null, 0, null),
            new GoldenInputStep("key", 0, 0, 0, "NoSuchKey", null, 0, null),
            new GoldenInputStep("command", 0, 0, 0, null, null, 0, "Rotate"),
        })
        {
            var badScript = script with { Steps = [bad], Expected = [] };
            Assert.Throws<NotSupportedException>(() => InputScriptRunner.Run(badScript));
        }
    }
}
