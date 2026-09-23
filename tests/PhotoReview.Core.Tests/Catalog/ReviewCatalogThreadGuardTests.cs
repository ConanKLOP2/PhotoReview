using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>
/// AR04 / ADR 0005: ReviewCatalog's owner-thread guard is [Conditional("DEBUG")] and a no-op
/// until <see cref="ReviewCatalog.BindToCurrentThread"/> is called.
/// </summary>
public class ReviewCatalogThreadGuardTests
{
    private static readonly string[] Paths = [@"C:\p\a.jpg", @"C:\p\b.jpg", @"C:\p\c.jpg"];

    [Fact(DisplayName = "Unbound catalog: mutators work from any thread (unit tests unaffected)")]
    public async Task Unbound_MutatedFromOtherThread_DoesNotThrow()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(Paths);

        await Task.Run(() =>
        {
            catalog.SetCurrent(1);
            catalog.Remove(Paths[0]);
            catalog.Reset(Paths);
        });

        Assert.Equal(3, catalog.Count);
    }

    [Fact(DisplayName = "Bound catalog: mutators on the owner thread work")]
    public void Bound_MutatedOnOwnerThread_DoesNotThrow()
    {
        var catalog = new ReviewCatalog();
        catalog.BindToCurrentThread();

        catalog.Reset(Paths);
        Assert.True(catalog.SetCurrent(2));
        Assert.True(catalog.ReplaceOrder([Paths[2], Paths[1], Paths[0]]));
        Assert.True(catalog.UpdateMetadata(Paths[1], 10, DateTime.UtcNow));
        Assert.True(catalog.MoveToFront(Paths[1]));
        Assert.Equal(1, catalog.Remove(Paths[2]));
        Assert.True(catalog.Restore(Paths[2], 0));
        Assert.Equal(3, catalog.Count);
    }

    [Fact(DisplayName = "Bound catalog: a mutator on another thread throws in Debug, is unchecked in Release")]
    public void Bound_MutatedFromOtherThread_ThrowsOnlyInDebug()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(Paths);
        catalog.BindToCurrentThread();

        Exception? observed = null;
        var thread = new Thread(() =>
        {
            try { catalog.SetCurrent(1); }
            catch (Exception ex) { observed = ex; }
        });
        thread.Start();
        thread.Join();

#if DEBUG
        var ex = Assert.IsType<InvalidOperationException>(observed);
        Assert.Contains("SetCurrent", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, catalog.CurrentIndex);
#else
        Assert.Null(observed);
        Assert.Equal(1, catalog.CurrentIndex);
#endif
    }

    [Fact(DisplayName = "Reads stay unchecked on a bound catalog (preload snapshot runs off the UI thread)")]
    public async Task Bound_ReadFromOtherThread_DoesNotThrow()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(Paths);
        catalog.BindToCurrentThread();

        var snapshot = await Task.Run(() => (catalog.EntriesSnapshot().Length, catalog.IndexOf(Paths[1])));

        Assert.Equal((3, 1), snapshot);
    }
}
