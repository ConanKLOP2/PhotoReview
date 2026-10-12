using System.IO;
using PhotoReview.TestSupport.Golden;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-28: writes <c>tests/Fixtures/golden/pixel/*.png</c> from the WPF build (G-PIX). Opt-in (PHOTOREVIEW_GOLDEN_RECORD=1), run through
/// <c>tools/diag/record-golden.ps1</c> (its filter matches this class by name). Never part of the CI filter (Category=Manual).
/// </summary>
[Trait("Category", "Manual")]
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class PixelGoldenRecordTests
{
    [Fact]
    public async Task RecordAll_WritesThePixelGoldenFromTheWpfBuild()
    {
        Assert.True(GoldenFile.IsRecordingEnabled,
            $"Recording rewrites tests/Fixtures/golden/pixel. Set {GoldenFile.RecordEnvironmentVariable}=1 on purpose (tools/diag/record-golden.ps1).");

        var frames = await PixelGoldenRecorder.RenderAllAsync(PixelGoldenRecorder.RecordedCases);
        Assert.Equal(PixelGoldenRecorder.RecordedCases.Count, frames.Count);
        foreach (var c in PixelGoldenRecorder.RecordedCases)
        {
            PngFile.Write(PixelGoldenRecorder.PngPath(c), frames[c.Name]);
        }

        // A stale file of a removed case must not linger: the shell test refuses orphans.
        // (WP-28b: the extension frames are recorded by PixelExtGoldenRecordTests and kept here.)
        var wanted = PixelGoldenRecorder.RecordedCases.Select(c => c.Name)
            .Concat(PixelGoldenExtCatalog.GoldenNames).Select(n => n + ".png").ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(PixelGoldenFiles.Directory, "*.png"))
        {
            if (!wanted.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
    }
}
