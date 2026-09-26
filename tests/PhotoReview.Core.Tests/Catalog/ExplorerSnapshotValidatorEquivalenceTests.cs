using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>The optimised validator must give the same verdict, reason and order as the original for mutated snapshots.</summary>
public sealed class ExplorerSnapshotValidatorEquivalenceTests
{
    private static ExplorerViewSnapshot Snap(string folder, IReadOnlyList<string> paths) =>
        new(folder, paths, [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);

    [Fact(DisplayName = "Random snapshot mutations validate exactly like the original implementation")]
    public void MatchesOriginal_OnMutatedSnapshots()
    {
        const string folder = @"C:\Photos\Set A";
        string[] scanned = [.. Enumerable.Range(0, 8).Select(i => $@"{folder}\img{i}.jpg")];
        string[] folderSpellings = [folder, folder + @"\", folder.ToUpperInvariant(), @"\?\" + folder, @"C:\Photos\Set A\..\Set A", @"C:\Photos"];
        var r = new Random(7);

        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var order = scanned.OrderBy(_ => r.Next()).ToList();
            switch (r.Next(9))
            {
                case 1: order.RemoveAt(r.Next(order.Count)); break;                                   // incomplete
                case 2: order.Add(order[r.Next(order.Count)]); break;                                 // duplicate
                case 3: order.Add($@"{folder}\extra.jpg"); break;                                     // unknown member
                case 4: order[r.Next(order.Count)] = $@"C:\Other\img{r.Next(8)}.jpg"; break;          // outside folder
                case 5: order[r.Next(order.Count)] = order[0].ToUpperInvariant(); break;              // case variant
                case 6: order[r.Next(order.Count)] = @"\?\" + order[0]; break;                       // extended prefix
                case 7: order[r.Next(order.Count)] = ""; break;                                       // empty
                case 8: order[r.Next(order.Count)] = $@"{folder}\sub\..\img{r.Next(8)}.jpg"; break;   // dot segments
            }

            var snapshot = Snap(folderSpellings[r.Next(folderSpellings.Length)], order);
            var expectedOk = ExplorerSnapshotValidatorReference.TryValidate(snapshot, scanned, out var expectedOrder, out var expectedReason);
            var actualOk = ExplorerSnapshotValidator.TryValidate(snapshot, scanned, out var actualOrder, out var actualReason);

            Assert.Equal(expectedOk, actualOk);
            Assert.Equal(expectedReason, actualReason);
            Assert.Equal(expectedOrder, actualOrder);
        }
    }
}
