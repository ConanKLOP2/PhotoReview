using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gap fillers (Stryker, 2026-10-03) for the small file-action helpers: <see cref="DuplicateFinder"/>
/// (what is hashed, zero-byte files, unreadable paths), <see cref="ActionDestinationPolicy.Validate"/> (an invalid first
/// character) and the <see cref="FileFingerprint"/> tolerance boundary.
/// </summary>
public sealed class FileActionsSmallMutationGapTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    // ---- DuplicateFinder ----

    [Fact]
    public async Task FindAsync_TwoEmptyFiles_AreDuplicatesOfEachOther()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\img.jpg", "", Stamp);
        fs.AddFile(@"C:\photos\img (1).jpg", "", Stamp);

        var result = await DuplicateFinder.FindAsync(
            [@"C:\photos\img.jpg", @"C:\photos\img (1).jpg"], removeNumbered: true, (_, _) => Task.FromResult("same"), fs);

        Assert.Equal([@"C:\photos\img (1).jpg"], result); // identical (empty) content is still identical content
    }

    [Fact]
    public async Task FindAsync_FilesWithNoSameSizePartner_AreNeverHashed()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a (1).jpg", "123", Stamp);
        fs.AddFile(@"C:\photos\b (1).jpg", "12345", Stamp);
        var hashed = new List<string>();

        var result = await DuplicateFinder.FindAsync(
            [@"C:\photos\a (1).jpg", @"C:\photos\b (1).jpg"], removeNumbered: true,
            (path, _) =>
            {
                hashed.Add(path);
                return Task.FromResult("same");
            }, fs);

        Assert.Empty(result);
        Assert.Empty(hashed); // a file alone in its size group cannot be a duplicate: reading it would be wasted disk I/O
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("argument")]
    [InlineData("notsupported")]
    public async Task FindAsync_PathWhoseStatFails_IsSkippedAndTheOthersAreStillCompared(string kind)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\img.jpg", "same", Stamp);
        fs.AddFile(@"C:\photos\img (1).jpg", "same", Stamp);
        fs.AddFile(@"C:\photos\locked.jpg", "same", Stamp);
        fs.StatHook = path => path.EndsWith("locked.jpg", StringComparison.OrdinalIgnoreCase)
            ? kind switch
            {
                "io" => new IOException("locked"),
                "unauthorized" => new UnauthorizedAccessException("locked"),
                "argument" => new ArgumentException("locked"),
                _ => new NotSupportedException("locked"),
            }
            : null;

        var result = await DuplicateFinder.FindAsync(
            [@"C:\photos\img.jpg", @"C:\photos\locked.jpg", @"C:\photos\img (1).jpg"], removeNumbered: true,
            (_, _) => Task.FromResult("same"), fs);

        Assert.Equal([@"C:\photos\img (1).jpg"], result);
    }

    // ---- ActionDestinationPolicy ----

    [Theory]
    [InlineData("*sel")]
    [InlineData("?sel")]
    [InlineData("|sel")]
    [InlineData(@"\\?\*sel")]
    [InlineData(@"\\?\|sel")]
    public void Validate_NameStartingWithAnInvalidCharacter_IsRejected(string destination)
    {
        Assert.Equal(ActionDestinationCheck.InvalidChars, ActionDestinationPolicy.Validate(destination));
    }

    // ---- FileFingerprint ----

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, -1, true)]
    [InlineData(2000, 0, true)]        // exactly the tolerance, later: still the same file
    [InlineData(-2000, 0, true)]       // exactly the tolerance, earlier
    [InlineData(2000, 1, false)]       // one tick beyond
    [InlineData(-2000, -1, false)]
    public void MatchesMovedDestination_WriteTimeAtTheToleranceBoundary_IsAcceptedOnlyUpToTheTolerance(int offsetMs, int extraTicks, bool expected)
    {
        var offset = TimeSpan.FromMilliseconds(offsetMs) + TimeSpan.FromTicks(extraTicks);
        var stat = new FileStat(10, Stamp + offset);

        Assert.Equal(expected, FileFingerprint.MatchesMovedDestination(stat, 10, Stamp));
    }

    [Fact]
    public void MatchesMovedDestination_DifferentSizeWithinTolerance_IsRejected()
    {
        Assert.False(FileFingerprint.MatchesMovedDestination(new FileStat(11, Stamp), 10, Stamp));
    }
}