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

    // P-RB-01: the decision follows the volume mount point GetVolumePathName resolves (junctions, mounted folders), not the textual root.

    [Fact(DisplayName = "P-RB-01: a path under C: that resolves to a non-fixed mount point (junction / mounted folder) is refused")]
    public void CanRecycle_JunctionToNonFixedVolume_ReturnsFalse()
    {
        string? asked = null;
        DriveType? DriveTypeOf(string root) { asked = root; return root == @"C:\" ? DriveType.Fixed : DriveType.Removable; }

        var result = RecycleEligibility.CanRecycle(@"C:\link\usb\x.jpg", DriveTypeOf, _ => @"E:\");

        Assert.False(result);
        Assert.Equal(@"E:\", asked);
    }

    [Fact(DisplayName = "P-RB-01: a volume mounted as a folder is judged by its own drive type")]
    public void CanRecycle_MountedFolder_UsesMountPointType()
    {
        Func<string, DriveType?> typeOf = root => root == @"C:\mnt\usb\" ? DriveType.Removable : DriveType.Fixed;

        Assert.False(RecycleEligibility.CanRecycle(@"C:\mnt\usb\x.jpg", typeOf, _ => @"C:\mnt\usb\"));
        Assert.True(RecycleEligibility.CanRecycle(@"C:\mnt\fixeddisk\x.jpg", typeOf, _ => @"C:\mnt\fixeddisk\"));
    }

    [Fact(DisplayName = "P-RB-01: a fixed volume reached through a junction is accepted")]
    public void CanRecycle_JunctionToFixedVolume_ReturnsTrue()
    {
        Assert.True(RecycleEligibility.CanRecycle(@"C:\link\x.jpg", _ => DriveType.Fixed, _ => @"D:\"));
    }

    [Theory(DisplayName = "P-RB-01: an unresolvable mount point or a UNC mount point is refused without asking the drive type")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\nas\share\")]
    public void CanRecycle_UnresolvableOrUncMountPoint_ReturnsFalse(string? mount)
    {
        Assert.False(RecycleEligibility.CanRecycle(@"C:\link\x.jpg", _ => throw new InvalidOperationException("must not be asked"), _ => mount));
    }

    [Fact(DisplayName = "P-RB-01: a resolver that throws an I/O error refuses")]
    public void CanRecycle_ResolverThrows_ReturnsFalse()
    {
        Assert.False(RecycleEligibility.CanRecycle(@"C:\a.jpg", _ => DriveType.Fixed, _ => throw new IOException("boom")));
    }

    [Fact(DisplayName = "P-RB-01: UNC paths stay refused even when the resolver would say fixed")]
    public void CanRecycle_UncPathWithResolver_ReturnsFalse()
    {
        Assert.False(RecycleEligibility.CanRecycle(@"\\nas\share\a.jpg", _ => DriveType.Fixed, _ => @"C:\"));
    }

    [Fact(DisplayName = "P-RB-01: a relative path is resolved against the current directory before the mount point is asked")]
    public void CanRecycle_RelativePath_ResolverReceivesFullPath()
    {
        string? received = null;

        var result = RecycleEligibility.CanRecycle(Path.Combine("rel", "a.jpg"), _ => DriveType.Fixed, full => { received = full; return Path.GetPathRoot(full); });

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(Path.Combine("rel", "a.jpg")), received);
    }
}
