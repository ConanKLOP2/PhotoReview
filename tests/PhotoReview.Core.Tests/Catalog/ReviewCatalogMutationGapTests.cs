using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Mutation-testing gaps in <see cref="ReviewCatalog"/>: position bookkeeping, duplicate handling, index rebuild bound, group restore/degrade.</summary>
public sealed class ReviewCatalogMutationGapTests
{
    private const string Dir = @"C:\p\";

    private static string P(string name) => Dir + name;

    private static ReviewCatalog Pairing(params string[] names)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(names.Select(n => new CatalogEntry(P(n))), RawPairMode.PreferJpeg);
        return catalog;
    }

    private static ReviewCatalog Plain(params string[] names)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(names.Select(P));
        return catalog;
    }

    // --- MoveToFront -------------------------------------------------------------------------------------------------

    [Fact]
    public void MoveToFront_ItemAlreadyAtTheFront_ChangesNothingStructural()
    {
        var catalog = Plain("a.jpg", "b.jpg");
        var version = catalog.StructuralVersion;

        Assert.True(catalog.MoveToFront(P("a.jpg")));

        Assert.Equal(version, catalog.StructuralVersion);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact]
    public void MoveToFront_ItemInTheMiddle_MovesItAndBumpsTheVersion()
    {
        var catalog = Plain("a.jpg", "b.jpg", "c.jpg");
        var version = catalog.StructuralVersion;

        Assert.True(catalog.MoveToFront(P("b.jpg")));

        Assert.NotEqual(version, catalog.StructuralVersion);
        Assert.Equal([P("b.jpg"), P("a.jpg"), P("c.jpg")], catalog.Paths);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    // --- Remove ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Remove_EntryWhoseMemberPathIsAlsoAnotherEntry_KeepsFindingTheSurvivorByThatPath()
    {
        var group = new CaptureGroup(P("A.jpg"), P("A.cr2"));
        var catalog = new ReviewCatalog();
        // The RAW member was restored on its own before the group entry that also names it: two entries answer to A.cr2.
        catalog.Reset([new CatalogEntry(P("A.cr2")), new CatalogEntry(P("A.jpg")) { CaptureGroup = group }]);
        Assert.Equal(0, catalog.IndexOf(P("A.cr2")));

        catalog.Remove(P("A.cr2"));

        Assert.Equal(1, catalog.Count);
        Assert.Equal(0, catalog.IndexOf(P("A.cr2"))); // the group entry now answers to its member path
        Assert.Same(group, catalog.Find(P("A.cr2"))!.CaptureGroup);
    }

    [Fact]
    public void Remove_AfterTheMaximumNumberOfIncrementalRemovals_ForcesAnIndexRebuild()
    {
        var names = Enumerable.Range(0, 200).Select(i => $"p{i:D3}.jpg").ToArray();
        var catalog = Plain(names);
        Assert.Equal(0, catalog.IndexOf(P(names[0]))); // builds the index once
        var baseline = catalog.IndexRebuildCountForTests;

        for (var i = 0; i < 64; i++) catalog.Remove(P(names[i]));
        Assert.Equal(baseline, catalog.IndexRebuildCountForTests); // 64 removals are tracked incrementally

        catalog.Remove(P(names[64])); // the 65th exceeds the bound: the next lookup rebuilds
        Assert.Equal(135 - 1, catalog.IndexOf(P(names[199])));
        Assert.Equal(baseline + 1, catalog.IndexRebuildCountForTests);
    }

    // --- RestoreMembers ----------------------------------------------------------------------------------------------

    [Fact]
    public void RestoreMembers_PathAlreadyListedAtIndexZero_IsNotAddedAgain()
    {
        var catalog = Plain("a.jpg", "b.jpg");

        var restored = catalog.RestoreMembers([P("a.jpg")], 1);

        Assert.False(restored);
        Assert.Equal([P("a.jpg"), P("b.jpg")], catalog.Paths);
    }

    [Theory]
    [InlineData(0, 1)] // inserted before the current photo: the index follows it
    [InlineData(1, 0)] // inserted after it: the index is unchanged
    public void RestoreMembers_CurrentPhotoKeepsBeingTheDisplayedOne(int insertAt, int expectedCurrent)
    {
        var catalog = Plain("a.jpg", "b.jpg");
        Assert.Equal(0, catalog.CurrentIndex);

        Assert.True(catalog.RestoreMembers([P("c.jpg")], insertAt));

        Assert.Equal(expectedCurrent, catalog.CurrentIndex);
        Assert.Equal(P("a.jpg"), catalog.Current!.Path);
    }

    [Fact]
    public void RestoreMembers_IntoAnEmptyCatalog_SelectsTheRestoredEntry()
    {
        var catalog = new ReviewCatalog();

        Assert.True(catalog.RestoreMembers([P("c.jpg")], 0));

        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal(P("c.jpg"), catalog.Current!.Path);
    }

    [Fact]
    public void RestoreMembers_SecondMemberReturnsWhileTheFirstIsListedAtIndexZero_FoldsThemIntoOneGroupedEntry()
    {
        var catalog = Pairing("a.jpg");
        var group = new CaptureGroup(P("a.jpg"), P("a.cr2"));

        var restored = catalog.RestoreMembers([P("a.cr2")], 1, group);

        Assert.True(restored);
        var entry = Assert.Single(catalog.Entries);
        Assert.Same(group, entry.CaptureGroup);
        Assert.Equal(P("a.jpg"), entry.Path);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Theory]
    [InlineData(0, 0, "x.jpg", false)] // before the folded entries: unchanged
    [InlineData(4, 3, "z.jpg", false)] // after them: shifts down by the removed entry
    [InlineData(3, 1, "a.cr2", true)]  // on the removed duplicate: moves to the surviving grouped entry, keeping the shown member
    [InlineData(1, 1, "a.jpg", true)]  // on the surviving slot: unchanged
    public void RestoreMembers_BothMembersListedSeparately_FoldsThemAndKeepsTheCurrentPhoto(int startCurrent, int expectedCurrent, string expectedPath, bool grouped)
    {
        var catalog = Pairing("x.jpg", "a.jpg", "y.jpg", "z.jpg");
        Assert.True(catalog.RestoreMembers([P("a.cr2")], 3)); // pairing mode, no group given: the RAW comes back as its own entry
        Assert.Equal([P("x.jpg"), P("a.jpg"), P("y.jpg"), P("a.cr2"), P("z.jpg")], catalog.Paths);
        Assert.True(catalog.SetCurrent(startCurrent));
        var group = new CaptureGroup(P("a.jpg"), P("a.cr2"));

        var restored = catalog.RestoreMembers([], 0, group);

        Assert.True(restored);
        Assert.Equal(4, catalog.Count);
        Assert.Equal(expectedCurrent, catalog.CurrentIndex);
        Assert.Equal(P(expectedPath), catalog.Current!.Path);
        Assert.Equal(grouped, catalog.Current.CaptureGroup is not null);
    }

    // --- ReplaceOrder ------------------------------------------------------------------------------------------------

    [Fact]
    public void ReplaceOrder_MemberOrderNamingAnUnknownPath_IsRejectedWithoutThrowing()
    {
        var catalog = Pairing("a.jpg", "a.cr2", "b.jpg"); // [a.jpg (group a.jpg+a.cr2), b.jpg]: three member paths

        var accepted = catalog.ReplaceOrder([P("a.jpg"), P("a.cr2"), P("zzz.jpg")]);

        Assert.False(accepted);
        Assert.Equal([P("a.jpg"), P("b.jpg")], catalog.Paths);
    }

    [Fact]
    public void ReplaceOrder_MemberOrderRepeatingAMember_IsRejected()
    {
        var catalog = Pairing("a.jpg", "a.cr2", "b.jpg");

        var accepted = catalog.ReplaceOrder([P("a.jpg"), P("a.jpg"), P("b.jpg")]);

        Assert.False(accepted);
        Assert.Equal([P("a.jpg"), P("b.jpg")], catalog.Paths);
    }

    [Fact]
    public void ReplaceOrder_ValidMemberOrder_ReordersTheGroupedEntries()
    {
        var catalog = Pairing("a.jpg", "a.cr2", "b.jpg");

        var accepted = catalog.ReplaceOrder([P("b.jpg"), P("a.cr2"), P("a.jpg")]);

        Assert.True(accepted);
        Assert.Equal([P("b.jpg"), P("a.jpg")], catalog.Paths);
    }

    // --- RemovePaths -------------------------------------------------------------------------------------------------

    [Fact]
    public void RemovePaths_UnrelatedEntryRemoved_LeavesAnUntouchedCaptureGroupIntact()
    {
        var catalog = Pairing("a.jpg", "a.cr2", "b.jpg", "c.jpg");

        var removed = catalog.RemovePaths([P("c.jpg")]);

        Assert.Equal([P("c.jpg")], removed);
        Assert.Equal([P("a.jpg"), P("b.jpg")], catalog.Paths);
        Assert.NotNull(catalog.Entries[0].CaptureGroup);
    }

    // --- InsertSorted ------------------------------------------------------------------------------------------------

    [Fact]
    public void InsertSorted_PathAlreadyAtIndexZero_ReturnsItsIndexWithoutAddingADuplicate()
    {
        var catalog = Plain("a.jpg", "b.jpg");

        var index = catalog.InsertSorted(P("a.jpg"), string.CompareOrdinal);

        Assert.Equal(0, index);
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void InsertSorted_ComparisonThatTiesWithAnExistingEntry_InsertsBeforeIt()
    {
        var catalog = Plain("aa.jpg", "bb.jpg");
        static int ByLength(string x, string y) => x.Length.CompareTo(y.Length);

        var index = catalog.InsertSorted(P("cc.jpg"), ByLength);

        Assert.Equal(0, index);
        Assert.Equal([P("cc.jpg"), P("aa.jpg"), P("bb.jpg")], catalog.Paths);
    }

    [Fact]
    public void InsertSorted_IntoAnEmptyCatalog_SelectsTheNewEntry()
    {
        var catalog = new ReviewCatalog();

        Assert.Equal(0, catalog.InsertSorted(P("a.jpg"), string.CompareOrdinal));

        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal(P("a.jpg"), catalog.Current!.Path);
    }

    [Theory]
    [InlineData("a.jpg", 0, 2)] // before the current photo
    [InlineData("c.jpg", 1, 2)] // exactly at the current photo's slot: the photo moves down, it stays displayed
    [InlineData("e.jpg", 2, 1)] // after it: nothing moves
    public void InsertSorted_KeepsTheCurrentPhotoDisplayed(string inserted, int expectedIndex, int expectedCurrent)
    {
        var catalog = Plain("b.jpg", "d.jpg");
        Assert.True(catalog.SetCurrent(1));

        var index = catalog.InsertSorted(P(inserted), string.CompareOrdinal);

        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedCurrent, catalog.CurrentIndex);
        Assert.Equal(P("d.jpg"), catalog.Current!.Path);
    }
}