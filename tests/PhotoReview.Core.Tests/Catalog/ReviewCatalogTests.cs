using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class ReviewCatalogTests
{
    [Fact]
    public void RestoreMembers_SeparateMode_ReinsertsOnlyUniquePathsAsIndependentEntries()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([new CatalogEntry(@"C:\photos\before.jpg"), new CatalogEntry(@"C:\photos\after.jpg")]);
        catalog.RestoreMembers([@"C:\photos\raw.cr2", @"C:\photos\jpeg.jpg", @"C:\photos\raw.cr2"], 1);

        Assert.Equal([@"C:\photos\before.jpg", @"C:\photos\raw.cr2", @"C:\photos\jpeg.jpg", @"C:\photos\after.jpg"], catalog.Paths);
        Assert.All(catalog.EntriesSnapshot(), entry => Assert.Null(entry.CaptureGroup));
    }

    [Fact]
    public void RestoreMembers_RawSupportOff_RestoresOnlyTheJpegAsAPlainEntry()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([new CatalogEntry(@"C:\photos\before.jpg"), new CatalogEntry(@"C:\photos\after.jpg")]);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");

        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], 1, group, rawEnabled: false);

        Assert.Equal([@"C:\photos\before.jpg", @"C:\photos\p.jpg", @"C:\photos\after.jpg"], catalog.Paths);
        Assert.All(catalog.EntriesSnapshot(), entry => Assert.Null(entry.CaptureGroup));
    }

    [Fact]
    public void Reset_PathsOverload_ForgetsThePreviousPairMode()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");

        catalog.Reset([@"C:\photos\before.jpg", @"C:\photos\after.jpg"]); // plain paths: Separate again
        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], 1, group);

        Assert.Equal([@"C:\photos\before.jpg", @"C:\photos\p.jpg", @"C:\photos\p.cr2", @"C:\photos\after.jpg"], catalog.Paths);
        Assert.All(catalog.EntriesSnapshot(), entry => Assert.Null(entry.CaptureGroup));
    }

    [Fact]
    public void Reset_EntriesOverload_ForgetsThePreviousPairMode()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");

        catalog.Reset([new CatalogEntry(@"C:\photos\before.jpg"), new CatalogEntry(@"C:\photos\after.jpg")]);
        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], 1, group);

        Assert.Equal(4, catalog.Count);
        Assert.All(catalog.EntriesSnapshot(), entry => Assert.Null(entry.CaptureGroup));
    }

    [Fact]
    public void UpdateMetadata_PathOfANonRepresentativeMember_IsANoOp()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([new CatalogEntry(@"C:\photos\p.jpg"), new CatalogEntry(@"C:\photos\p.cr2")], RawPairMode.PreferJpeg);
        var stamp = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        var applied = catalog.UpdateMetadata(@"C:\photos\p.cr2", 99, stamp, 6000, 4000); // the RAW: not the entry's own file

        Assert.False(applied);
        var entry = Assert.Single(catalog.EntriesSnapshot());
        Assert.Null(entry.Length);
        Assert.Null(entry.LastWriteUtc);
        Assert.Null(entry.Width);
    }

    [Fact]
    public void UpdateMetadata_PathOfTheRepresentative_StampsTheGroupEntry()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([new CatalogEntry(@"C:\photos\p.jpg"), new CatalogEntry(@"C:\photos\p.cr2")], RawPairMode.PreferJpeg);
        var stamp = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        var applied = catalog.UpdateMetadata(@"C:\photos\P.JPG", 7, stamp);

        Assert.True(applied);
        Assert.Equal(7, catalog.EntriesSnapshot()[0].Length);
        Assert.NotNull(catalog.EntriesSnapshot()[0].CaptureGroup);
    }

    private static ReviewCatalog PairCatalog(RawPairMode mode)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([new CatalogEntry(@"C:\photos\before.jpg"), new CatalogEntry(@"C:\photos\after.jpg")], mode);
        return catalog;
    }

    [Theory]
    [InlineData(RawPairMode.PreferJpeg, @"C:\photos\p.jpg")]
    [InlineData(RawPairMode.PreferRaw, @"C:\photos\p.cr2")]
    public void RestoreMembers_BothImageMembers_RestoresOneGroupedEntryWithRepresentativeForTheMode(RawPairMode mode, string representative)
    {
        var catalog = PairCatalog(mode);

        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], 1);

        Assert.Equal([@"C:\photos\before.jpg", representative, @"C:\photos\after.jpg"], catalog.Paths);
        var entry = catalog.EntriesSnapshot()[1];
        Assert.NotNull(entry.CaptureGroup);
        Assert.Equal(@"C:\photos\p.jpg", entry.CaptureGroup!.JpegPath);
        Assert.Equal(@"C:\photos\p.cr2", entry.CaptureGroup.RawPath);
        Assert.Equal(1, catalog.IndexOf(@"C:\photos\p.jpg"));
        Assert.Equal(1, catalog.IndexOf(@"C:\photos\p.cr2"));
    }

    [Fact]
    public void RestoreMembers_SidecarInPaths_NeverBecomesAnEntryButTravelsInTheGroup()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);

        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2", @"C:\photos\p.xmp"], 1);

        Assert.Equal(3, catalog.Count);
        Assert.DoesNotContain(catalog.Paths, path => path.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\photos\p.xmp", catalog.EntriesSnapshot()[1].CaptureGroup!.XmpPath);
        Assert.Equal(-1, catalog.IndexOf(@"C:\photos\p.xmp"));
    }

    [Fact]
    public void RestoreMembers_OnlyASidecar_RestoresNothing()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        var version = catalog.StructuralVersion;

        catalog.RestoreMembers([@"C:\photos\p.xmp"], 0);

        Assert.Equal(2, catalog.Count);
        Assert.Equal(version, catalog.StructuralVersion);
    }

    [Fact]
    public void RestoreMembers_ExplicitGroup_IsPreservedIncludingItsSidecar()
    {
        var catalog = PairCatalog(RawPairMode.PreferRaw);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2", @"C:\photos\p.cr2.xmp");

        catalog.RestoreMembers(group.ImagePaths, 0, group);

        Assert.Equal(@"C:\photos\p.cr2", catalog.PathAt(0));
        Assert.Same(group, catalog.EntriesSnapshot()[0].CaptureGroup);
    }

    [Fact]
    public void RestoreMembers_OneMemberOfAPair_RestoresAnUngroupedEntry()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);

        catalog.RestoreMembers([@"C:\photos\p.cr2"], 1);

        Assert.Equal(@"C:\photos\p.cr2", catalog.PathAt(1));
        Assert.Null(catalog.EntriesSnapshot()[1].CaptureGroup);
    }

    [Fact]
    public void RestoreMembers_KeepsCurrentIndexOnTheDisplayedImage()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        catalog.SetCurrent(1); // showing "after.jpg"

        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], 1);

        Assert.Equal(@"C:\photos\after.jpg", catalog.Current!.Path); // not yanked to the restored capture
        Assert.Equal(2, catalog.CurrentIndex);
    }

    [Fact]
    public void RestoreMembers_IntoEmptyCatalog_MakesTheRestoredEntryCurrent()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([], RawPairMode.PreferJpeg);

        catalog.RestoreMembers([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], 0);

        Assert.Equal(1, catalog.Count);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact]
    public void CatalogEntry_ValidPath_InitializesCorrectly()
    {
        var entry = new CatalogEntry(@"C:\Photos\photo1.jpg");
        Assert.Equal(@"C:\Photos\photo1.jpg", entry.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CatalogEntry_InvalidPath_ThrowsArgumentException(string? invalidPath)
    {
        Assert.Throws<ArgumentException>(() => new CatalogEntry(invalidPath!));
    }

    [Fact]
    public void EmptyCatalog_PropertiesAndBoundaries()
    {
        var catalog = new ReviewCatalog();

        Assert.Equal(0, catalog.Count);
        Assert.Equal(-1, catalog.CurrentIndex);
        Assert.Null(catalog.Current);
        Assert.Empty(catalog.Paths);
        Assert.Empty(catalog.Paths);

        Assert.Equal(-1, catalog.IndexOf("anything.jpg"));
        Assert.False(catalog.SetCurrent(0));
        Assert.True(catalog.SetCurrent(-1));
        Assert.Equal(-1, catalog.Remove("anything.jpg"));
    }

    [Fact]
    public void SingleItem_RemovalEmptiesCatalog()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["photo1.jpg"]);

        Assert.Equal(1, catalog.Count);
        Assert.Equal(0, catalog.CurrentIndex);
        Assert.NotNull(catalog.Current);
        Assert.Equal("photo1.jpg", catalog.Current.Path);

        var nextIndex = catalog.Remove("photo1.jpg");
        Assert.Equal(-1, nextIndex);
        Assert.Equal(0, catalog.Count);
        Assert.Equal(-1, catalog.CurrentIndex);
        Assert.Null(catalog.Current);
    }

    [Fact]
    public void Remove_MiddleAndLast_CalculatesNextIndexCorrectly()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["img1.jpg", "img2.jpg", "img3.jpg"]);
        catalog.SetCurrent(1); // Pointing to img2.jpg

        // Remove middle item (img2.jpg)
        var next = catalog.Remove("img2.jpg");
        Assert.Equal(1, next);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal("img3.jpg", catalog.Current?.Path);
        Assert.Equal(2, catalog.Count);

        // Current is now at index 1 ("img3.jpg", which is the last item)
        // Remove last item
        next = catalog.Remove("img3.jpg");
        Assert.Equal(0, next);
        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal("img1.jpg", catalog.Current?.Path);
        Assert.Equal(1, catalog.Count);
    }

    [Fact(DisplayName = "RV-C09: removing an entry before the current one keeps the same current photo (index shifts down)")]
    public void Remove_EntryBeforeCurrent_KeepsSameCurrentPath()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["img1.jpg", "img2.jpg", "img3.jpg", "img4.jpg"]);
        catalog.SetCurrent(2); // img3.jpg

        var next = catalog.Remove("img1.jpg");

        Assert.Equal(1, next);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal("img3.jpg", catalog.Current?.Path);
    }

    [Fact(DisplayName = "RV-C09: removing an entry after the current one leaves the current photo and index unchanged")]
    public void Remove_EntryAfterCurrent_KeepsSameCurrentPath()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["img1.jpg", "img2.jpg", "img3.jpg", "img4.jpg"]);
        catalog.SetCurrent(1); // img2.jpg

        var next = catalog.Remove("img4.jpg");

        Assert.Equal(1, next);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal("img2.jpg", catalog.Current?.Path);
    }

    [Fact]
    public void Restore_ClampsIndexAndPreventsDuplicates()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["img1.jpg", "img3.jpg"]);

        // Cannot restore duplicate
        Assert.False(catalog.Restore("img1.jpg", 0));
        Assert.Equal(2, catalog.Count);

        // Restore with index = -1 -> clamped to 0
        Assert.True(catalog.Restore("img0.jpg", -1));
        Assert.Equal(3, catalog.Count);
        Assert.Equal("img0.jpg", catalog.Paths[0]);

        // Restore with large index -> clamped to end (Count)
        Assert.True(catalog.Restore("img4.jpg", 999));
        Assert.Equal(4, catalog.Count);
        Assert.Equal("img4.jpg", catalog.Paths[3]);

        // Restore at middle
        Assert.True(catalog.Restore("img2.jpg", 2));
        Assert.Equal(5, catalog.Count);
        Assert.Equal("img2.jpg", catalog.Paths[2]);
    }

    [Theory]
    [InlineData("a.jpg", "a.jpg", "c.jpg")]
    [InlineData("A.JPG", "a.jpg", "c.jpg")]
    [InlineData("a.jpg", "b.jpg", "b.jpg")]
    public void ReplaceOrder_RejectsDuplicatesEvenWhenCountAndMembersMatch(string x, string y, string z)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["a.jpg", "b.jpg", "c.jpg"]);
        catalog.SetCurrent(1);
        var versionBefore = catalog.StructuralVersion;

        Assert.False(catalog.ReplaceOrder([x, y, z]));

        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], catalog.Paths);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal(versionBefore, catalog.StructuralVersion);
    }

    [Fact]
    public void ReplaceOrder_PreservesCurrentByPath_AndRejectsMismatchedSet()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["a.jpg", "b.jpg", "c.jpg"]);
        catalog.SetCurrent(2); // Current is c.jpg

        // Mismatched count (fewer items)
        Assert.False(catalog.ReplaceOrder(["a.jpg", "b.jpg"]));
        Assert.Equal(2, catalog.CurrentIndex);

        // Mismatched set (same count, different item)
        Assert.False(catalog.ReplaceOrder(["a.jpg", "b.jpg", "d.jpg"]));
        Assert.Equal(2, catalog.CurrentIndex);

        // Exact match with different order
        Assert.True(catalog.ReplaceOrder(["c.jpg", "a.jpg", "b.jpg"]));
        // c.jpg moved to index 0, CurrentIndex must track it to index 0
        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal("c.jpg", catalog.Current?.Path);
        Assert.Equal(["c.jpg", "a.jpg", "b.jpg"], catalog.Paths);
    }

    [Fact]
    public void Paths_TakenBeforeAChange_IsNotMutatedByIt()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["img1.jpg", "img2.jpg"]);

        var snapshot = catalog.Paths;
        Assert.Equal(["img1.jpg", "img2.jpg"], snapshot);

        catalog.Remove("img1.jpg");
        Assert.Single(catalog.Paths);

        // Snapshot is not mutated
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("img1.jpg", snapshot[0]);
    }

    [Fact(DisplayName = "AR16 RemovePaths: removing other entries keeps the current path, at its new index")]
    public void RemovePaths_NonCurrent_KeepsCurrentByPath()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg"]);
        catalog.SetCurrent(2);
        var version = catalog.StructuralVersion;

        var removed = catalog.RemovePaths(["D.JPG", "a.jpg", "ghost.jpg"]);

        Assert.Equal(["a.jpg", "d.jpg"], removed);
        Assert.Equal(["b.jpg", "c.jpg", "e.jpg"], catalog.Paths);
        Assert.Equal("c.jpg", catalog.Current?.Path);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal(-1, catalog.IndexOf("a.jpg"));
        Assert.NotEqual(version, catalog.StructuralVersion);
    }

    [Theory(DisplayName = "AR16 RemovePaths: a removed current advances exactly like Remove (Delete)")]
    [InlineData(1, new[] { "b.jpg" }, "c.jpg")]
    [InlineData(1, new[] { "b.jpg", "c.jpg" }, "d.jpg")]
    [InlineData(4, new[] { "e.jpg" }, "d.jpg")]
    [InlineData(3, new[] { "d.jpg", "e.jpg" }, "c.jpg")]
    [InlineData(0, new[] { "a.jpg", "c.jpg" }, "b.jpg")]
    public void RemovePaths_Current_AdvancesLikeDelete(int current, string[] unreadable, string expectedCurrent)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg"]);
        catalog.SetCurrent(current);

        catalog.RemovePaths(unreadable);

        // Delete semantics: the first surviving neighbour after the removed current, else the last one.
        Assert.Equal(5 - unreadable.Length, catalog.Count);
        Assert.Equal(expectedCurrent, catalog.Current?.Path);
        Assert.Equal(catalog.IndexOf(expectedCurrent), catalog.CurrentIndex);
    }

    [Fact(DisplayName = "AR16 RemovePaths: removing everything empties the catalog; nothing to remove changes nothing")]
    public void RemovePaths_AllOrNone()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["a.jpg", "b.jpg"]);
        var version = catalog.StructuralVersion;

        Assert.Empty(catalog.RemovePaths(["x.jpg"]));
        Assert.Equal(version, catalog.StructuralVersion);
        Assert.Equal(0, catalog.CurrentIndex);

        Assert.Equal(2, catalog.RemovePaths(["a.jpg", "b.jpg"]).Count);
        Assert.Equal(0, catalog.Count);
        Assert.Equal(-1, catalog.CurrentIndex);
    }

    [Theory]
    [InlineData(RawPairMode.PreferJpeg, @"C:\photos\p.jpg", @"C:\photos\p.cr2", @"C:\photos\p.jpg")]
    [InlineData(RawPairMode.PreferJpeg, @"C:\photos\p.cr2", @"C:\photos\p.jpg", @"C:\photos\p.cr2")] // current: the displayed member stays
    [InlineData(RawPairMode.PreferRaw, @"C:\photos\p.jpg", @"C:\photos\p.cr2", @"C:\photos\p.jpg")]
    public void RestoreMembers_SecondMemberComesBackAfterFirstWasRestoredAlone_ReformsTheGroupedEntryInPlace(
        RawPairMode mode, string firstRestored, string laterRestored, string representative)
    {
        var catalog = PairCatalog(mode);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2", @"C:\photos\p.xmp");
        catalog.RestoreMembers([firstRestored], 1, group); // partial undo: only one member came back
        Assert.Null(catalog.EntriesSnapshot()[1].CaptureGroup);
        catalog.SetCurrent(1);

        var restored = catalog.RestoreMembers([laterRestored], 1, group); // retry restores the partner

        Assert.True(restored);
        Assert.Equal([@"C:\photos\before.jpg", representative, @"C:\photos\after.jpg"], catalog.Paths);
        Assert.Equal(group, catalog.EntriesSnapshot()[1].CaptureGroup);
        Assert.Equal(1, catalog.IndexOf(@"C:\photos\p.jpg"));
        Assert.Equal(1, catalog.IndexOf(@"C:\photos\p.cr2"));
        Assert.Equal(1, catalog.CurrentIndex); // still on the same photo
    }

    [Theory]
    [InlineData(RawPairMode.PreferJpeg, @"C:\photos\p.cr2", @"C:\photos\p.jpg")]
    [InlineData(RawPairMode.PreferRaw, @"C:\photos\p.jpg", @"C:\photos\p.cr2")]
    public void RestoreMembers_ReformOfANonCurrentEntry_FollowsThePairMode(RawPairMode mode, string firstRestored, string representative)
    {
        var catalog = PairCatalog(mode);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");
        catalog.RestoreMembers([firstRestored], 1, group);
        catalog.SetCurrent(2); // viewing after.jpg

        catalog.RestoreMembers([mode == RawPairMode.PreferJpeg ? @"C:\photos\p.jpg" : @"C:\photos\p.cr2"], 1, group);

        Assert.Equal(representative, catalog.PathAt(1));
        Assert.Equal(group, catalog.EntriesSnapshot()[1].CaptureGroup);
        Assert.Equal(2, catalog.CurrentIndex);
    }

    [Fact]
    public void RestoreMembers_ReformWhenBothMembersWereStandaloneAndSecondIsCurrent_KeepsTheDisplayedMemberAndIndex()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");
        catalog.RestoreMembers([@"C:\photos\p.jpg"], 1, group);
        catalog.Restore(@"C:\photos\p.cr2", 2); // the partner was listed as its own entry too
        catalog.SetCurrent(2); // viewing the RAW

        catalog.RestoreMembers([@"C:\photos\p.cr2"], 1, group);

        Assert.Equal(3, catalog.Count);
        Assert.Equal(@"C:\photos\p.cr2", catalog.Current!.Path);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal(group, catalog.Current.CaptureGroup);
    }

    private static ReviewCatalog GroupedCatalog(RawPairMode mode, params string[] extraStandalone)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(extraStandalone.Prepend(@"C:\photos\p.cr2").Prepend(@"C:\photos\p.jpg").Select(path => new CatalogEntry(path)), mode);
        return catalog;
    }

    [Theory]
    [InlineData(RawPairMode.PreferRaw, @"C:\photos\p.cr2", @"C:\photos\p.jpg")] // unreadable representative
    [InlineData(RawPairMode.PreferJpeg, @"C:\photos\p.jpg", @"C:\photos\p.cr2")]
    public void RemovePaths_UnreadableRepresentativeOfCapture_DegradesToTheReadablePartner(RawPairMode mode, string bad, string survivor)
    {
        var catalog = GroupedCatalog(mode, @"C:\photos\z.jpg");
        catalog.SetCurrent(1);

        var removed = catalog.RemovePaths([bad]);

        Assert.Equal([bad], removed);
        Assert.Equal([survivor, @"C:\photos\z.jpg"], catalog.Paths);
        Assert.Null(catalog.EntriesSnapshot()[0].CaptureGroup);
        Assert.Equal(-1, catalog.IndexOf(bad));
        Assert.Equal(1, catalog.CurrentIndex); // z.jpg stays current
    }

    [Fact]
    public void RemovePaths_UnreadableNonRepresentativeMember_KeepsRepresentativeAndDropsTheGroup()
    {
        var catalog = GroupedCatalog(RawPairMode.PreferJpeg);
        var before = catalog.EntriesSnapshot()[0];

        var removed = catalog.RemovePaths([@"C:\photos\p.cr2"]);

        Assert.Equal([@"C:\photos\p.cr2"], removed);
        Assert.Equal([@"C:\photos\p.jpg"], catalog.Paths);
        Assert.Null(catalog.EntriesSnapshot()[0].CaptureGroup);
        Assert.Equal(before.Path, catalog.EntriesSnapshot()[0].Path);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact]
    public void RemovePaths_CurrentCaptureDegrades_StaysCurrentAtTheSamePosition()
    {
        var catalog = GroupedCatalog(RawPairMode.PreferRaw, @"C:\photos\z.jpg");
        Assert.Equal(0, catalog.CurrentIndex);

        catalog.RemovePaths([@"C:\photos\p.cr2"]);

        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal(@"C:\photos\p.jpg", catalog.Current!.Path);
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void RemovePaths_BothImageMembersOfCapture_RemovesTheEntryAndReportsBoth()
    {
        var catalog = GroupedCatalog(RawPairMode.PreferJpeg, @"C:\photos\z.jpg");

        var removed = catalog.RemovePaths([@"C:\photos\p.cr2", @"C:\photos\p.jpg"]);

        Assert.Equal([@"C:\photos\p.jpg", @"C:\photos\p.cr2"], removed);
        Assert.Equal([@"C:\photos\z.jpg"], catalog.Paths);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact]
    public void RestoreMembers_PartnerStillMissing_KeepsTheIndependentEntryUngrouped()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");
        catalog.RestoreMembers([@"C:\photos\p.jpg"], 1, group);

        var restored = catalog.RestoreMembers([@"C:\photos\p.jpg"], 1, group); // nothing new came back

        Assert.False(restored);
        Assert.Equal(3, catalog.Count);
        Assert.Null(catalog.EntriesSnapshot()[1].CaptureGroup);
    }

    [Fact]
    public void RestoreMembers_ReformKeepsCurrentIndexOnThePhotoAfterTheGroup()
    {
        var catalog = PairCatalog(RawPairMode.PreferJpeg);
        var group = new CaptureGroup(@"C:\photos\p.jpg", @"C:\photos\p.cr2");
        catalog.RestoreMembers([@"C:\photos\p.jpg"], 1, group);
        catalog.SetCurrent(2); // viewing after.jpg

        catalog.RestoreMembers([@"C:\photos\p.cr2"], 1, group);

        Assert.Equal(3, catalog.Count);
        Assert.Equal(2, catalog.CurrentIndex);
        Assert.Equal(@"C:\photos\after.jpg", catalog.Current!.Path);
    }
}
