using System.IO;
using PhotoReview.App.Services;
using PhotoReview.Core.FileActions;
using Xunit;

namespace PhotoReview.App.Tests.Services;

public sealed class WpfDialogServiceTests
{
    [Theory(DisplayName = "RV-A14: an unreadable journal is reported to the user, not swallowed")]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void TryReadRecoveryEntries_JournalReadThrows_ShowsErrorAndReturnsNull(Type exceptionType)
    {
        var shown = new List<(string Title, string Message)>();

        var entries = WpfDialogService.TryReadRecoveryEntries(
            () => throw (Exception)Activator.CreateInstance(exceptionType, "journal is locked")!,
            (title, message) => shown.Add((title, message)));

        Assert.Null(entries);
        var error = Assert.Single(shown);
        Assert.Contains("journal is locked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadRecoveryEntries_JournalReadable_ReturnsEntriesWithoutError()
    {
        var shown = 0;

        var entries = WpfDialogService.TryReadRecoveryEntries(() => [], (_, _) => shown++);

        Assert.NotNull(entries);
        Assert.Empty(entries);
        Assert.Equal(0, shown);
    }

    [Fact]
    public void TryReadRecoveryEntries_UnrelatedException_Propagates()
        => Assert.Throws<InvalidOperationException>(() => WpfDialogService.TryReadRecoveryEntries(
            () => throw new InvalidOperationException("bug"), (_, _) => { }));
}
