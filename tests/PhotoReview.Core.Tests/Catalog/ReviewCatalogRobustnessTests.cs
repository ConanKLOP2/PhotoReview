using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Invariants and exception safety of <see cref="ReviewCatalog"/> beyond the happy-path tests.</summary>
public sealed class ReviewCatalogRobustnessTests
{
    private static IEnumerable<string> ThrowsAfter(int count)
    {
        for (var i = 0; i < count; i++) yield return @"C:\new\n" + i + ".jpg";
        throw new InvalidOperationException("enumeration failed");
    }

    [Fact(DisplayName = "Reset(paths) whose enumeration throws leaves the previous catalog fully intact")]
    public void Reset_PathsEnumerationThrows_KeepsPreviousState()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\b.jpg", @"C:\c.jpg"]);
        Assert.Equal(1, catalog.IndexOf(@"C:\b.jpg")); // builds the lookup index
        catalog.SetCurrent(2);
        var version = catalog.StructuralVersion;

        Assert.Throws<InvalidOperationException>(() => catalog.Reset(ThrowsAfter(2)));

        Assert.Equal(3, catalog.Count);
        Assert.Equal(2, catalog.CurrentIndex);
        Assert.Equal(@"C:\c.jpg", catalog.Current?.Path);
        Assert.Equal(1, catalog.IndexOf(@"C:\b.jpg"));
        Assert.Equal(-1, catalog.IndexOf(@"C:\new\n0.jpg"));
        Assert.Equal(version, catalog.StructuralVersion);
    }

    [Fact(DisplayName = "Reset(entries) whose enumeration throws leaves the previous catalog fully intact")]
    public void Reset_EntriesEnumerationThrows_KeepsPreviousState()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\b.jpg"]);
        Assert.Equal(0, catalog.IndexOf(@"C:\a.jpg"));

        IEnumerable<CatalogEntry> Throwing()
        {
            yield return new CatalogEntry(@"C:\x.jpg");
            throw new InvalidOperationException("boom");
        }

        Assert.Throws<InvalidOperationException>(() => catalog.Reset(Throwing()));

        Assert.Equal(2, catalog.Count);
        Assert.Equal(0, catalog.IndexOf(@"C:\a.jpg"));
        Assert.Equal(-1, catalog.IndexOf(@"C:\x.jpg"));
    }

    [Fact(DisplayName = "Reset(catalog.Entries) (a live view of itself) keeps the entries")]
    public void Reset_WithOwnLiveEntriesView_KeepsEntries()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\b.jpg"]);

        catalog.Reset(catalog.Entries);

        Assert.Equal([@"C:\a.jpg", @"C:\b.jpg"], catalog.Paths);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact(DisplayName = "Reset skips null/blank paths and null entries")]
    public void Reset_SkipsNullAndBlank()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(new string?[] { null, "", "  ", @"C:\a.jpg" }!);
        Assert.Equal([@"C:\a.jpg"], catalog.Paths);

        catalog.Reset(new CatalogEntry?[] { null, new CatalogEntry(@"C:\b.jpg") }!);
        Assert.Equal([@"C:\b.jpg"], catalog.Paths);
    }

    [Theory(DisplayName = "ReplaceOrder rejects non-permutations and leaves the catalog untouched")]
    [InlineData(@"C:\a.jpg", @"C:\a.jpg", @"C:\c.jpg")]   // duplicate, right count
    [InlineData(@"C:\a.jpg", @"C:\b.jpg", @"C:\x.jpg")]   // unknown member
    [InlineData(@"C:\A.JPG", @"C:\a.jpg", @"C:\c.jpg")]   // case-variant duplicate
    public void ReplaceOrder_NotAPermutation_ReturnsFalseAndKeepsOrder(string p0, string p1, string p2)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\b.jpg", @"C:\c.jpg"]);
        catalog.SetCurrent(1);
        var version = catalog.StructuralVersion;

        Assert.False(catalog.ReplaceOrder([p0, p1, p2]));

        Assert.Equal([@"C:\a.jpg", @"C:\b.jpg", @"C:\c.jpg"], catalog.Paths);
        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal(version, catalog.StructuralVersion);
    }

    [Fact(DisplayName = "ReplaceOrder with a null element returns false instead of throwing")]
    public void ReplaceOrder_NullElement_ReturnsFalse()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\b.jpg"]);

        Assert.False(catalog.ReplaceOrder([@"C:\a.jpg", null!]));
    }

    [Fact(DisplayName = "ReplaceOrder on a catalog holding case-variant duplicates does not throw")]
    public void ReplaceOrder_CatalogWithCaseVariantDuplicates_DoesNotThrow()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\A.JPG"]);

        var ex = Record.Exception(() => catalog.ReplaceOrder([@"C:\A.JPG", @"C:\a.jpg"]));

        Assert.Null(ex);
        Assert.Equal(2, catalog.Count);
    }

    [Fact(DisplayName = "ReplaceOrder keeps the current photo selected by path")]
    public void ReplaceOrder_KeepsCurrentByPath()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg", @"C:\b.jpg", @"C:\c.jpg"]);
        catalog.SetCurrent(2);

        Assert.True(catalog.ReplaceOrder([@"C:\c.jpg", @"C:\b.jpg", @"C:\a.jpg"]));

        Assert.Equal(@"C:\c.jpg", catalog.Current?.Path);
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact(DisplayName = "Restore and InsertSorted with a blank path are rejected without changing anything")]
    public void RestoreAndInsertSorted_BlankPath_Rejected()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\a.jpg"]);
        var version = catalog.StructuralVersion;

        Assert.False(catalog.Restore(" ", 0));
        Assert.Throws<ArgumentException>(() => catalog.InsertSorted("", StringComparer.Ordinal.Compare));

        Assert.Equal(1, catalog.Count);
        Assert.Equal(version, catalog.StructuralVersion);
    }

    /// <summary>Straightforward list model of the catalog used as the oracle.</summary>
    private sealed class Model
    {
        public List<string> Paths = [];
        public int Current = -1;

        public int IndexOf(string p) => Paths.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));

        public void Reset(IEnumerable<string> p) { Paths = [.. p]; Current = Paths.Count > 0 ? 0 : -1; }

        public void MoveToFront(string p)
        {
            var i = IndexOf(p);
            if (i < 0) return;
            var v = Paths[i]; Paths.RemoveAt(i); Paths.Insert(0, v); Current = 0;
        }

        public void Remove(string p)
        {
            var i = IndexOf(p);
            if (i < 0) return;
            Paths.RemoveAt(i);
            Current = Paths.Count == 0 ? -1 : Math.Min(i, Paths.Count - 1);
        }

        public void Restore(string p, int index)
        {
            if (IndexOf(p) >= 0) return;
            var at = Math.Clamp(index, 0, Paths.Count);
            Paths.Insert(at, p);
            if (Current == -1) Current = at; else if (at <= Current) Current++;
        }

        public void ReplaceOrder(List<string> order)
        {
            var cur = Current >= 0 ? Paths[Current] : null;
            Paths = order;
            Current = cur is null ? (Paths.Count > 0 ? 0 : -1) : Paths.FindIndex(x => string.Equals(x, cur, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory(DisplayName = "Randomized operation sequences match a reference list model and keep index/version invariants")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RandomOperations_MatchReferenceModel(int seed)
    {
        var rng = new Random(seed);
        var catalog = new ReviewCatalog();
        var model = new Model();
        string P(int n) => (rng.Next(2) == 0 ? @"C:\Photos\img" : @"C:\PHOTOS\IMG") + n + ".jpg";
        var universe = Enumerable.Range(0, 12).ToArray();

        for (var step = 0; step < 400; step++)
        {
            var before = catalog.StructuralVersion;
            switch (rng.Next(6))
            {
                case 0:
                {
                    var set = universe.OrderBy(_ => rng.Next()).Take(rng.Next(0, 9)).Select(n => @"C:\Photos\img" + n + ".jpg").ToList();
                    catalog.Reset(set); model.Reset(set);
                    break;
                }
                case 1:
                {
                    var p = P(rng.Next(12)); catalog.MoveToFront(p); model.MoveToFront(p);
                    break;
                }
                case 2:
                {
                    var p = P(rng.Next(12)); catalog.Remove(p); model.Remove(p);
                    break;
                }
                case 3:
                {
                    var p = P(rng.Next(12)); var i = rng.Next(-2, 14); catalog.Restore(p, i); model.Restore(p, i);
                    break;
                }
                case 4:
                {
                    var order = model.Paths.OrderBy(_ => rng.Next()).ToList();
                    // Sometimes present the same set in a different case.
                    if (rng.Next(3) == 0 && order.Count > 0) order[0] = order[0].ToUpperInvariant();
                    Assert.True(catalog.ReplaceOrder(order));
                    model.ReplaceOrder(order);
                    break;
                }
                default:
                {
                    if (model.Paths.Count > 0)
                    {
                        var i = rng.Next(model.Paths.Count); Assert.True(catalog.SetCurrent(i)); model.Current = i;
                    }
                    break;
                }
            }

            Assert.Equal(model.Paths.Select(p => p.ToLowerInvariant()), catalog.Paths.Select(p => p.ToLowerInvariant()));
            Assert.Equal(model.Current, catalog.CurrentIndex);
            Assert.Equal(model.Paths.Count == 0, catalog.Current is null);
            Assert.True(catalog.StructuralVersion >= before);
            for (var i = 0; i < catalog.Count; i++)
            {
                Assert.Equal(i, catalog.IndexOf(catalog.PathAt(i)));
                Assert.Equal(i, catalog.IndexOf(catalog.PathAt(i).ToUpperInvariant()));
            }
        }
    }
}
