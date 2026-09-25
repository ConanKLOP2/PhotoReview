using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>The Explorer snapshot comes from COM: the validator must answer false (never throw) for anything odd.</summary>
public sealed class ExplorerSnapshotValidatorRobustnessTests
{
    private const string Folder = @"C:\photos";

    private static ExplorerViewSnapshot Snapshot(string folder, IReadOnlyList<string> ordered) =>
        new(folder, ordered, [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);

    [Theory(DisplayName = "Paths the file system rejects make the snapshot invalid instead of throwing")]
    [InlineData("C:\\photos\\a\0.jpg")]
    [InlineData("C:\\photos\\<>|.jpg")]
    [InlineData("\0")]
    public void InvalidCandidatePath_ReturnsFalse(string bad)
    {
        var ok = ExplorerSnapshotValidator.TryValidate(Snapshot(Folder, [bad]), [@"C:\photos\a.jpg"], out var ordered, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
        Assert.Empty(ordered);
    }

    [Fact(DisplayName = "An invalid scanned file path makes the snapshot invalid instead of throwing")]
    public void InvalidScannedPath_ReturnsFalse()
    {
        var ok = ExplorerSnapshotValidator.TryValidate(Snapshot(Folder, [@"C:\photos\a.jpg"]), ["C:\\photos\\a\0.jpg"], out _, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Theory(DisplayName = "A snapshot with a null or empty folder or a null path list is invalid, not an exception")]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(Folder, false)]
    public void NullPieces_ReturnFalse(string? folder, bool listPresent)
    {
        var snapshot = Snapshot(folder!, listPresent ? [@"C:\photos\a.jpg"] : null!);

        var ok = ExplorerSnapshotValidator.TryValidate(snapshot, [@"C:\photos\a.jpg"], out var ordered, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
        Assert.Empty(ordered);
    }

    [Fact(DisplayName = "Case, slash style, trailing separators and dot segments do not stop a matching snapshot from validating")]
    public void EquivalentSpellings_Validate()
    {
        var ok = ExplorerSnapshotValidator.TryValidate(
            Snapshot(@"c:/PHOTOS/", [@"C:\photos\sub\..\B.JPG", "c:/photos/a.jpg"]),
            [@"C:\photos\a.jpg", @"C:\photos\b.jpg"], out var ordered, out _);

        Assert.True(ok);
        Assert.Equal([@"c:\photos\b.jpg", @"c:\photos\a.jpg"], ordered.Select(p => p.ToLowerInvariant()).ToList());
    }

    [Fact(DisplayName = "Extra non-image files in the Explorer view are ignored but keep their relative order")]
    public void ExtraFiles_Ignored()
    {
        var ok = ExplorerSnapshotValidator.TryValidate(
            Snapshot(Folder, [@"C:\photos\z.txt", @"C:\photos\b.jpg", @"C:\photos\notes.docx", @"C:\photos\a.jpg"]),
            [@"C:\photos\a.jpg", @"C:\photos\b.jpg"], out var ordered, out _);

        Assert.True(ok);
        Assert.Equal([@"C:\photos\b.jpg", @"C:\photos\a.jpg"], ordered);
    }

    [Theory(DisplayName = "Fuzz: a permutation validates and keeps the snapshot order; any drop, duplicate or foreign file is rejected")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Fuzz_PermutationOracle(int seed)
    {
        var r = new Random(seed);
        for (var round = 0; round < 300; round++)
        {
            var scanned = Enumerable.Range(0, r.Next(1, 25)).Select(i => Folder + @"\img" + i + (r.Next(2) == 0 ? ".jpg" : ".png")).ToList();
            var order = scanned.OrderBy(_ => r.Next()).Select(p => r.Next(3) == 0 ? p.ToUpperInvariant() : p).ToList();

            Assert.True(ExplorerSnapshotValidator.TryValidate(Snapshot(Folder, order), scanned, out var ordered, out _));
            Assert.Equal(order.Select(p => p.ToUpperInvariant()), ordered.Select(p => p.ToUpperInvariant()));

            var broken = order.ToList();
            switch (r.Next(4))
            {
                case 0: broken.RemoveAt(r.Next(broken.Count)); break;
                case 1: broken.Add(broken[r.Next(broken.Count)]); break;
                case 2: broken.Add(@"C:\elsewhere\x.jpg"); break;
                default: broken[r.Next(broken.Count)] = @"C:\photos\sub\deep.jpg"; break;
            }

            Assert.False(ExplorerSnapshotValidator.TryValidate(Snapshot(Folder, broken), scanned, out var none, out var reason));
            Assert.Empty(none);
            Assert.NotNull(reason);
        }
    }

    [Fact(DisplayName = "ReviewCatalog.ReplaceOrder accepts exactly what the validator returns for a catalog with the same files")]
    public void ValidatorOutput_IsAcceptedByCatalog()
    {
        var scanned = Enumerable.Range(0, 30).Select(i => Folder + @"\p" + i + ".jpg").ToList();
        var catalog = new ReviewCatalog();
        catalog.Reset(scanned);
        var order = scanned.AsEnumerable().Reverse().Select(p => p.ToUpperInvariant()).ToList();

        Assert.True(ExplorerSnapshotValidator.TryValidate(Snapshot(Folder, order), scanned, out var ordered, out _));

        Assert.True(catalog.ReplaceOrder(ordered));
        Assert.Equal(scanned.AsEnumerable().Reverse(), catalog.Paths);
    }
}
