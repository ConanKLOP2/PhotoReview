using System.IO;
using PhotoReview.App;
using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>
/// D05: DiagOptions parses PHOTOREVIEW_DIAG_* environment variables once (via a Lazy) and defaults
/// every flag to "disabled" when unset. These tests mutate process environment variables and the
/// static Lazy snapshot, so they run in the "GlobalState" collection like PerfTraceTests (D03).
/// </summary>
[Collection("GlobalState")]
public sealed class DiagOptionsTests : IDisposable
{
    private const string PreReadVar = "PHOTOREVIEW_DIAG_PREREAD";
    private const string PreloadWorkersVar = "PHOTOREVIEW_DIAG_PRELOAD_WORKERS";
    private const string DisableDiskCacheVar = "PHOTOREVIEW_DIAG_DISABLE_DISKCACHE";

    private readonly string? _previousPreRead = Environment.GetEnvironmentVariable(PreReadVar);
    private readonly string? _previousWorkers = Environment.GetEnvironmentVariable(PreloadWorkersVar);
    private readonly string? _previousDisableDiskCache = Environment.GetEnvironmentVariable(DisableDiskCacheVar);
    private readonly TempRoot _root = new("diag-options");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PreReadVar, _previousPreRead);
        Environment.SetEnvironmentVariable(PreloadWorkersVar, _previousWorkers);
        Environment.SetEnvironmentVariable(DisableDiskCacheVar, _previousDisableDiskCache);
        DiagOptions.ResetForTests();
        _root.Dispose();
    }

    private static void ClearAll()
    {
        Environment.SetEnvironmentVariable(PreReadVar, null);
        Environment.SetEnvironmentVariable(PreloadWorkersVar, null);
        Environment.SetEnvironmentVariable(DisableDiskCacheVar, null);
        DiagOptions.ResetForTests();
    }

    [Fact(DisplayName = "No PHOTOREVIEW_DIAG_* variables means every flag is disabled")]
    public void NoEnvironmentVariablesMeansDisabled()
    {
        ClearAll();

        Assert.False(DiagOptions.PreRead);
        Assert.Null(DiagOptions.PreloadWorkers);
        Assert.False(DiagOptions.DisableDiskCache);
        Assert.False(DiagOptions.AnyEnabled);
        Assert.Equal("(none)", DiagOptions.Describe());
    }

    [Fact(DisplayName = "PHOTOREVIEW_DIAG_PREREAD=1 enables PreRead; other values do not")]
    public void PreReadParsesOnlyExactly1()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(PreReadVar, "1");
        DiagOptions.ResetForTests();
        Assert.True(DiagOptions.PreRead);
        Assert.True(DiagOptions.AnyEnabled);
        Assert.Contains("PREREAD=1", DiagOptions.Describe());

        foreach (var invalid in new[] { "true", "0", "yes", "" })
        {
            Environment.SetEnvironmentVariable(PreReadVar, invalid);
            DiagOptions.ResetForTests();
            Assert.False(DiagOptions.PreRead);
        }
    }

    [Theory(DisplayName = "PHOTOREVIEW_DIAG_PRELOAD_WORKERS accepts 0..16 and rejects everything else")]
    [InlineData("0", 0)]
    [InlineData("16", 16)]
    [InlineData("4", 4)]
    public void PreloadWorkersParsesInRangeValues(string raw, int expected)
    {
        ClearAll();
        Environment.SetEnvironmentVariable(PreloadWorkersVar, raw);
        DiagOptions.ResetForTests();
        Assert.Equal(expected, DiagOptions.PreloadWorkers);
        Assert.True(DiagOptions.AnyEnabled);
        Assert.Contains($"PRELOAD_WORKERS={expected}", DiagOptions.Describe());
    }

    [Theory(DisplayName = "PHOTOREVIEW_DIAG_PRELOAD_WORKERS rejects out-of-range or unparsable values")]
    [InlineData("-1")]
    [InlineData("17")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2.5")]
    public void PreloadWorkersRejectsInvalidValues(string raw)
    {
        ClearAll();
        Environment.SetEnvironmentVariable(PreloadWorkersVar, raw);
        DiagOptions.ResetForTests();
        Assert.Null(DiagOptions.PreloadWorkers);
        Assert.False(DiagOptions.AnyEnabled);
    }

    [Fact(DisplayName = "PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1 enables DisableDiskCache; other values do not")]
    public void DisableDiskCacheParsesOnlyExactly1()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(DisableDiskCacheVar, "1");
        DiagOptions.ResetForTests();
        Assert.True(DiagOptions.DisableDiskCache);
        Assert.True(DiagOptions.AnyEnabled);
        Assert.Contains("DISABLE_DISKCACHE=1", DiagOptions.Describe());

        Environment.SetEnvironmentVariable(DisableDiskCacheVar, "0");
        DiagOptions.ResetForTests();
        Assert.False(DiagOptions.DisableDiskCache);
    }

    [Fact(DisplayName = "Describe lists every active flag and AnyEnabled reflects all three")]
    public void DescribeAndAnyEnabledCombineAllFlags()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(PreReadVar, "1");
        Environment.SetEnvironmentVariable(PreloadWorkersVar, "2");
        Environment.SetEnvironmentVariable(DisableDiskCacheVar, "1");
        DiagOptions.ResetForTests();

        Assert.True(DiagOptions.AnyEnabled);
        var description = DiagOptions.Describe();
        Assert.Contains("PREREAD=1", description);
        Assert.Contains("PRELOAD_WORKERS=2", description);
        Assert.Contains("DISABLE_DISKCACHE=1", description);
    }

    [Fact(DisplayName = "ResetForTests re-reads the environment instead of caching the first read forever")]
    public void ResetForTestsRereadsEnvironment()
    {
        ClearAll();
        Assert.False(DiagOptions.PreRead);
        Environment.SetEnvironmentVariable(PreReadVar, "1");
        // Without ResetForTests, DiagOptions.PreRead must still report the stale (false) value once
        // read, because production code reads the environment exactly once via Lazy.
        Assert.False(DiagOptions.PreRead);
        DiagOptions.ResetForTests();
        Assert.True(DiagOptions.PreRead);
    }

    [Fact(DisplayName = "PHOTOREVIEW_DIAG_PREREAD=1 decodes the same bitmap size as the default path")]
    public void PreReadDecodesSameSizeAsDefault()
    {
        ClearAll();
        // A valid, tiny 1x1 PNG, same fixture bytes used elsewhere in the suite for portable decode tests.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var path = _root.File("prereadtest.png", png);

        var withoutPreRead = PreviewImageService.DecodeSource(path, 0);
        Assert.Equal(1, withoutPreRead.PixelWidth);
        Assert.Equal(1, withoutPreRead.PixelHeight);

        Environment.SetEnvironmentVariable(PreReadVar, "1");
        DiagOptions.ResetForTests();
        try
        {
            var withPreRead = PreviewImageService.DecodeSource(path, 0);
            Assert.Equal(withoutPreRead.PixelWidth, withPreRead.PixelWidth);
            Assert.Equal(withoutPreRead.PixelHeight, withPreRead.PixelHeight);
        }
        finally
        {
            ClearAll();
        }
    }
}
