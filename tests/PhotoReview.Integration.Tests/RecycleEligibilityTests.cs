using System.IO;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>R2-F-05: SHFileOperation recycles only on local fixed drives; elsewhere it deletes permanently, so the bin refuses.</summary>
public sealed class RecycleEligibilityTests
{
    [Fact(DisplayName = "CanRecycle accepts a path on a fixed drive")]
    public void CanRecycle_FixedDrive_ReturnsTrue()
    {
        Assert.True(RecycleEligibility.CanRecycle(@"C:\photos\a.jpg", _ => DriveType.Fixed));
    }

    [Theory(DisplayName = "CanRecycle refuses drives that have no Recycle Bin")]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void CanRecycle_NonFixedDrive_ReturnsFalse(DriveType type)
    {
        Assert.False(RecycleEligibility.CanRecycle(@"E:\DCIM\a.jpg", _ => type));
    }

    [Fact(DisplayName = "CanRecycle refuses a drive whose type cannot be determined")]
    public void CanRecycle_UnknownDrive_ReturnsFalse()
    {
        Assert.False(RecycleEligibility.CanRecycle(@"E:\DCIM\a.jpg", _ => null));
    }

    [Theory(DisplayName = "CanRecycle refuses UNC paths without asking the drive type")]
    [InlineData(@"\\nas\share\a.jpg")]
    [InlineData(@"\\?\UNC\nas\share\a.jpg")]
    public void CanRecycle_UncPath_ReturnsFalse(string path)
    {
        Assert.False(RecycleEligibility.CanRecycle(path, _ => DriveType.Fixed));
    }

    [Fact(DisplayName = "CanRecycle asks about the drive root of an extended-length local path")]
    public void CanRecycle_ExtendedLocalPath_QueriesDriveRoot()
    {
        string? asked = null;

        var result = RecycleEligibility.CanRecycle(@"\\?\D:\photos\a.jpg", root => { asked = root; return DriveType.Fixed; });

        Assert.True(result);
        Assert.Equal(@"D:\", asked);
    }
}
