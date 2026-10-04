using System.Diagnostics;
using PhotoReview.Core.Catalog;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class CaptureGroupBuilderTests(ITestOutputHelper output)
{
    [Fact]
    public void Build_MatchesSameFolderAndBasenameIgnoringCase_AndIncludesXmp()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\IMG_0001.JPG", @"C:\photos\img_0001.cr3", @"C:\photos\Img_0001.xmp"],
            [@"C:\photos\IMG_0001.xmp"]);

        var group = Assert.Single(groups);
        Assert.Equal(@"C:\photos\IMG_0001.JPG", group.JpegPath);
        Assert.Equal(@"C:\photos\img_0001.cr3", group.RawPath);
        Assert.Equal(@"C:\photos\IMG_0001.xmp", group.XmpPath);
        Assert.Equal(3, group.Paths.Count);
    }

    [Fact]
    public void Build_LeavesUnpairedAndAmbiguousFilesSeparate()
    {
        var groups = CaptureGroupBuilder.Build(
        [
            @"C:\photos\only-raw.nef",
            @"C:\photos\only-jpeg.jpg",
            @"C:\photos\ambiguous.jpg",
            @"C:\photos\ambiguous.jpeg",
            @"C:\photos\ambiguous.arw",
            @"C:\photos\valid.jpg",
            @"C:\photos\valid.nef",
            @"C:\other\valid.cr2"
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(@"C:\photos\valid.jpg", group.JpegPath);
        Assert.Equal(@"C:\photos\valid.nef", group.RawPath);
        Assert.Null(group.XmpPath);
    }

    [Fact]
    public void Build_IgnoresUnmatchedAndAmbiguousSidecars()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\a.jpg", @"C:\photos\a.cr2", @"C:\photos\b.jpg", @"C:\photos\b.cr2"],
            [@"C:\photos\a.xmp", @"C:\photos\a.XMP", @"C:\photos\orphan.xmp"]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.XmpPath));
    }

    [Theory]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    public void Build_NonJpegImageNextToRaw_IsNotGrouped(string extension)
    {
        var groups = CaptureGroupBuilder.Build([@"C:\photos\A" + extension, @"C:\photos\A.dng"]);

        Assert.Empty(groups);
    }

    [Fact]
    public void Build_NonJpegImageBesideJpegAndRaw_DoesNotMakePairAmbiguousOrJoinIt()
    {
        var groups = CaptureGroupBuilder.Build([@"C:\photos\A.jpg", @"C:\photos\A.tif", @"C:\photos\A.dng"]);

        var group = Assert.Single(groups);
        Assert.DoesNotContain(@"C:\photos\A.tif", group.Paths);
    }

    [Fact]
    public void Build_SidecarNamedBaseXmpWinsOverFileExtensionSidecars()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\a.jpg", @"C:\photos\a.cr2"],
            [@"C:\photos\a.cr2.xmp", @"C:\photos\a.xmp"]);

        Assert.Equal(@"C:\photos\a.xmp", Assert.Single(groups).XmpPath);
    }

    [Fact]
    public void Build_SingleFileExtensionSidecar_IsIncludedWhenNoBaseNamedSidecarExists()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\a.jpg", @"C:\photos\a.cr2"],
            [@"C:\photos\A.CR2.xmp"]);

        Assert.Equal(@"C:\photos\A.CR2.xmp", Assert.Single(groups).XmpPath);
    }

    [Fact]
    public void Build_TwoCompetingFileExtensionSidecars_AreAmbiguousAndLeftOutOfTheGroup()
    {
        // Documented behavior: with no base-named sidecar and two candidates the group is still built (so the pair
        // stays together) but no sidecar is claimed; Recycle/Move leave both .xmp files where they are.
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\a.jpg", @"C:\photos\a.cr2"],
            [@"C:\photos\a.cr2.xmp", @"C:\photos\a.jpg.xmp"]);

        var group = Assert.Single(groups);
        Assert.Null(group.XmpPath);
        Assert.Equal(2, group.Paths.Count);
    }

    [Theory]
    [InlineData(PhotoReview.Core.Model.RawPairMode.PreferJpeg)]
    [InlineData(PhotoReview.Core.Model.RawPairMode.PreferRaw)]
    public void GroupEntries_CollapsedEntryTakesPositionOfFirstMemberSeen_NotTheRepresentative(PhotoReview.Core.Model.RawPairMode mode)
    {
        // Documented behavior for name/size/date sorts: the pair's single entry sits where its FIRST member appears in
        // the incoming order, even when the representative (JPEG or RAW per mode) appears later.
        var order = new[]
        {
            new CatalogEntry(@"C:\photos\a.cr2"), new CatalogEntry(@"C:\photos\b.jpg"), new CatalogEntry(@"C:\photos\a.jpg"),
        };

        var grouped = CaptureGroupBuilder.GroupEntries(order, mode);

        Assert.Equal(2, grouped.Count);
        Assert.Equal(mode == PhotoReview.Core.Model.RawPairMode.PreferJpeg ? @"C:\photos\a.jpg" : @"C:\photos\a.cr2", grouped[0].Path);
        Assert.Equal(@"C:\photos\b.jpg", grouped[1].Path);
    }

    [Fact]
    public void GroupEntries_UsesSelectedRepresentativeAndKeepsBothMembersAddressable()
    {
        var jpeg = new CatalogEntry(@"C:\photos\a.jpg") { Length = 10, Width = 3000 };
        var raw = new CatalogEntry(@"C:\photos\a.cr2") { Length = 100, Width = 6000 };
        var other = new CatalogEntry(@"C:\photos\b.jpg");
        var catalog = new ReviewCatalog();

        catalog.Reset([jpeg, raw, other], PhotoReview.Core.Model.RawPairMode.PreferJpeg,
            [@"C:\photos\a.xmp"]);

        Assert.Equal(2, catalog.Count);
        Assert.Equal(jpeg.Path, catalog.PathAt(0));
        Assert.Equal(0, catalog.IndexOf(raw.Path));
        Assert.Equal(10, catalog.Find(raw.Path)!.Length);
        Assert.Equal([jpeg.Path, raw.Path, @"C:\photos\a.xmp"], catalog.Current!.CaptureGroup!.Paths);
    }

    [Fact]
    public void GroupEntries_PreferRawUsesRawMetadata_AndExplorerOrderCollapsesMembers()
    {
        var jpeg = new CatalogEntry(@"C:\photos\a.jpg") { Length = 10 };
        var raw = new CatalogEntry(@"C:\photos\a.cr2") { Length = 100 };
        var other = new CatalogEntry(@"C:\photos\b.jpg");
        var catalog = new ReviewCatalog();
        catalog.Reset([jpeg, raw, other], PhotoReview.Core.Model.RawPairMode.PreferRaw);

        Assert.Equal(raw.Path, catalog.PathAt(0));
        Assert.Equal(100, catalog.Find(jpeg.Path)!.Length);
        Assert.True(catalog.ReplaceOrder([other.Path, raw.Path, jpeg.Path]));
        Assert.Equal([other.Path, raw.Path], catalog.Paths);
        Assert.Equal(1, catalog.IndexOf(jpeg.Path));
    }

    [Fact]
    public void Reset_DefaultModeKeepsPairMembersSeparate()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\photos\a.jpg", @"C:\photos\a.cr2"]);

        Assert.Equal(2, catalog.Count);
        Assert.All(catalog.EntriesSnapshot(), entry => Assert.Null(entry.CaptureGroup));
    }

    private static List<CatalogEntry> TenThousandPairedEntries()
    {
        var entries = new List<CatalogEntry>(10_000);
        for (var index = 0; index < 5_000; index++)
        {
            var basename = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            entries.Add(new CatalogEntry($@"C:\photos\{basename}.jpg"));
            entries.Add(new CatalogEntry($@"C:\photos\{basename}.cr2"));
        }

        return entries;
    }

    /// <summary>Measured 4146 KiB after the bucket/presize optimisation (6504 KiB before it); 5 MiB fails the old code and leaves ~20% headroom.</summary>
    private const long AllocationBudgetBytes = 5 * 1024 * 1024;

    [Fact]
    [Trait("Category", "HotPath")]
    public void GroupEntries_TenThousandPaths_GroupsAllPairsWithBoundedAllocation()
    {
        // Deterministic guard for the 10k-entry hot path. It replaces a wall-clock budget, which is meaningless on a
        // shared, parallel runner (see docs/TESTING.md > Timing tests). Allocation on the calling thread does not depend
        // on machine load, and a per-path regression (LINQ, per-group lists, re-hashing...) shows up as a multiple of it.
        var entries = TenThousandPairedEntries();
        _ = CaptureGroupBuilder.GroupEntries(entries, PhotoReview.Core.Model.RawPairMode.PreferJpeg); // warm-up (JIT/static init)

        var before = GC.GetAllocatedBytesForCurrentThread();
        var grouped = CaptureGroupBuilder.GroupEntries(entries, PhotoReview.Core.Model.RawPairMode.PreferJpeg);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(5_000, grouped.Count);
        Assert.All(grouped, entry => Assert.NotNull(entry.CaptureGroup));
        output.WriteLine($"10k paths (5k pairs): {allocated / 1024} KiB allocated ({allocated / 10_000.0:F0} B/path).");
        Assert.True(allocated < AllocationBudgetBytes, $"GroupEntries allocated {allocated / 1024} KiB for 10k paths (budget {AllocationBudgetBytes / 1024} KiB).");
    }

    /// <summary>Timing report for a human, never in the gate: the wall-clock assertion this replaces flaked at ~2x under a full parallel run.</summary>
    [Fact]
    [Trait("Category", "Manual")]
    public void GroupEntries_TenThousandPaths_TimingReport()
    {
        var entries = TenThousandPairedEntries();
        _ = CaptureGroupBuilder.GroupEntries(entries, PhotoReview.Core.Model.RawPairMode.PreferJpeg);
        var samples = new double[15];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            var grouped = CaptureGroupBuilder.GroupEntries(entries, PhotoReview.Core.Model.RawPairMode.PreferJpeg);
            samples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(5_000, grouped.Count);
        }

        Array.Sort(samples);
        output.WriteLine($"10k paths (5k pairs) grouping time: min {samples[0]:F2} ms, median {samples[7]:F2} ms, max {samples[^1]:F2} ms.");
    }
}
