using PhotoReview.Core.FileActions;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>"Move to… / Copy to…": a picked or remembered destination folder (pure path logic; existence is injected).</summary>
public sealed class PickedFolderPolicyTests
{
    private const string PhotoFolder = @"D:\Photos\Trip";

    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"D:\Photos", @"D:\Photos\Trip", @"D:\Photos\Trip\Best", @"E:\Backup", @"\\server\share\x", @"E:\",
    };

    [Theory]
    [InlineData(@"E:\Backup", PickedFolderCheck.Ok)]
    [InlineData(@"E:\Backup\", PickedFolderCheck.Ok)]
    [InlineData(@"E:\", PickedFolderCheck.Ok)]
    [InlineData(@"D:\Photos", PickedFolderCheck.Ok)]
    [InlineData(@"D:\Photos\Trip\Best", PickedFolderCheck.Ok)]
    [InlineData(@"\\server\share\x", PickedFolderCheck.Ok)]
    [InlineData(@"D:\Photos\Trip", PickedFolderCheck.SameAsPhotoFolder)]
    [InlineData(@"d:\photos\TRIP\", PickedFolderCheck.SameAsPhotoFolder)]
    [InlineData(@"D:\Photos\Trip\Best\..", PickedFolderCheck.SameAsPhotoFolder)]
    [InlineData(@"E:\Gone", PickedFolderCheck.Missing)]
    [InlineData("Best", PickedFolderCheck.NotAbsolute)]
    [InlineData(@"..\Other", PickedFolderCheck.NotAbsolute)]
    [InlineData(@"\Photos", PickedFolderCheck.NotAbsolute)]
    [InlineData("E:Backup", PickedFolderCheck.NotAbsolute)]
    [InlineData(@"E:\a|b", PickedFolderCheck.NotAbsolute)]
    [InlineData("", PickedFolderCheck.Empty)]
    [InlineData("  ", PickedFolderCheck.Empty)]
    [InlineData(null, PickedFolderCheck.Empty)]
    public void ValidatePickedFolder_Classifies(string? destination, PickedFolderCheck expected) =>
        Assert.Equal(expected, ActionDestinationPolicy.ValidatePickedFolder(destination, PhotoFolder, Existing.Contains));

    [Fact]
    public void ValidatePickedFolder_ChecksExistenceOfTheNormalizedPath()
    {
        var asked = new List<string>();
        var check = ActionDestinationPolicy.ValidatePickedFolder(@"E:\Backup\", PhotoFolder, p => { asked.Add(p); return true; });

        Assert.Equal(PickedFolderCheck.Ok, check);
        Assert.Equal([@"E:\Backup"], asked);
    }
}
