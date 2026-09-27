using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public sealed class ReviewCatalogRemoveIndexTests
{
    private static string P(int i) => $@"C:\photos\img{i:D5}.jpg";

    private static ReviewCatalog Build(int count)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(Enumerable.Range(0, count).Select(P));
        return catalog;
    }

    private static void AssertMatchesModel(ReviewCatalog catalog, List<string> model)
    {
        Assert.Equal(model.Count, catalog.Count);
        foreach (var (path, i) in model.Select((p, i) => (p, i)))
            Assert.Equal(i, catalog.IndexOf(path.ToUpperInvariant()));
    }

    [Fact(DisplayName = "A burst of Removes does not rebuild the path index each time (hot review loop)")]
    public void Remove_Burst_DoesNotRebuildIndexPerRemoval()
    {
        var catalog = Build(500);
        Assert.Equal(0, catalog.IndexOf(P(0)));
        var rebuilds = catalog.IndexRebuildCountForTests;

        for (var i = 0; i < 30; i++)
        {
            catalog.Remove(catalog.PathAt(100));
            Assert.True(catalog.IndexOf(catalog.PathAt(100)) == 100); // the lookup ImagePresenter does for the next photo
        }

        Assert.Equal(rebuilds, catalog.IndexRebuildCountForTests);
    }

    [Fact(DisplayName = "Index stays correct through removals, Restore, InsertSorted, MoveToFront and past the rebuild threshold (model check)")]
    public void Index_StaysCorrect_AgainstListModel()
    {
        var catalog = Build(300);
        var model = Enumerable.Range(0, 300).Select(P).ToList();
        var rng = new Random(12345);
        var removed = new List<string>();

        for (var step = 0; step < 600; step++)
        {
            switch (rng.Next(6))
            {
                case 0 or 1 or 2 when model.Count > 0:
                    var victim = model[rng.Next(model.Count)];
                    catalog.Remove(victim.ToUpperInvariant());
                    model.Remove(victim);
                    removed.Add(victim);
                    Assert.Equal(-1, catalog.IndexOf(victim));
                    break;
                case 3 when removed.Count > 0:
                    var back = removed[^1];
                    removed.RemoveAt(removed.Count - 1);
                    var at = rng.Next(model.Count + 1);
                    Assert.True(catalog.Restore(back, at));
                    model.Insert(at, back);
                    break;
                case 4 when model.Count > 1:
                    var front = model[rng.Next(model.Count)];
                    Assert.True(catalog.MoveToFront(front));
                    model.Remove(front);
                    model.Insert(0, front);
                    break;
                case 5 when removed.Count > 0:
                    var ins = removed[0];
                    removed.RemoveAt(0);
                    var idx = catalog.InsertSorted(ins, StringComparer.OrdinalIgnoreCase.Compare);
                    Assert.InRange(idx, 0, model.Count);
                    model.Insert(idx, ins);
                    break;
            }
            if (step % 25 == 0) AssertMatchesModel(catalog, model);
        }

        AssertMatchesModel(catalog, model);
        Assert.Equal(model, catalog.Paths);
    }

    [Fact(DisplayName = "Removing more entries than the threshold still gives correct lookups")]
    public void Remove_PastThreshold_StillCorrect()
    {
        var catalog = Build(400);
        var model = Enumerable.Range(0, 400).Select(P).ToList();
        _ = catalog.IndexOf(P(0));

        for (var i = 0; i < 150; i++)
        {
            var victim = model[(i * 7) % model.Count];
            catalog.Remove(victim);
            model.Remove(victim);
        }

        AssertMatchesModel(catalog, model);
    }
}
