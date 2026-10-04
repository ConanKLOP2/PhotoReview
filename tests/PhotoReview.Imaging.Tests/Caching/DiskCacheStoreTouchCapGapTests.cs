using System.IO;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>Mutation-gap test: the access-touch throttle table is cleared when it already holds exactly its cap (8192), not one entry later.</summary>
[Trait("Category", "HotPath")]
public sealed class DiskCacheStoreTouchCapGapTests : IDisposable
{
    private static readonly DateTime T0 = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Sentinel = new(2010, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    private readonly TempRoot _root = new("DiskCacheTouchCap");

    public void Dispose() => _root.Dispose();

    private static bool Touched(string path, DateTime expected) =>
        Math.Abs((File.GetLastAccessTimeUtc(path) - expected).TotalSeconds) < 5;

    [Fact(DisplayName = "With exactly 8192 tracked entries the next new entry clears the throttle table")]
    public void NoteAccessed_ClearsWhenTheTableHoldsExactlyTheCap()
    {
        var dir = _root.Dir("cap-exact");
        var a = Path.Combine(dir, "a.png");
        File.WriteAllBytes(a, new byte[10]);
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1000, utcNow: () => T0);

        store.NoteAccessed(a);                               // tracked: 1
        for (var i = 0; i < 8191; i++) store.NoteAccessed(Path.Combine(dir, $"f{i}.png")); // missing files: tracked: 8192
        File.SetLastAccessTimeUtc(a, Sentinel);
        store.NoteAccessed(a);                               // still throttled (count 8192, no new entry)
        Assert.True(Touched(a, Sentinel), "a throttled repeat must not touch the file");

        store.NoteAccessed(Path.Combine(dir, "overflow.png")); // the table holds exactly its cap: cleared before this one is added
        store.NoteAccessed(a);

        Assert.True(Touched(a, T0), "the table was not cleared when it held exactly its cap");
    }
}
