using System.IO;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Table-driven adversarial inputs for <see cref="ExplorerSnapshotValidator"/> and its reason codes.</summary>
[Trait("Category", "HotPath")]
public sealed class ExplorerSnapshotValidatorEdgeCaseTests
{
    private const string Folder = @"C:\photos";

    private static ExplorerViewSnapshot Snapshot(string folder, params string[] ordered) =>
        new(folder, ordered, [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);

    private static (bool Valid, IReadOnlyList<string> Ordered, string? Reason) Run(string folder, string[] ordered, string[] scanned)
    {
        var valid = ExplorerSnapshotValidator.TryValidate(Snapshot(folder, ordered), scanned, out var result, out var reason);
        return (valid, result, reason);
    }

    public static TheoryData<ExplorerOrderStatus, string?, string> NonAvailableStatuses() => new()
    {
        { ExplorerOrderStatus.NoMatchingWindow, null, "NoMatchingWindow" },
        { ExplorerOrderStatus.NativeViewUnavailable, null, "NativeViewUnavailable" },
        { ExplorerOrderStatus.InvalidSnapshot, null, "InvalidSnapshot" },
        { ExplorerOrderStatus.TimedOut, ExplorerReason.Timeout, ExplorerReason.Timeout },
        { ExplorerOrderStatus.Canceled, ExplorerReason.Canceled, ExplorerReason.Canceled },
        { ExplorerOrderStatus.Failed, ExplorerReason.Format(ExplorerReason.QueryFailed, "IOException"), "query-failed|IOException" },
    };

    [Theory]
    [MemberData(nameof(NonAvailableStatuses))]
    public void NonAvailableStatusIsRejectedWithItsReasonOrStatusName(ExplorerOrderStatus status, string? reason, string expected)
    {
        var snapshot = new ExplorerViewSnapshot(Folder, [Folder + @"\a.jpg"], [], ExplorerGroupState.None, status, reason, DateTime.UtcNow);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [Folder + @"\a.jpg"], out var ordered, out var actual);

        Assert.False(valid);
        Assert.Equal(expected, actual);
        Assert.Empty(ordered);
    }

    [Theory]
    [InlineData(@"C:\photos\a.jpg", @"C:\photos\a.jpg", true)]
    [InlineData(@"C:\PHOTOS\A.JPG", @"C:\photos\a.jpg", true)]
    [InlineData(@"C:\photos\.\a.jpg", @"C:\photos\a.jpg", true)]
    [InlineData(@"C:\photos\x\..\a.jpg", @"C:\photos\a.jpg", true)]
    [InlineData(@"C:/photos/a.jpg", @"C:\photos\a.jpg", true)]
    [InlineData(@"\\?\C:\photos\a.jpg", @"C:\photos\a.jpg", true)]
    [InlineData(@"C:\photos\Ảnh đẹp 01.jpg", @"C:\photos\ẢNH ĐẸP 01.jpg", true)]
    public void EquivalentSpellingsOfTheSameFileAreAccepted(string reported, string scanned, bool expected)
    {
        var (valid, ordered, reason) = Run(Folder, [reported], [scanned]);

        Assert.Equal(expected, valid);
        Assert.Null(reason);
        Assert.Equal(Path.GetFullPath(scanned), Assert.Single(ordered), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\photos\sub\a.jpg", "outside-folder")]
    [InlineData(@"C:\photos2\a.jpg", "outside-folder")]
    [InlineData(@"C:\photo\a.jpg", "outside-folder")]
    [InlineData(@"C:\photos\..\other\a.jpg", "outside-folder")]
    [InlineData(@"D:\photos\a.jpg", "outside-folder")]
    [InlineData(@"\\server\share\photos\a.jpg", "outside-folder")]
    [InlineData("C:\\photos\\a\0.jpg", "outside-folder")]
    [InlineData("", "empty-path")]
    [InlineData("   ", "empty-path")]
    public void PathsThatAreNotDirectChildrenAreRejectedWithTheirReasonAndNeverThrow(string candidate, string expectedReason)
    {
        var (valid, ordered, reason) = Run(Folder, [Folder + @"\a.jpg", candidate], [Folder + @"\a.jpg"]);

        Assert.False(valid);
        Assert.Equal(expectedReason, reason);
        Assert.Empty(ordered);
    }

    [Fact]
    public void ScannedFileWithInvalidCharacterIsRejectedInsteadOfThrowing()
    {
        var (valid, _, reason) = Run(Folder, [Folder + @"\a.jpg"], ["C:\\photos\\bad\0.jpg"]);

        Assert.False(valid);
        Assert.Equal(ExplorerReason.OutsideFolder, reason);
    }

    [Fact]
    public void DuplicatesDifferingOnlyByCaseAreRejected()
    {
        var (valid, _, reason) = Run(Folder, [Folder + @"\a.jpg", Folder + @"\A.JPG", Folder + @"\b.jpg"],
            [Folder + @"\a.jpg", Folder + @"\b.jpg"]);

        Assert.False(valid);
        Assert.Equal(ExplorerReason.DuplicateItem, reason);
    }

    [Fact]
    public void DuplicateOfAnUnscannedFileIsStillRejected()
    {
        var (valid, _, reason) = Run(Folder, [Folder + @"\x.txt", Folder + @"\x.txt", Folder + @"\a.jpg"], [Folder + @"\a.jpg"]);

        Assert.False(valid);
        Assert.Equal(ExplorerReason.DuplicateItem, reason);
    }

    [Fact]
    public void UnscannedNonImageEntriesAreIgnoredButOrderOfTheRestIsKept()
    {
        var (valid, ordered, _) = Run(Folder, [Folder + @"\readme.txt", Folder + @"\b.jpg", Folder + @"\notes.doc", Folder + @"\a.jpg"],
            [Folder + @"\a.jpg", Folder + @"\b.jpg"]);

        Assert.True(valid);
        Assert.Equal([Folder + @"\b.jpg", Folder + @"\a.jpg"], ordered);
    }

    [Fact]
    public void ScannedFileMissingFromSnapshotIsIncomplete()
    {
        var (valid, _, reason) = Run(Folder, [Folder + @"\a.jpg", Folder + @"\readme.txt"], [Folder + @"\a.jpg", Folder + @"\b.jpg"]);

        Assert.False(valid);
        Assert.Equal(ExplorerReason.IncompleteSnapshot, reason);
    }

    [Fact]
    public void EmptySnapshotIsValidOnlyForAnEmptyScan()
    {
        Assert.True(Run(Folder, [], []).Valid);
        Assert.Equal(ExplorerReason.IncompleteSnapshot, Run(Folder, [], [Folder + @"\a.jpg"]).Reason);
    }

    [Theory]
    [InlineData(@"C:\photos\")]
    [InlineData(@"c:\PHOTOS")]
    [InlineData(@"C:\photos\.")]
    [InlineData(@"\\?\C:\photos")]
    public void SnapshotFolderSpellingDoesNotChangeTheVerdict(string snapshotFolder)
    {
        var (valid, ordered, _) = Run(snapshotFolder, [Folder + @"\b.jpg", Folder + @"\a.jpg"], [Folder + @"\a.jpg", Folder + @"\b.jpg"]);

        Assert.True(valid);
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void DriveRootFolderIsSupported()
    {
        var (valid, ordered, _) = Run(@"C:\", [@"C:\a.jpg"], [@"C:\a.jpg"]);

        Assert.True(valid);
        Assert.Equal(@"C:\a.jpg", Assert.Single(ordered));
    }

    [Fact]
    public void UncFolderIsSupportedInBothSpellings()
    {
        var (valid, ordered, _) = Run(@"\\srv\share\photos", [@"\\?\UNC\srv\share\photos\a.jpg"], [@"\\srv\share\photos\a.jpg"]);

        Assert.True(valid);
        Assert.Equal(@"\\srv\share\photos\a.jpg", Assert.Single(ordered));
    }

    [Fact]
    public void LongPathsBeyondMaxPathAreSupported()
    {
        var deep = Folder + @"\" + new string('d', 120) + @"\" + new string('e', 120);
        var file = deep + @"\" + new string('f', 60) + ".jpg";

        var (valid, ordered, _) = Run(deep, [@"\\?\" + file], [file]);

        Assert.True(valid);
        Assert.Equal(file, Assert.Single(ordered));
    }

    [Theory]
    [InlineData(@"\\?\C:\a", @"C:\a")]
    [InlineData(@"\\?\UNC\srv\share\a", @"\\srv\share\a")]
    [InlineData(@"C:\a\..\b", @"C:\b")]
    [InlineData(@"\\srv\share\a", @"\\srv\share\a")]
    public void NormalizePathStripsExtendedPrefixOnly(string input, string expected) =>
        Assert.Equal(expected, ExplorerSnapshotValidator.NormalizePath(input));

    [Fact(DisplayName = "Property: any permutation of the scanned files validates to exactly that permutation")]
    public void AnyPermutationValidatesToItself()
    {
        var random = new Random(20260926);
        var files = Enumerable.Range(0, 50).Select(i => $@"C:\photos\IMG_{i:D4} ảnh.jpg").ToArray();
        for (var round = 0; round < 40; round++)
        {
            var permuted = files.OrderBy(_ => random.Next()).ToArray();
            var spelled = permuted.Select(p => random.Next(3) switch { 0 => p.ToUpperInvariant(), 1 => p.Replace('\\', '/'), _ => p }).ToArray();

            var (valid, ordered, reason) = Run(Folder, spelled, files);

            Assert.True(valid, reason);
            Assert.Equal(permuted, ordered, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact(DisplayName = "Property: dropping any one scanned file makes the snapshot incomplete and adding a duplicate makes it invalid")]
    public void AnySingleMutationIsDetected()
    {
        var files = Enumerable.Range(0, 12).Select(i => $@"C:\photos\{i}.jpg").ToArray();
        for (var i = 0; i < files.Length; i++)
        {
            var without = files.Where((_, index) => index != i).ToArray();
            Assert.Equal(ExplorerReason.IncompleteSnapshot, Run(Folder, without, files).Reason);

            var duplicated = files.Append(files[i].ToUpperInvariant()).ToArray();
            Assert.Equal(ExplorerReason.DuplicateItem, Run(Folder, duplicated, files).Reason);
        }
    }

    [Fact(DisplayName = "Property: CanonicalizeFolder is idempotent and blind to case-free trailing separators")]
    public void CanonicalizeFolderIsIdempotent()
    {
        var random = new Random(7);
        var folders = new[] { @"C:\a", @"C:\a\b\", @"C:\a\.\b", @"C:\a\x\..\b", @"\\srv\share\a\", @"C:\", @"\\?\D:\deep\x\" };
        foreach (var folder in folders)
        {
            var once = ExplorerSnapshotValidator.CanonicalizeFolder(folder);
            Assert.Equal(once, ExplorerSnapshotValidator.CanonicalizeFolder(once));
            Assert.True(ExplorerSnapshotValidator.SamePath(folder, once + (once.EndsWith('\\') ? string.Empty : @"\")));
            Assert.True(ExplorerSnapshotValidator.SamePath(folder, random.Next(2) == 0 ? once.ToUpperInvariant() : once.ToLowerInvariant()));
        }
    }
}
