using System.IO;
using PhotoReview.Core.IO;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// T-08 / APP-P01: the one place a navigation can be superseded between "its stat resumed and it was still current" and "its
/// viewer decode is requested" is a synchronous re-entry -- code that runs in between (here the status text sink) starts a newer
/// <see cref="ImagePresenter.PresentAsync"/>, which cancels and DISPOSES the older navigation's decode source. The older
/// navigation must then use the token it captured up front instead of asking the disposed source for it again.
/// </summary>
[Collection("GlobalState")] // merged onto the whole class: the test below swaps the process-wide AppLog (see CapturedAppLog)
public sealed partial class ImagePresenterTests
{
    [Fact(DisplayName = "APP-P01: a navigation superseded by a re-entrant present before its viewer decode starts still requests the decode with its captured token")]
    public async Task PresentAsync_SupersededByReentrantPresentBeforeViewerDecodeStarts_ReadsTheCapturedTokenNotTheDisposedSource()
    {
        using var log = new CapturedAppLog();
        var first = CreateFakeImageFile("first.png");
        var second = CreateFakeImageFile("second.png");
        _catalog.Reset([first, second]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        var presenter = CreatePresenterWithDelayedStat(fs);
        Task? reentrant = null;
        var entered = 0;
        _sink.OnSetStatusText = _ =>
        {
            // First status of the first navigation ("loading"): runs synchronously inside PresentCoreAsync, after its stat resumed and
            // was found current, before it asks the preview service for the viewer decode.
            // The guard is an atomic flag set BEFORE re-entering, not "reentrant is not null": the second navigation's own "loading"
            // status can fire on the stat worker before the assignment below completes, which re-entered again, superseded the
            // second navigation and left the awaited task completed with nothing presented (the CI flake).
            if (Interlocked.Exchange(ref entered, 1) != 0) return;
            reentrant = presenter.PresentAsync(1);
        };
        try
        {
            var stale = presenter.PresentAsync(0);
            await stale.WaitAsync(GateTimeout);
            Assert.NotNull(reentrant);
            await reentrant.WaitAsync(GateTimeout);
        }
        finally { fs.ReleaseAll(); }

        Assert.Equal([second], _sink.PresentedPaths);
        // Reading Token from the disposed source throws ObjectDisposedException, which the presenter's last catch only LOGS (the
        // navigation is stale, so nothing else shows it): the log is the observable, so it must hold no failure for this navigation.
        var logged = log.Text();
        Assert.DoesNotContain("ObjectDisposedException", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowImage failed", logged, StringComparison.Ordinal);
    }
}
