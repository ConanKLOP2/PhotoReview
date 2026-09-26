using System;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class ReviewCatalogTests
{
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
        Assert.False(catalog.MoveToFront("anything.jpg"));
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

    [Fact]
    public void MoveToFront_BringsItemToHeadAndSelectsIt()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["a.jpg", "b.jpg", "c.jpg"]);

        Assert.False(catalog.MoveToFront("nonexistent.jpg"));

        Assert.True(catalog.MoveToFront("c.jpg"));
        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal("c.jpg", catalog.Current?.Path);
        Assert.Equal(["c.jpg", "a.jpg", "b.jpg"], catalog.Paths);

        // Already at front
        Assert.True(catalog.MoveToFront("c.jpg"));
        Assert.Equal(0, catalog.CurrentIndex);
        Assert.Equal(["c.jpg", "a.jpg", "b.jpg"], catalog.Paths);
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
    public void InsertSorted_MaintainsOrder()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(["10.jpg", "30.jpg"]);

        Comparison<string> comparison = string.CompareOrdinal;

        var idx = catalog.InsertSorted("20.jpg", comparison);
        Assert.Equal(1, idx);
        Assert.Equal(["10.jpg", "20.jpg", "30.jpg"], catalog.Paths);

        // Insert at beginning
        var idxHead = catalog.InsertSorted("05.jpg", comparison);
        Assert.Equal(0, idxHead);
        Assert.Equal(["05.jpg", "10.jpg", "20.jpg", "30.jpg"], catalog.Paths);

        // Insert at end
        var idxTail = catalog.InsertSorted("40.jpg", comparison);
        Assert.Equal(4, idxTail);
        Assert.Equal(["05.jpg", "10.jpg", "20.jpg", "30.jpg", "40.jpg"], catalog.Paths);

        // Insert existing duplicate
        var idxDup = catalog.InsertSorted("20.jpg", comparison);
        Assert.Equal(2, idxDup);
        Assert.Equal(5, catalog.Count);
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
}

