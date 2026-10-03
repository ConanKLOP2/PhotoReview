using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class ActionDestinationPolicyTests
{
    [Theory]
    [InlineData("Loai-2", ActionDestinationCheck.Ok)]
    [InlineData("sub\\deeper", ActionDestinationCheck.Ok)]
    [InlineData("..\\x", ActionDestinationCheck.EscapesSourceFolder)]
    [InlineData("a\\..\\..\\x", ActionDestinationCheck.EscapesSourceFolder)]
    [InlineData("a/../../x", ActionDestinationCheck.EscapesSourceFolder)]
    [InlineData("D:\\Backup", ActionDestinationCheck.Ok)]
    [InlineData("\\\\server\\share\\x", ActionDestinationCheck.Ok)]
    [InlineData("\\photos", ActionDestinationCheck.InvalidChars)]
    [InlineData("/photos", ActionDestinationCheck.InvalidChars)]
    [InlineData("a|b", ActionDestinationCheck.InvalidChars)]
    [InlineData("a:b", ActionDestinationCheck.InvalidChars)]
    [InlineData("sel*", ActionDestinationCheck.InvalidChars)]
    [InlineData("a?b", ActionDestinationCheck.InvalidChars)]
    [InlineData("D:\\Backup\\*", ActionDestinationCheck.InvalidChars)]
    [InlineData(@"\\?\D:\Backup", ActionDestinationCheck.Ok)]
    [InlineData(@"\\?\UNC\server\share\x", ActionDestinationCheck.Ok)]
    [InlineData(@"\\.\D:\Backup", ActionDestinationCheck.Ok)]
    [InlineData(@"\\?\D:\Back*", ActionDestinationCheck.InvalidChars)]
    [InlineData(@"\\?\D:\Back?up", ActionDestinationCheck.InvalidChars)]
    [InlineData("", ActionDestinationCheck.Empty)]
    [InlineData("  ", ActionDestinationCheck.Empty)]
    public void Validate_ClassifiesDestinations(string destination, ActionDestinationCheck expected)
    {
        Assert.Equal(expected, ActionDestinationPolicy.Validate(destination));
    }

    [Fact(DisplayName = "SEC-01: a junction resolving outside the source folder is rejected even though the lexical path is contained")]
    public void ValidateNoEscapeViaReparsePoint_JunctionEscapesFolder_Rejected()
    {
        var fs = new InMemoryFileSystem();
        fs.AddReparsePoint(@"C:\photos\link", @"C:\outside");

        var result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(@"C:\photos", @"C:\photos\link\processed", fs);

        Assert.Equal(ActionDestinationCheck.EscapesSourceFolder, result);
    }

    [Theory(DisplayName = "RV-S06: a destination whose reparse points cannot be resolved is rejected (fail closed), not thrown")]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateNoEscapeViaReparsePoint_UnresolvableLink_FailsClosed(bool accessDenied)
    {
        var fs = new InMemoryFileSystem();
        // What PhysicalFileSystem.ResolveRealPath throws for a link loop (ERROR_CANT_RESOLVE_FILENAME) or an unreadable link.
        fs.ResolveRealPathHook = path => path.StartsWith(@"C:\photos\loop", StringComparison.OrdinalIgnoreCase)
            ? (accessDenied ? new UnauthorizedAccessException("denied") : new IOException("The name of the file cannot be resolved by the system."))
            : null;

        ActionDestinationCheck result = ActionDestinationCheck.Ok;
        var ex = Record.Exception(() =>
            result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(@"C:\photos", @"C:\photos\loop\processed\a.jpg", fs));

        Assert.Null(ex);
        Assert.Equal(ActionDestinationCheck.EscapesSourceFolder, result);
    }

    [Fact(DisplayName = "SEC-01: a junction that resolves back inside the source folder is still allowed")]
    public void ValidateNoEscapeViaReparsePoint_JunctionStaysInsideFolder_Allowed()
    {
        var fs = new InMemoryFileSystem();
        fs.AddReparsePoint(@"C:\photos\link", @"C:\photos\real");

        var result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(@"C:\photos", @"C:\photos\link\processed", fs);

        Assert.Equal(ActionDestinationCheck.Ok, result);
    }

    [Fact(DisplayName = "SEC-01: an ordinary destination with no reparse points anywhere is unaffected")]
    public void ValidateNoEscapeViaReparsePoint_OrdinaryDestination_Allowed()
    {
        var fs = new InMemoryFileSystem();

        var result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(@"C:\photos", @"C:\photos\sub\deeper", fs);

        Assert.Equal(ActionDestinationCheck.Ok, result);
    }

    [Fact(DisplayName = "SEC-01: a destination FILE that is itself a symlink pointing outside the source folder is rejected")]
    public void ValidateNoEscapeViaReparsePoint_DestinationFileIsSymlinkOutside_Rejected()
    {
        var fs = new InMemoryFileSystem();
        // The final path segment itself (not just an intermediate directory) is registered as a reparse point,
        // simulating a symlinked destination file whose real target is outside the photo folder.
        fs.AddReparsePoint(@"C:\photos\a.jpg", @"C:\outside\a.jpg");

        var result = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(@"C:\photos", @"C:\photos\a.jpg", fs);

        Assert.Equal(ActionDestinationCheck.EscapesSourceFolder, result);
    }
}
