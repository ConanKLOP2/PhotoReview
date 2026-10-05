using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Benchmark.Cli;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// R13: a report file whose final path is a hard link or symlink to a user's photo must be replaced, never written through.
/// Links are created only under a private temp folder (Native: real file-system links).
/// </summary>
[Trait("Category", "Native")]
public sealed class BenchmarkCliReportWriteTests : IDisposable
{
    private readonly TempRoot _root = new("cli-report-write");

    public void Dispose() => _root.Dispose(); // deleting a link entry never touches its target

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [Fact(DisplayName = "R13 (Native): a report path that is a hard link to a photo is replaced, the photo's bytes stay intact")]
    public async Task HardLinkedReportPath_DoesNotOverwriteThePhoto()
    {
        var photo = Path.Combine(_root.Dir("photos"), "IMG_0001.CR2");
        File.WriteAllBytes(photo, [1, 2, 3, 4]);
        var report = Path.Combine(_root.Dir("reports"), "summary.md");
        Assert.True(CreateHardLinkW(report, photo, IntPtr.Zero), "could not create a hard link in the temp folder");

        await ToolPathGuard.WriteReportFileAsync(report, "report text");

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(photo));
        Assert.Equal("report text", File.ReadAllText(report));
    }

    [Fact(DisplayName = "R13 (Native): a report path that is a file symlink to a photo is replaced, the photo's bytes stay intact")]
    public async Task SymlinkedReportPath_DoesNotOverwriteThePhoto()
    {
        var photo = Path.Combine(_root.Dir("photos"), "IMG_0002.CR2");
        File.WriteAllBytes(photo, [9, 8, 7]);
        var report = Path.Combine(_root.Dir("reports"), "summary.md");
        try { File.CreateSymbolicLink(report, photo); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No symlink privilege on this account: the hard-link test above covers the same overwrite path.
            Assert.False(File.Exists(report), "skipped: file symlink creation needs privilege (" + ex.Message + ")");
            return;
        }

        await ToolPathGuard.WriteReportFileAsync(report, "report text");

        Assert.Equal([9, 8, 7], File.ReadAllBytes(photo));
        Assert.Equal("report text", File.ReadAllText(report));
        Assert.Null(new FileInfo(report).LinkTarget);
    }

    [Fact(DisplayName = "R30: two default report directories resolved in the same second differ")]
    public void DefaultReportDirectories_AreUnique()
    {
        var photos = _root.Dir("photos2");

        Assert.NotEqual(BenchmarkCliArguments.ResolveReportDirectory(photos, null), BenchmarkCliArguments.ResolveReportDirectory(photos, null));
    }

    private static PerfSession.Options NewOptions(double? bandwidth) =>
        new() { ScenarioPath = "s.json", Folder = "f", OutDir = "o", SlowLinkBandwidthMbps = bandwidth };

    [Fact(DisplayName = "R34: a bandwidth-only slow-link option is applied")]
    public void BandwidthOnlySlowLink_IsRequested()
    {
        Assert.True(PerfSession.IsSlowLinkRequested(NewOptions(bandwidth: 5)));
        Assert.False(PerfSession.IsSlowLinkRequested(NewOptions(bandwidth: null)));
    }

    [Theory(DisplayName = "R35: an action step that never ran its action is reported as not invoked")]
    [InlineData(0, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, false)]
    public void ActionNotInvoked_IsReported(int run, int handled, bool expectedFailure) =>
        Assert.Equal(expectedFailure, PerfSession.ActionNotInvokedReason("F5", run, handled) is not null);

    [Theory(DisplayName = "R20: matching dimensions only mean preview-only while the sensor is unknown or larger")]
    [InlineData(1600, 1067, 1600, 1067, 0, 0, true)]
    [InlineData(1600, 1067, 1600, 1067, 6000, 4000, true)]
    [InlineData(6000, 4000, 6000, 4000, 6000, 4000, false)]
    [InlineData(1600, 1067, 1620, 1080, 6000, 4000, false)]
    public void InferPreviewOnly_RequiresSmallerThanSensor(int dw, int dh, int pw, int ph, int sw, int sh, bool expected) =>
        Assert.Equal(expected, RawSurvey.InferPreviewOnly(dw, dh, pw, ph, sw, sh));
}
