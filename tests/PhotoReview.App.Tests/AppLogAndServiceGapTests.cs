using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>
/// Stryker gap pins that need to observe the app log (what is and is not logged), the decoder provider list and the
/// dialog service's early exits. All of them share the process-wide <see cref="AppLog.Instance"/>, hence GlobalState.
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class AppLogAndServiceGapTests
{
    // ---- TaskLogging: only a fault is logged ----

    [Fact]
    public async Task FireAndLog_CancelledTask_IsNotLoggedButAFaultIs()
    {
        using var capture = new CapturedAppLog();
        var cancelled = new TaskCompletionSource();
        var faulted = new TaskCompletionSource();
        cancelled.Task.FireAndLog("ctx-cancelled");
        faulted.Task.FireAndLog("ctx-faulted");

        cancelled.SetCanceled();
        faulted.SetException(new InvalidOperationException("boom"));
        await Task.Run(static () => { }); // lets a (wrongly) scheduled continuation of the cancelled task run before the log is read

        var text = capture.Text();
        Assert.Contains("ctx-faulted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ctx-cancelled", text, StringComparison.Ordinal);
    }

    // ---- InfoOverlayViewModel: an expected I/O failure is not worth a log line, anything else is ----

    [Theory]
    [InlineData(typeof(IOException), false)]
    [InlineData(typeof(UnauthorizedAccessException), false)]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(ArgumentException), true)]
    public async Task SiblingSearchFault_IsLoggedOnlyWhenItIsNotAnExpectedIoFailure(Type exceptionType, bool logged)
    {
        using var capture = new CapturedAppLog();
        var settings = new AppSettings { ShowInfoOverlay = true, ShowFolderInfo = true };
        var overlay = new InfoOverlayViewModel(() => settings,
            (_, _) => throw (Exception)Activator.CreateInstance(exceptionType, "boom")!);

        overlay.SetFolder(@"C:\photos\trip");
        await overlay.PendingSiblings.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(logged, capture.Text().Contains("Sibling folder search failed", StringComparison.Ordinal));
    }

    // ---- DecoderProviders: the reason of a failed probe reaches the forced log and the exception ----

    private static List<(DecoderBackend Backend, Func<IImageDecoder> Factory)> CreateProviders(
        DecoderProviders.TurboJpegProbe turboJpeg, DecoderProviders.LibRawProbe libRaw, Func<bool>? libRawNeeded,
        List<(string Message, Exception Error)> forced, List<string> info) =>
        DecoderProviders.Create(null!, libRawNeeded, libRaw, turboJpeg,
            (message, error) => forced.Add((message, error)), info.Add);

    private static bool Fails(out string? reason, string? text)
    {
        reason = text;
        return false;
    }

    private static bool Works(out string? reason)
    {
        reason = null;
        return true;
    }

    [Fact]
    public void Create_WhenTurboJpegProbeFails_ForcedLogCarriesTheProbeReason()
    {
        var forced = new List<(string Message, Exception Error)>();

        var providers = CreateProviders((out string? r) => Fails(out r, "no dll"), Works, null, forced, []);

        Assert.DoesNotContain(providers, p => p.Backend == DecoderBackend.TurboJpeg);
        var entry = Assert.Single(forced, f => f.Message.Contains("TurboJPEG", StringComparison.Ordinal));
        Assert.Contains("no dll", entry.Message, StringComparison.Ordinal);
        Assert.Equal("no dll", entry.Error.Message);
    }

    [Fact]
    public void Create_WhenTurboJpegProbeFailsWithoutAReason_UsesAGenericExceptionMessage()
    {
        var forced = new List<(string Message, Exception Error)>();

        CreateProviders((out string? r) => Fails(out r, null), Works, null, forced, []);

        var entry = Assert.Single(forced, f => f.Message.Contains("TurboJPEG", StringComparison.Ordinal));
        Assert.Equal("TurboJPEG probe failed.", entry.Error.Message);
    }

    [Fact]
    public void Create_WhenLibRawProbeFailsAndRawIsNeeded_ForcedLogCarriesTheProbeReason()
    {
        var forced = new List<(string Message, Exception Error)>();

        var providers = CreateProviders(Works, (out string? r) => Fails(out r, "no libraw"), () => true, forced, []);

        Assert.DoesNotContain(providers, p => p.Backend == DecoderBackend.LibRaw);
        var entry = Assert.Single(forced, f => f.Message.Contains("LibRaw", StringComparison.Ordinal));
        Assert.Contains("no libraw", entry.Message, StringComparison.Ordinal);
        Assert.Equal("no libraw", entry.Error.Message);
    }

    [Fact]
    public void Create_WhenLibRawProbeFailsWithoutAReason_UsesAGenericExceptionMessage()
    {
        var forced = new List<(string Message, Exception Error)>();

        CreateProviders(Works, (out string? r) => Fails(out r, null), () => true, forced, []);

        var entry = Assert.Single(forced, f => f.Message.Contains("LibRaw", StringComparison.Ordinal));
        Assert.Equal("LibRaw probe failed.", entry.Error.Message);
    }

    // ---- WpfDialogService: windows that need an absent service are not opened ----

    [Fact]
    public void ShowRecovery_WithoutAnOperationJournalService_DoesNothing()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Null(Record.Exception(() => new WpfDialogService(services).ShowRecovery()));
    }

    [Fact]
    public void ShowSettings_WithoutASettingsStoreService_ReportsNoChange()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.False(new WpfDialogService(services).ShowSettings());
    }
}
