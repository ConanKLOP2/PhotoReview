using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class DuplicateFinderSkipsUnremovableGroupsTests
{
    private readonly InMemoryFileSystem _fs = new();

    [Theory(DisplayName = "A same-size group with no member matching the naming rule is not hashed (no wasted full reads)")]
    [InlineData(true, @"C:\p\a.raw", @"C:\p\b.raw", @"C:\p\c (1).jpg", @"C:\p\d (1).jpg")]
    [InlineData(false, @"C:\p\a (1).raw", @"C:\p\b (1).raw", @"C:\p\c.jpg", @"C:\p\d.jpg")]
    public async Task FindAsync_GroupWithoutMatchingMember_IsNeverHashed(bool removeNumbered, string skipA, string skipB, string keepA, string keepB)
    {
        _fs.WriteAllTextAtomic(skipA, "1234567890"); // 10 bytes, none of the pair matches the rule
        _fs.WriteAllTextAtomic(skipB, "abcdefghij");
        _fs.WriteAllTextAtomic(keepA, "xy");          // 2 bytes, both match the rule
        _fs.WriteAllTextAtomic(keepB, "xy");
        var hashed = new List<string>();

        var result = await DuplicateFinder.FindAsync(
            [skipA, skipB, keepA, keepB],
            removeNumbered,
            (path, _) => { hashed.Add(path); return Task.FromResult("same"); },
            _fs);

        Assert.DoesNotContain(skipA, hashed);
        Assert.DoesNotContain(skipB, hashed);
        Assert.Equal(2, hashed.Count);
        Assert.Single(result); // the pair matches entirely, so the first one is kept as survivor
    }
}
