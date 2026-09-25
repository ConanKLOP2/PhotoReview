using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Platform.Windows.Explorer;

namespace PhotoReview.Integration.Tests;

/// <summary>Window-selection policy of the Explorer query, driven by fake windows (no COM).</summary>
public sealed class ExplorerWindowSelectorTests
{
    private const string Folder = @"C:\photos";

    private sealed class FakeComException(string message, int hresult) : COMException(message, hresult);

    private sealed class FakeWindow(string? location, Func<ExplorerViewSnapshot>? read = null, Exception? locationError = null)
    {
        public string? Location { get; } = location;
        public Func<ExplorerViewSnapshot>? Read { get; } = read;
        public Exception? LocationError { get; } = locationError;
        public int Released { get; set; }
        public int Reads { get; set; }
    }

    private sealed class ListLog : ILog
    {
        public bool Enabled => true;
        public List<string> Errors { get; } = [];
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Add(message);
    }

    private static ExplorerViewSnapshot Available(params string[] names) =>
        new(Folder, names.Select(n => Folder + @"\" + n).ToArray(), [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);

    private static ExplorerViewSnapshot Unavailable(string reason) =>
        new(Folder, [], [], ExplorerGroupState.Unknown, ExplorerOrderStatus.NativeViewUnavailable, reason, DateTime.UtcNow);

    private static FakeWindow Showing(string location, Func<ExplorerViewSnapshot> read) => new(location, read);

    private static ExplorerViewSnapshot Select(IEnumerable<FakeWindow> windows, ILog? log = null, string folder = Folder, CancellationToken token = default) =>
        ExplorerWindowSelector.Select(folder, windows,
            w => w.LocationError is not null ? throw w.LocationError : w.Location,
            w => { w.Reads++; return (w.Read ?? throw new InvalidOperationException("not expected to be read"))(); },
            w => w.Released++, log ?? NullLog.Instance, null, token);

    [Fact]
    public void NoWindowsReportsNoMatchingWindowWithCount()
    {
        var snapshot = Select([]);

        Assert.Equal(ExplorerOrderStatus.NoMatchingWindow, snapshot.Status);
        Assert.Equal("no-matching-window|0", snapshot.Reason);
    }

    [Fact]
    public void WindowsOnOtherFoldersOrNonFileLocationsAreSkippedAndAllReleased()
    {
        FakeWindow[] windows =
        [
            new(@"file:///C:/other/"), new(null), new(""), new("https://example.com/"),
            new("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"), new("file:///C:/photos2/"),
        ];

        var snapshot = Select(windows);

        Assert.Equal(ExplorerOrderStatus.NoMatchingWindow, snapshot.Status);
        Assert.Equal("no-matching-window|6", snapshot.Reason);
        Assert.All(windows, w => { Assert.Equal(1, w.Released); Assert.Equal(0, w.Reads); });
    }

    [Theory]
    [InlineData("file:///C:/photos/")]
    [InlineData("file:///C:/photos")]
    [InlineData("file:///c:/PHOTOS/")]
    [InlineData("FILE:///C:/photos/./")]
    public void LocationSpellingsOfTheSameFolderMatch(string location)
    {
        var window = Showing(location, () => Available("a.jpg"));

        var snapshot = Select([window]);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(1, window.Released);
    }

    [Fact]
    public void UnicodeAndPercentEncodedFolderNamesMatch()
    {
        var folder = @"D:\Ảnh đẹp #1\tháng 9";
        var window = Showing("file:///D:/%E1%BA%A2nh%20%C4%91%E1%BA%B9p%20%231/th%C3%A1ng%209/", () => Available("a.jpg"));

        Assert.Equal(ExplorerOrderStatus.Available, Select([window], folder: folder).Status);
    }

    [Fact]
    public void UncLocationMatchesUncFolder()
    {
        var window = Showing("file://server/share/photos/", () => Available("a.jpg"));

        Assert.Equal(ExplorerOrderStatus.Available, Select([window], folder: @"\\server\share\photos").Status);
    }

    [Fact(DisplayName = "A window that closes while its location is read is skipped and the next matching window is used")]
    public void WindowClosingDuringLocationReadIsSkipped()
    {
        var closing = new FakeWindow(null, locationError: new FakeComException("RPC server unavailable", unchecked((int)0x800706BA)));
        var good = Showing("file:///C:/photos/", () => Available("a.jpg"));

        var snapshot = Select([closing, good]);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(1, closing.Released);
        Assert.Equal(1, good.Released);
    }

    [Fact]
    public void UnexpectedLocationErrorIsLoggedAndSkipped()
    {
        var log = new ListLog();
        var broken = new FakeWindow(null, locationError: new InvalidOperationException("weird"));
        var good = Showing("file:///C:/photos/", () => Available("a.jpg"));

        var snapshot = Select([broken, good], log: log);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Single(log.Errors);
    }

    [Fact(DisplayName = "Several windows on the same folder: a failing one does not hide a working one")]
    public void MultipleMatchingWindowsFallThroughFailures()
    {
        var loading = Showing("file:///C:/photos/", () => Unavailable("empty-view|0x00000000|0"));
        var throwing = Showing("file:///C:/photos/", () => throw new FakeComException("gone", unchecked((int)0x80010108)));
        var good = Showing("file:///C:/photos/", () => Available("b.jpg", "a.jpg"));
        var never = Showing("file:///C:/photos/", () => Available("z.jpg"));

        var snapshot = Select(Lazy(loading, throwing, good, never));

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(2, snapshot.OrderedPaths.Count);
        Assert.Equal(0, never.Reads);
        Assert.Equal(0, never.Released); // never enumerated: a lazy walk stops at the first success
        Assert.All(new[] { loading, throwing, good }, w => Assert.Equal(1, w.Released));
    }

    [Fact(DisplayName = "When every matching window fails the FIRST failure is reported (not NoMatchingWindow)")]
    public void AllMatchingWindowsFailingReportsFirstFailure()
    {
        var first = Showing("file:///C:/photos/", () => Unavailable("empty-view|0x00000000|0"));
        var second = Showing("file:///C:/photos/", () => throw new FakeComException("gone", unchecked((int)0x80010108)));

        var snapshot = Select([first, second]);

        Assert.Equal(ExplorerOrderStatus.NativeViewUnavailable, snapshot.Status);
        Assert.Equal("empty-view|0x00000000|0", snapshot.Reason);
    }

    [Fact]
    public void ThrownReadIsReportedWithTypeAndHresult()
    {
        var window = Showing("file:///C:/photos/", () => throw new FakeComException("gone", unchecked((int)0x80010108)));

        var snapshot = Select([window]);

        Assert.Equal(ExplorerOrderStatus.Failed, snapshot.Status);
        Assert.Equal("native-view-failed|FakeComException|0x80010108", snapshot.Reason);
        Assert.Equal(1, window.Released);
    }

    [Fact]
    public void CancellationDuringReadStopsTheWalkAndReleases()
    {
        var first = Showing("file:///C:/photos/", () => throw new OperationCanceledException());
        var second = Showing("file:///C:/photos/", () => Available("a.jpg"));

        var snapshot = Select([first, second]);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.Equal(1, first.Released);
        Assert.Equal(0, second.Reads);
    }

    [Fact]
    public void CancelledTokenStopsBeforeLookingAtAnyLocation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var window = Showing("file:///C:/photos/", () => Available("a.jpg"));

        var snapshot = Select([window], token: cts.Token);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.Equal(0, window.Reads);
        Assert.Equal(1, window.Released);
    }

    [Fact]
    public void CancellationRequestedBetweenWindowsStopsTheWalk()
    {
        using var cts = new CancellationTokenSource();
        var first = new FakeWindow("file:///C:/other/");
        var second = Showing("file:///C:/photos/", () => Available("a.jpg"));

        var snapshot = ExplorerWindowSelector.Select(Folder, Lazy(first, second),
            w => { if (ReferenceEquals(w, first)) cts.Cancel(); return w.Location; },
            w => { w.Reads++; return w.Read!(); }, w => w.Released++, NullLog.Instance, null, cts.Token);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.Equal(0, second.Reads);
        Assert.Equal(1, first.Released);
        Assert.Equal(1, second.Released);
    }

    [Theory]
    [InlineData("file:///C:/photos/", @"C:\photos")]
    [InlineData("file:///C:/", @"C:\")]
    [InlineData("file://srv/share/x/", @"\\srv\share\x")]
    [InlineData("file:///C:/a%20b/", @"C:\a b")]
    public void TryCanonicalizeLocationDecodesFileUrls(string location, string expected)
    {
        Assert.True(ExplorerWindowSelector.TryCanonicalizeLocation(location, out var folder));
        Assert.Equal(expected, folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://x/y")]
    [InlineData("ftp://x/y")]
    [InlineData("not a url")]
    public void TryCanonicalizeLocationRejectsNonFileLocations(string? location)
    {
        Assert.False(ExplorerWindowSelector.TryCanonicalizeLocation(location, out var folder));
        Assert.Equal(string.Empty, folder);
    }

    /// <summary>Enumerates lazily like the COM enumerator: windows after an early return are never produced.</summary>
    private static IEnumerable<FakeWindow> Lazy(params FakeWindow[] windows)
    {
        foreach (var window in windows) yield return window;
    }
}
