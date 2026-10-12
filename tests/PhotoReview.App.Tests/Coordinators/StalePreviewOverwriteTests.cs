using System.IO;
using PhotoReview.Imaging.Caching;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// "An OLD preview overwrites the NEW one" (INV-1) as a deterministic property, not a timing experiment. Every decode, thumbnail
/// and zoom original is a gate this test opens in an order it chooses (see <see cref="StalePreviewHarness"/>); the presenter runs
/// on a one-thread fake UI loop against the real <see cref="PreviewImageService"/>/<see cref="ThumbnailCache"/>. Two kinds of
/// check: (1) the whole sink log is "stale-free" -- every image, status, view-mode and presented call belongs to the navigation
/// that is current when it happens, and the navigation never goes backwards; (2) the settled state (image, size, photo info,
/// status, catalog index, compare) is the last navigation's. No Thread.Sleep/Task.Delay: a step returns when the presenter has
/// really reacted. The orders are enumerated exhaustively for small bursts, and sampled with a fixed seed for the big one.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class StalePreviewOverwriteTests
{
    private static readonly string[] ThreeFiles = ["p0.jpg", "p1.jpg", "p2.jpg"];

    private enum OpKind
    {
        Navigate,
        Preview,
        Thumbnail,
    }

    /// <param name="Value">The navigation ordinal for <see cref="OpKind.Navigate"/>, the file index otherwise.</param>
    private readonly record struct Op(OpKind Kind, int Value)
    {
        public override string ToString() => (Kind switch { OpKind.Navigate => "N", OpKind.Preview => "P", _ => "T" }) + Value;
    }

    /// <summary>
    /// Every order in which the navigations of <paramref name="targets"/> are issued (in sequence) and the preview (and, with
    /// <paramref name="thumbnails"/>, thumbnail) of each file finishes, a file finishing only after its first navigation.
    /// </summary>
    private static List<Op[]> Orders(int[] targets, bool thumbnails)
    {
        var files = targets.Distinct().ToArray();
        var total = targets.Length + (files.Length * (thumbnails ? 2 : 1));
        var result = new List<Op[]>();
        var current = new List<Op>();

        void Walk(int issued, HashSet<int> navigated, HashSet<(OpKind, int)> released)
        {
            if (current.Count == total)
            {
                result.Add([.. current]);
                return;
            }

            if (issued < targets.Length)
            {
                current.Add(new Op(OpKind.Navigate, issued));
                var added = navigated.Add(targets[issued]);
                Walk(issued + 1, navigated, released);
                if (added) navigated.Remove(targets[issued]);
                current.RemoveAt(current.Count - 1);
            }

            foreach (var file in navigated.OrderBy(f => f).ToArray())
            {
                foreach (var kind in thumbnails ? new[] { OpKind.Thumbnail, OpKind.Preview } : [OpKind.Preview])
                {
                    if (!released.Add((kind, file))) continue;
                    current.Add(new Op(kind, file));
                    Walk(issued, navigated, released);
                    current.RemoveAt(current.Count - 1);
                    released.Remove((kind, file));
                }
            }
        }

        Walk(0, [], []);
        return result;
    }

    private static string Describe(IEnumerable<Op> ops, bool forceStarted) => string.Join(' ', ops) + (forceStarted ? " (decodes forced to start)" : "");

    private static async Task RunOrderAsync(string dir, int[] targets, IReadOnlyList<Op> ops, bool thumbnails, bool forceStarted)
    {
        using var harness = new StalePreviewHarness(ThreeFiles, thumbnails, sharedDir: dir);
        var scenario = Describe(ops, forceStarted);
        try
        {
            foreach (var op in ops)
            {
                switch (op.Kind)
                {
                    case OpKind.Navigate:
                        var file = targets[op.Value];
                        await harness.NavigateAsync(file);
                        // At most the two viewer slots can be occupied by decodes that were superseded while running.
                        if (forceStarted && op.Value < 2) await harness.PreviewStartedAsync(file);
                        break;
                    case OpKind.Preview:
                        await harness.ReleasePreviewAsync(op.Value);
                        break;
                    default:
                        await harness.ReleaseThumbnailAsync(op.Value);
                        break;
                }
            }

            await harness.SettleAsync();
            harness.AssertNoStaleOverwrite(scenario);
            await harness.AssertSettledOnAsync(targets[^1]);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new InvalidOperationException($"scenario [{scenario}] did not run to the end: {ex.Message}", ex);
        }
    }

    private static async Task RunAllAsync(int[] targets, bool thumbnails, IEnumerable<Op[]> orders)
    {
        var dir = StalePreviewHarness.NewFolder();
        var ran = 0;
        try
        {
            foreach (var ops in orders)
            {
                // With thumbnails only the interesting variant runs: every decode really inside the decoder when it is superseded.
                if (!thumbnails) await RunOrderAsync(dir, targets, ops, thumbnails, forceStarted: false);
                await RunOrderAsync(dir, targets, ops, thumbnails, forceStarted: true);
                ran++;
            }
        }
        finally
        {
            StalePreviewHarness.DeleteFolder(dir);
        }

        Assert.True(ran > 0);
    }

    // ---- ImagePresenter: completion order of a burst --------------------------------------------

    [Fact(DisplayName = "STALE: three files, previews finishing in EVERY order (each decode started or not): only the last navigation is ever shown")]
    public async Task Burst_ThreeFiles_EveryPreviewCompletionOrder_NeverShowsAnOlderImage()
    {
        var orders = Orders([0, 1, 2], thumbnails: false);

        Assert.Equal(15, orders.Count); // N0<N1<N2 and Pf after Nf: 1 x 3 x 5 orders; guards the enumerator itself
        await RunAllAsync([0, 1, 2], thumbnails: false, orders);
    }

    [Fact(DisplayName = "STALE: back-and-forth burst (file 0, 1, 0 again): the revisit joins or restarts the decode and the old navigation never wins")]
    public async Task Burst_RevisitingTheFirstFile_EveryPreviewCompletionOrder_NeverShowsAnOlderImage()
    {
        var orders = Orders([0, 1, 0], thumbnails: false);

        Assert.Equal(8, orders.Count);
        await RunAllAsync([0, 1, 0], thumbnails: false, orders);
    }

    [Fact(DisplayName = "STALE: two files, thumbnail and preview of each finishing in EVERY order: a late thumbnail never replaces the new image")]
    public async Task Burst_TwoFiles_EveryThumbnailAndPreviewOrder_NeverShowsAnOlderImage()
    {
        var orders = Orders([0, 1], thumbnails: true);

        Assert.Equal(40, orders.Count);
        await RunAllAsync([0, 1], thumbnails: true, orders);
    }

    [Fact(DisplayName = "STALE: three files with thumbnails, a fixed-seed sample of completion orders")]
    public async Task Burst_ThreeFilesWithThumbnails_SeededSampleOfOrders_NeverShowsAnOlderImage()
    {
        var all = Orders([0, 1, 2], thumbnails: true);
        var rng = new Random(20261012);
        var sample = all.OrderBy(_ => rng.Next()).Take(12).ToList();

        Assert.True(all.Count > 1000, "the order space is large; it is sampled, not enumerated");
        await RunAllAsync([0, 1, 2], thumbnails: true, sample);
    }

    // ---- ImagePresenter: specific adversarial orders --------------------------------------------

    [Fact(DisplayName = "STALE: the OLD thumbnail arrives after the NEW preview was presented: it is dropped, not flashed over the new image")]
    public async Task LateThumbnailOfAnOlderNavigation_AfterTheNewPreview_IsNeverShown()
    {
        using var harness = new StalePreviewHarness(ThreeFiles, thumbnails: true);
        await harness.NavigateAsync(0);
        await harness.ThumbnailRequestedAsync(0); // the thumbnail of file 0 is really pending
        await harness.NavigateAsync(1);
        await harness.ReleasePreviewAsync(1);
        await harness.AssertSettledOnAsync(1);
        var shownBefore = harness.Events.Count(e => e.Kind == UiEventKind.Image);

        await harness.ReleaseThumbnailAsync(0);   // late
        await harness.ReleasePreviewAsync(0);     // later still
        await harness.SettleAsync();

        harness.AssertNoStaleOverwrite("late thumbnail");
        Assert.Equal(shownBefore, harness.Events.Count(e => e.Kind == UiEventKind.Image)); // nothing at all was shown after
        Assert.DoesNotContain(harness.Events, e => e.Image is { Path: var p } && p == harness.Paths[0]);
        await harness.AssertSettledOnAsync(1);
    }

    [Fact(DisplayName = "STALE: a preview decode that was already running when the user moved on is not shown, and is cached under its own key")]
    public async Task SupersededRunningDecode_FinishingLate_IsCachedForALaterVisitAndNeverShown()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        await harness.NavigateAsync(0);
        await harness.PreviewStartedAsync(0);
        await harness.NavigateAsync(1);
        await harness.PreviewStartedAsync(1);
        await harness.ReleasePreviewAsync(1);
        await harness.AssertSettledOnAsync(1);

        await harness.ReleasePreviewAsync(0);     // the superseded decode finishes after the new image is on screen

        await harness.AssertSettledOnAsync(1);
        harness.AssertNoStaleOverwrite("late running decode");
        // Not lost, not poisoned: the decode of file 0 is in the RAM cache under file 0's key and is file 0's bitmap...
        Assert.True(harness.Service.TryGetCachedPreview(harness.Paths[0], out var cached0));
        Assert.Equal(harness.Paths[0], ((ShownImage)cached0.PlatformImage).Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(harness.Service.TryGetCachedPreview(harness.Paths[1], out var cached1));
        Assert.Equal(harness.Paths[1], ((ShownImage)cached1.PlatformImage).Path, StringComparer.OrdinalIgnoreCase);
        // ... and returning to file 0 shows exactly that bitmap, straight from RAM (no second decode).
        await harness.NavigateAsync(0);
        await harness.SettleAsync();
        await harness.AssertSettledOnAsync(0);
        Assert.Equal(1, harness.PreviewDecodeCalls(0));
        Assert.Same(cached0.PlatformImage, harness.Presenter.CurrentImage);
    }

    [Fact(DisplayName = "STALE: decodes already in flight from PRELOAD (no cancellation possible) finishing in reverse order never overwrite the new image")]
    public async Task JoinedPreloadDecodes_FinishingInReverseOrder_NeverOverwriteTheNewImage()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        var preloads = ThreeFiles.Select((_, i) => harness.Service.GetPreviewAsync(harness.Paths[i])).ToArray();
        await harness.PreviewStartedAsync(0);
        await harness.PreviewStartedAsync(1);
        await harness.PreviewStartedAsync(2);
        for (var i = 0; i < 3; i++) await harness.NavigateAsync(i);   // each joins the preload of its file

        await harness.ReleasePreviewAsync(2);
        await harness.ReleasePreviewAsync(1);     // finishes after file 2 was presented
        await harness.ReleasePreviewAsync(0);
        await Task.WhenAll(preloads).WaitAsync(StalePreviewHarness.Bound);

        harness.AssertNoStaleOverwrite("joined preloads in reverse");
        await harness.AssertSettledOnAsync(2);
        Assert.All(preloads, p => Assert.True(p.IsCompletedSuccessfully));
    }

    [Fact(DisplayName = "STALE: a superseded navigation whose decode FAILS late shows no error and removes nothing from the catalog")]
    public async Task LateDecodeFailureOfAnOlderNavigation_NeitherShowsAnErrorNorTouchesTheCatalog()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        harness.FailPreview(0, new FileNotFoundException("gone", harness.Paths[0])); // would drop the entry if it were current
        await harness.NavigateAsync(0);
        await harness.PreviewStartedAsync(0);
        await harness.NavigateAsync(1);
        await harness.PreviewStartedAsync(1);
        await harness.ReleasePreviewAsync(1);

        await harness.ReleasePreviewAsync(0);

        harness.AssertNoStaleOverwrite("late failure");
        await harness.AssertSettledOnAsync(1);
        Assert.Equal(3, harness.Catalog.Count);
        Assert.Equal(harness.Paths, harness.Catalog.Paths);
    }

    [Fact(DisplayName = "STALE: ClearCache while the current image decodes: that navigation still presents; a superseded one does not repopulate the cache")]
    public async Task ClearCacheDuringDecodes_CurrentNavigationStillPresents_AndNothingStaleIsCached()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        await harness.NavigateAsync(0);
        await harness.PreviewStartedAsync(0);
        await harness.NavigateAsync(1);
        await harness.PreviewStartedAsync(1);

        harness.Service.ClearCache();
        await harness.ReleasePreviewAsync(0);
        await harness.ReleasePreviewAsync(1);

        harness.AssertNoStaleOverwrite("clear cache");
        await harness.AssertSettledOnAsync(1);
        Assert.Equal(0, harness.Service.CacheCount); // both finished after the epoch moved on: neither is published
    }

    [Fact(DisplayName = "STALE: a full-resolution original (zoom) that finishes after the user moved on changes neither bitmap, size nor view mode")]
    public async Task LateZoomOriginal_AfterNavigatingAway_ChangesNothingOnScreen()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        await harness.NavigateAsync(0);
        await harness.ReleasePreviewAsync(0);
        await harness.AssertSettledOnAsync(0);
        await harness.OnUiAsync(() => harness.Presenter.SetViewerZoom(2.0)); // arms the original decode of file 0
        await harness.OriginalStartedAsync(0);
        await harness.NavigateAsync(1);
        await harness.ReleasePreviewAsync(1);
        await harness.AssertSettledOnAsync(1);
        var eventsBefore = harness.Events.Count;

        harness.OpenOriginal(0);                 // the original of file 0 finishes late
        await harness.SettleAsync();

        harness.AssertNoStaleOverwrite("late zoom original");
        Assert.Equal(eventsBefore, harness.Events.Count);
        Assert.False(await harness.Ui.InvokeAsync(() => harness.Presenter.ZoomDetail.IsShowingOriginal));
        await harness.AssertSettledOnAsync(1);
    }

    [Fact(DisplayName = "STALE: the original of the CURRENT image replaces its preview (same file, original stage) and a later navigation drops it")]
    public async Task ZoomOriginal_OfTheCurrentImage_ReplacesThePreview_ThenNavigationReleasesIt()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        await harness.NavigateAsync(0);
        await harness.ReleasePreviewAsync(0);
        await harness.OnUiAsync(() => harness.Presenter.SetViewerZoom(2.0));
        await harness.OriginalStartedAsync(0);

        var pending = await harness.Ui.InvokeAsync(() => harness.Presenter.ZoomDetail.PendingLoad);
        Assert.NotNull(pending);                  // the original is really in flight
        harness.OpenOriginal(0);
        await pending!.WaitAsync(StalePreviewHarness.Bound);

        await harness.AssertSettledOnAsync(0, ShownStage.Original);
        harness.AssertNoStaleOverwrite("original of current");
        await harness.NavigateAsync(1);
        await harness.ReleasePreviewAsync(1);
        await harness.AssertSettledOnAsync(1);
        Assert.Null(await harness.Ui.InvokeAsync(() => harness.Presenter.ZoomDetail.HeldOriginal));
    }

    // ---- ImagePresenter: compare (two cells) ----------------------------------------------------

    private static readonly string[] PairFiles = ["a.jpg", "a (1).jpg", "b.jpg", "b (1).jpg", "c.jpg"];

    [Fact(DisplayName = "STALE compare: the partner of a SUPERSEDED pair finishing late neither reopens compare nor shows its images or status")]
    public async Task ComparePartnerOfASupersededPair_FinishingLate_ChangesNothing()
    {
        using var harness = new StalePreviewHarness(PairFiles);
        await harness.NavigateAsync(0);               // a.jpg: main image, then the pair (a, a (1)) loads
        harness.OpenPreview(0);
        await harness.PreviewStartedAsync(1);         // a (1).jpg is the pending cell: the navigation is inside compare loading
        await harness.NavigateAsync(4);               // c.jpg: not in a pair
        await harness.ReleasePreviewAsync(4);
        await harness.AssertSettledOnAsync(4);

        harness.OpenPreview(1);                       // the partner arrives after the user left
        await harness.SettleAsync();

        harness.AssertNoStaleOverwrite("late partner");
        Assert.False(harness.Compare.IsVisible);
        Assert.Null(harness.Compare.LeftImage);
        Assert.Null(harness.Compare.RightImage);
        Assert.Null(harness.Compare.LeftPath);
        Assert.DoesNotContain(harness.Events, e => e.Image is { Path: var p } && p == harness.Paths[1]);
        await harness.AssertSettledOnAsync(4);
    }

    [Fact(DisplayName = "STALE compare: pair A superseded by pair B, B's cells arrive first, then A's late cell: compare shows B's two images")]
    public async Task ComparePairOverwrittenByANewerPair_LateCellOfTheOldPair_IsDropped()
    {
        using var harness = new StalePreviewHarness(PairFiles);
        await harness.NavigateAsync(0);               // pair (a, a (1))
        harness.OpenPreview(0);
        await harness.PreviewStartedAsync(1);
        await harness.NavigateAsync(2);               // pair (b, b (1))
        harness.OpenPreview(2);
        await harness.PreviewStartedAsync(3);

        harness.OpenPreview(3);                       // B completes first
        await harness.Ui.InvokeAsync(() => 0).WaitAsync(StalePreviewHarness.Bound);
        var navB = harness.Navs[1];
        await navB.Task.WaitAsync(StalePreviewHarness.Bound);
        harness.OpenPreview(1);                       // A's cell arrives afterwards
        await harness.SettleAsync();

        harness.AssertNoStaleOverwrite("pair overwritten");
        Assert.True(harness.Compare.IsVisible);
        Assert.Equal(harness.Paths[2], harness.Compare.LeftPath);
        Assert.Equal(harness.Paths[3], harness.Compare.RightPath);
        Assert.Equal(harness.Paths[2], ((ShownImage)harness.Compare.LeftImage!).Path);
        Assert.Equal(harness.Paths[3], ((ShownImage)harness.Compare.RightImage!).Path);
        Assert.Contains("b (1).jpg", harness.Presenter.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("a (1).jpg", harness.Presenter.StatusText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "STALE: the refresh stat after an image was presented finishes after the user moved on: the old image's Ready status and session are not applied")]
    public async Task PostPresentStat_OfASupersededNavigation_FinishingLate_AppliesNothing()
    {
        using var harness = new StalePreviewHarness(ThreeFiles);
        harness.StatGate.HoldSecondStat(harness.Paths[0]);
        await harness.NavigateAsync(0);
        harness.OpenPreview(0);
        await harness.StatGate.SecondStatStarted(harness.Paths[0]).WaitAsync(StalePreviewHarness.Bound); // file 0 IS on screen
        var onScreen = await harness.Ui.InvokeAsync(() => harness.Presenter.CurrentImage as ShownImage);
        Assert.Equal(harness.Paths[0], onScreen!.Path, StringComparer.OrdinalIgnoreCase);
        await harness.NavigateAsync(1);
        harness.OpenPreview(1);

        harness.StatGate.ReleaseSecondStat(harness.Paths[0]);
        await harness.SettleAsync();

        harness.AssertNoStaleOverwrite("late refresh stat");
        await harness.AssertSettledOnAsync(1);
    }

    [Fact(DisplayName = "STALE compare: the compare partner of a superseded navigation FAILS late: the new image and its status are untouched")]
    public async Task ComparePartnerFailingLate_OfASupersededNavigation_ChangesNothing()
    {
        using var harness = new StalePreviewHarness(PairFiles);
        harness.FailPreview(1, new InvalidOperationException("partner is broken"));
        await harness.NavigateAsync(0);
        harness.OpenPreview(0);
        await harness.PreviewStartedAsync(1);         // inside compare loading
        await harness.NavigateAsync(4);
        await harness.ReleasePreviewAsync(4);
        await harness.AssertSettledOnAsync(4);

        harness.OpenPreview(1);                       // the partner decode now throws, after the user left
        await harness.SettleAsync();

        harness.AssertNoStaleOverwrite("late partner failure");
        Assert.DoesNotContain(harness.Events, e => e.Kind == UiEventKind.Status && e.Text!.Contains("broken", StringComparison.OrdinalIgnoreCase));
        await harness.AssertSettledOnAsync(4);
        Assert.Equal(PairFiles.Length, harness.Catalog.Count);
    }}
