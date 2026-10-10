using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Session;
using System.Windows;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>Small pure helpers behind the windows: the forwarding folder sink, viewport validation, diagnostics texts.</summary>
public sealed class MainWindowSupportTests
{
    private sealed class RecordingSink : IFolderLoadSink
    {
        public List<string> Calls { get; } = [];
        public int ResetCount { get; private set; }

        public void ResetCaches() { ResetCount++; Calls.Add("Reset"); }
        public void OnCatalogReady(string folder, int count, SessionState session) => Calls.Add($"Ready {folder} {count} {session.Folder}");
        public Task PresentAsync(int index, long presentationGeneration) { Calls.Add($"Present {index} {presentationGeneration}"); return Task.CompletedTask; }
        public void OnEmpty(string folder, SessionState session) => Calls.Add($"Empty {folder} {session.Folder}");
        public void OnEmptyWithSubfolders(string folder, SessionState session, int subfolderCount) => Calls.Add($"EmptySub {folder} {session.Folder} {subfolderCount}");
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => Calls.Add($"Order {count} {currentIndex} {currentKept}");
        public void OnFilesSkipped(string folder, IReadOnlyList<SkippedEntry> skipped) => Calls.Add($"Skipped {folder} {skipped.Count}");
        public void OnFailed(string folder, Exception exception) => Calls.Add($"Failed {folder} {exception.Message}");
        public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved)
        {
            Calls.Add($"Unreadable {string.Join('|', removedPaths)} {currentRemoved}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ForwardingFolderSink_ForwardsEveryCallWithItsArguments_ToTheCurrentTarget()
    {
        var target = new RecordingSink();
        var sink = new ForwardingFolderSink(() => target);
        var session = new SessionState { Folder = "S" };

        sink.ResetCaches();
        sink.OnCatalogReady(@"C:\a", 3, session);
        await sink.PresentAsync(2, 77);
        sink.OnEmpty(@"C:\b", session);
        sink.OnEmptyWithSubfolders(@"C:\c", session, 4);
        sink.OnOrderApplied(5, 1, true);
        sink.OnFilesSkipped(@"C:\d", [new SkippedEntry("x", "r"), new SkippedEntry("y", "r")]);
        sink.OnFailed(@"C:\e", new InvalidOperationException("boom"));
        await sink.OnUnreadableRemovedAsync(["p1", "p2"], true);

        Assert.Equal(
        [
            "Reset",
            @"Ready C:\a 3 S",
            "Present 2 77",
            @"Empty C:\b S",
            @"EmptySub C:\c S 4",
            "Order 5 1 True",
            @"Skipped C:\d 2",
            @"Failed C:\e boom",
            "Unreadable p1|p2 True",
        ], target.Calls);
    }

    [Fact]
    public void ForwardingFolderSink_ResolvesTheTargetAtEveryCall()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        var current = first;
        var sink = new ForwardingFolderSink(() => current);

        sink.ResetCaches();
        current = second;
        sink.ResetCaches();
        sink.ResetCaches();

        Assert.Equal(1, first.ResetCount);
        Assert.Equal(2, second.ResetCount);
    }

    private static ViewportSnapshot Snapshot(
        double vw = 800, double vh = 600, double hOff = 0, double vOff = 0,
        double maxW = 800, double maxH = 600, double actW = 800, double actH = 600)
        => new(1.0, ViewerStretchMode.Uniform, maxW, maxH, actW, actH, 800, 600, vw, vh, hOff, vOff,
            false, false);

    [Theory]
    [InlineData(double.NaN, 600, 0, 0, 800, 600, 800, 600, "Viewport not ready")]
    [InlineData(800, 0, 0, 0, 800, 600, 800, 600, "Viewport not ready")]
    [InlineData(800, 600, -1, 0, 800, 600, 800, 600, "Offsets invalid")]
    [InlineData(800, 600, 0, double.PositiveInfinity, 800, 600, 800, 600, "Offsets invalid")]
    [InlineData(800, 600, 0, 0, -1, 600, 800, 600, "MaxImage not set")]
    [InlineData(800, 600, 0, 0, 800, double.NaN, 800, 600, "MaxImage not set")]
    [InlineData(800, 600, 0, 0, 800, 600, -5, 600, "ActualImage invalid")]
    [InlineData(800, 600, 0, 0, 800, 600, 800, double.PositiveInfinity, "ActualImage invalid")]
    public void ValidateMeasurements_ReportsTheFirstInvalidGroup(double vw, double vh, double hOff, double vOff, double maxW, double maxH, double actW, double actH, string reason)
    {
        var result = ViewportConvergence.ValidateMeasurements(Snapshot(vw, vh, hOff, vOff, maxW, maxH, actW, actH));

        Assert.False(result.IsValid);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void ValidateMeasurements_ZeroImageAndOffsetAreValid()
    {
        var result = ViewportConvergence.ValidateMeasurements(Snapshot(maxW: 0, maxH: 0, actW: 0, actH: 0));

        Assert.True(result.IsValid);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void IsStableViewport_IdenticalSnapshotsButAnInvalidAfter_IsNotStable()
    {
        var bad = Snapshot(actW: -1);

        Assert.False(ViewportConvergence.IsStableViewport(bad, bad));
        Assert.True(ViewportConvergence.IsStableViewport(Snapshot(), Snapshot()));
    }

    [Fact]
    public void ViewportSnapshot_ToString_ListsEveryMeasurementWithRoundedFormats()
    {
        var snapshot = new ViewportSnapshot(1.234, ViewerStretchMode.Uniform, 100.4, 200.6, 300.2, 400.8, 500, 600, 700, 800, 1.26, 2.34,
            true, false);

        // The numbers use the current culture (diagnostic text for a human), so the expectation is formatted the same way.
        Assert.Equal(
            $"Zoom={1.23:F2} Stretch=Uniform MaxImage=({100:F0},{201:F0}) Actual=({300:F0},{401:F0}) Extent=({500:F0},{600:F0}) Viewport=({700:F0},{800:F0}) Offset=({1.3:F1},{2.3:F1}) Scrollbars=(Visible,Collapsed)",
            snapshot.ToString());
    }

    [Fact]
    public void ExplorerStatusText_MapsEveryStatusToItsOwnTranslation_AndUnknownToItsName()
    {
        var expected = new Dictionary<ExplorerOrderStatus, string>
        {
            [ExplorerOrderStatus.Available] = Tr.EnumExplorerOrderStatusAvailable,
            [ExplorerOrderStatus.NoMatchingWindow] = Tr.EnumExplorerOrderStatusNoMatchingWindow,
            [ExplorerOrderStatus.NativeViewUnavailable] = Tr.EnumExplorerOrderStatusNativeViewUnavailable,
            [ExplorerOrderStatus.InvalidSnapshot] = Tr.EnumExplorerOrderStatusInvalidSnapshot,
            [ExplorerOrderStatus.TimedOut] = Tr.EnumExplorerOrderStatusTimedOut,
            [ExplorerOrderStatus.Canceled] = Tr.EnumExplorerOrderStatusCanceled,
            [ExplorerOrderStatus.Failed] = Tr.EnumExplorerOrderStatusFailed,
        };

        foreach (var (status, text) in expected)
            Assert.Equal(text, DiagnosticsWindow.StatusText(status));
        Assert.Equal(expected.Count, expected.Values.Distinct().Count()); // seven different texts: a swapped arm cannot hide
        Assert.Equal("42", DiagnosticsWindow.StatusText((ExplorerOrderStatus)42));
    }
}
