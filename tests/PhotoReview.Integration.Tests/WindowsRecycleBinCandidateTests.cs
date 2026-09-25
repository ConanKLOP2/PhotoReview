using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

public sealed class WindowsRecycleBinCandidateTests
{
    [Fact]
    public void SelectorRequiresOriginalPathSizeAndTimestamp()
    {
        var path = @"C:\photos\same.jpg";
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);
        var candidate = new RecycleCandidate(@"C:\photos", "same.jpg", 42, timestamp);

        Assert.True(RecycleCandidateSelector.IsMatch(candidate, path, 42, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate, path, 41, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate, path, 42, timestamp.AddSeconds(1)));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate, @"C:\other\same.jpg", 42, timestamp));
    }

    [Fact(DisplayName = "Selector matches the shell's whole-second timestamp for a file written with sub-second precision")]
    public void SelectorToleratesShellWholeSecondTimestamps()
    {
        var path = @"C:\photos\same.jpg";
        var written = new DateTime(2026, 9, 24, 14, 58, 16, DateTimeKind.Utc).AddTicks(6190398);
        var shellValue = new DateTime(2026, 9, 24, 14, 58, 16, DateTimeKind.Utc);
        var candidate = new RecycleCandidate(@"C:\photos", "same.jpg", 4, shellValue);

        Assert.True(RecycleCandidateSelector.IsMatch(candidate, path, 4, written));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate, path, 4, written.AddSeconds(2)));
    }

    [Fact(DisplayName = "Selector accepts either the UTC or the local reading of the shell's unzoned timestamp")]
    public void SelectorAcceptsAlternateTimestampReading()
    {
        var path = @"C:\photos\same.jpg";
        var written = new DateTime(2026, 9, 24, 14, 58, 16, DateTimeKind.Utc);
        var wrongPrimary = written.AddHours(7);
        var candidate = new RecycleCandidate(@"C:\photos", "same.jpg", 4, wrongPrimary, written);

        Assert.True(RecycleCandidateSelector.IsMatch(candidate, path, 4, written));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate with { AlternateLastWriteUtc = null }, path, 4, written));
    }

    [Fact]
    public void SelectorDoesNotAcceptAmbiguousCandidateByMetadata()
    {
        var path = @"C:\photos\same.jpg";
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);
        // The same file recycled twice: once while extensions were shown, once while hidden.
        var first = new RecycleCandidate(@"C:\photos", "same.jpg", 42, timestamp);
        var second = new RecycleCandidate(@"C:\photos", "same", 42, timestamp);

        Assert.True(RecycleCandidateSelector.IsMatch(first, path, 42, timestamp));
        Assert.True(RecycleCandidateSelector.IsMatch(second, path, 42, timestamp));
        // WindowsRecycleBin requires exactly one matching candidate before invoking Restore.
        Assert.Equal(2, new[] { first, second }.Count(c => RecycleCandidateSelector.IsMatch(c, path, 42, timestamp)));
    }

    [Fact(DisplayName = "Selector matches the display name without extension (Explorer hides known extensions by default)")]
    public void SelectorMatchesDisplayNameWithoutExtension()
    {
        var path = @"C:\photos\IMG_0001.JPG";
        var timestamp = new DateTime(2026, 9, 25, 1, 2, 3, DateTimeKind.Utc);
        var candidate = new RecycleCandidate(@"C:\photos", "IMG_0001", 42, timestamp);

        Assert.True(RecycleCandidateSelector.IsMatch(candidate, path, 42, timestamp));
        Assert.True(RecycleCandidateSelector.IsMatch(candidate with { DeletedFrom = @"c:\PHOTOS\" }, path, 42, timestamp));
        // Folder, size and timestamp are still required.
        Assert.False(RecycleCandidateSelector.IsMatch(candidate with { DeletedFrom = @"C:\other" }, path, 42, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate, path, 41, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate, path, 42, timestamp.AddSeconds(5)));
        // A different base name never matches.
        Assert.False(RecycleCandidateSelector.IsMatch(candidate with { Name = "IMG_0002" }, path, 42, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(candidate with { Name = "IMG_000" }, path, 42, timestamp));
    }

    [Fact(DisplayName = "Selector keeps only the last extension off the display name (a.b.jpg shows as a.b)")]
    public void SelectorMatchesMultiDotNameWithoutLastExtension()
    {
        var path = @"C:\photos\a.b.jpg";
        var timestamp = new DateTime(2026, 9, 25, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(RecycleCandidateSelector.IsMatch(new RecycleCandidate(@"C:\photos", "a.b", 1, timestamp), path, 1, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(new RecycleCandidate(@"C:\photos", "a", 1, timestamp), path, 1, timestamp));
    }

    [Fact(DisplayName = "DeletedFrom is the original folder: a full-path DeletedFrom without a name does not match")]
    public void SelectorRequiresFolderPlusName()
    {
        var path = @"C:\photos\same.jpg";
        var timestamp = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);

        Assert.False(RecycleCandidateSelector.IsMatch(new RecycleCandidate(path, null, 42, timestamp), path, 42, timestamp));
        Assert.False(RecycleCandidateSelector.IsMatch(new RecycleCandidate(null, "same.jpg", 42, timestamp), path, 42, timestamp));
        Assert.True(RecycleCandidateSelector.IsMatch(new RecycleCandidate(@"C:\", "same", 42, timestamp), @"C:\same.jpg", 42, timestamp));
    }
}
