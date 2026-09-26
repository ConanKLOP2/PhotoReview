using PhotoReview.Core.FileActions;
using Xunit;

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
}
