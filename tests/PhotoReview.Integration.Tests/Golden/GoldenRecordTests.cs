using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10: writes <c>tests/Fixtures/golden/{viewport-layout,input-scripts,context-menu,overlay-boxes}.v1.json</c> from the WPF build
/// (key-names.v1.json is written by <c>PhotoReview.App.Tests</c> KeyNameGoldenTests). Opt-in: needs PHOTOREVIEW_GOLDEN_RECORD=1;
/// run through <c>tools/diag/record-golden.ps1</c> (hidden desktop). Never part of the CI filter (Category=Manual).
/// </summary>
[Trait("Category", "Manual")]
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class GoldenRecordTests
{
    [Fact]
    public async Task RecordAll_WritesTheGoldenFilesFromTheWpfBuild()
    {
        Assert.True(GoldenFile.IsRecordingEnabled,
            $"Recording rewrites tests/Fixtures/golden. Set {GoldenFile.RecordEnvironmentVariable}=1 on purpose (tools/diag/record-golden.ps1).");

        var viewport = await ViewportGoldenRecorder.RecordViewportAsync();
        GoldenFile.Write(GoldenFile.ViewportLayout, ViewportGoldenRecorder.Document(viewport), GoldenJsonContext.Default.GoldenViewportCase);

        var scripts = await InputScriptRecorder.RecordAsync(InputScriptCatalog.Build());
        GoldenFile.Write(GoldenFile.InputScripts, InputScriptRecorder.Document(scripts), GoldenJsonContext.Default.GoldenInputScript);

        var menu = await MenuGoldenRecorder.RecordAsync();
        GoldenFile.Write(GoldenFile.ContextMenu, MenuGoldenRecorder.Document(menu), GoldenJsonContext.Default.GoldenMenuCase);

        var overlay = await OverlayGoldenRecorder.RecordAsync();
        GoldenFile.Write(GoldenFile.OverlayBoxes, OverlayGoldenRecorder.Document(overlay), GoldenJsonContext.Default.GoldenOverlayBox);

        Assert.True(viewport.Count >= 600);
        Assert.True(scripts.Count >= 60);
        Assert.Equal(12, menu.Count);
    }
}
