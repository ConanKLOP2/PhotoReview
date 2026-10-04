using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// R11 (permanent delete fails closed unless the volume is positively a no-bin type) and R26 (size re-read right before the shell call).
/// Fakes only: the shell, File.Delete, drive type, mount point and file size are all injected, so no real file is deleted and the
/// real Recycle Bin is never touched. Constructs <c>WindowsRecycleBin</c>, hence the Integration category (rule TEST-OS).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecycleBinDeleteGuardTests
{
    private const string Photo = @"E:\photoreview-r11-nonexistent\a.jpg";
    private const long MiB = RecycleBinCapacityGuard.Mebibyte;

    private sealed class Settings : IRecycleBinSettingsSource
    {
        public RecycleBinVolumeSettings Volume { get; set; } = new(0, 100); // bin on, 100 MiB
        public RecycleBinPolicy ReadPolicy() => default;
        public string? GetVolumeGuid(string path) => "{0a089459-c105-4107-99e6-657d2fddd42b}";
        public DriveType? GetVolumeDriveType(string path) => DriveType.Fixed;
        public RecycleBinVolumeSettings ReadVolume(string volumeGuid) => Volume;
        public long? GetUserQuotaBytes(string path) => 100L * 1024 * MiB;
    }

    private static WindowsRecycleBin PermanentBin(DriveType? type, List<string> deleted, string? mount = @"E:\") =>
        new(null, new Settings(), driveTypeOf: _ => type, mountPointOf: _ => mount, fileDelete: deleted.Add);

    [Theory(DisplayName = "R11: DeletePermanently deletes only on a positively identified no-bin volume type")]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    public void DeletePermanently_KnownNoBinDrive_Deletes(DriveType type)
    {
        var deleted = new List<string>();

        // The fake File.Delete leaves nothing behind, and the path does not exist, so the post-condition holds.
        PermanentBin(type, deleted).DeletePermanently(Photo);

        Assert.Equal(Photo, Assert.Single(deleted));
    }

    [Theory(DisplayName = "R11: DeletePermanently refuses a fixed, unknown, no-root or unqueryable drive and never deletes")]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(null)]
    public void DeletePermanently_FixedOrUnknownDrive_RefusesWithoutDeleting(DriveType? type)
    {
        var deleted = new List<string>();

        Assert.Throws<InvalidOperationException>(() => PermanentBin(type, deleted).DeletePermanently(Photo));

        Assert.Empty(deleted);
    }

    [Fact(DisplayName = "R11: DeletePermanently refuses when the mount point cannot be resolved")]
    public void DeletePermanently_UnresolvableMountPoint_RefusesWithoutDeleting()
    {
        var deleted = new List<string>();

        Assert.Throws<InvalidOperationException>(() => PermanentBin(DriveType.Removable, deleted, mount: null).DeletePermanently(Photo));

        Assert.Empty(deleted);
    }

    [Fact(DisplayName = "R11: DeletePermanently refuses when the drive query throws, and still deletes on a UNC path")]
    public void DeletePermanently_DriveQueryThrows_RefusesAndUncStillAllowed()
    {
        var deleted = new List<string>();
        var throwing = new WindowsRecycleBin(null, new Settings(), driveTypeOf: _ => throw new IOException("transient"),
            mountPointOf: _ => @"E:\", fileDelete: deleted.Add);

        Assert.Throws<InvalidOperationException>(() => throwing.DeletePermanently(Photo));
        Assert.Empty(deleted);

        var unc = new WindowsRecycleBin(null, new Settings(), driveTypeOf: _ => null, mountPointOf: _ => null, fileDelete: deleted.Add);
        unc.DeletePermanently(@"\\nas\share\a.jpg");
        Assert.Equal(@"\\nas\share\a.jpg", Assert.Single(deleted));
    }

    private const string GuidPhoto = @"\\?\Volume{0a089459-c105-4107-99e6-657d2fddd42b}\x\a.jpg";
    private const string GuidRoot = @"\\?\Volume{0a089459-c105-4107-99e6-657d2fddd42b}\";

    [Theory(DisplayName = "R44: DeletePermanently refuses a volume-GUID / device path on a fixed, unknown or unqueryable volume")]
    [InlineData(GuidPhoto, DriveType.Fixed)]
    [InlineData(GuidPhoto, DriveType.Unknown)]
    [InlineData(GuidPhoto, null)]
    [InlineData(@"\\?\C:\x\a.jpg", DriveType.Fixed)]
    [InlineData(@"\\.\Volume{0a089459-c105-4107-99e6-657d2fddd42b}\x\a.jpg", DriveType.Fixed)]
    public void DeletePermanently_NonUncExtendedPathOnFixedVolume_RefusesWithoutDeleting(string path, DriveType? type)
    {
        var deleted = new List<string>();
        var queried = new List<string>();
        var bin = new WindowsRecycleBin(null, new Settings(), driveTypeOf: root => { queried.Add(root); return type; },
            mountPointOf: _ => GuidRoot, fileDelete: deleted.Add);

        Assert.Throws<InvalidOperationException>(() => bin.DeletePermanently(path));

        Assert.Empty(deleted);
        Assert.Equal(GuidRoot, Assert.Single(queried));
    }

    [Theory(DisplayName = "R44: DeletePermanently refuses a volume-GUID path whose mount point cannot be resolved")]
    [InlineData(null)]
    [InlineData("")]
    public void DeletePermanently_VolumeGuidPathUnresolvableMount_Refuses(string? mount)
    {
        var deleted = new List<string>();
        var bin = new WindowsRecycleBin(null, new Settings(), driveTypeOf: _ => DriveType.Removable, mountPointOf: _ => mount, fileDelete: deleted.Add);

        Assert.Throws<InvalidOperationException>(() => bin.DeletePermanently(GuidPhoto));

        Assert.Empty(deleted);
    }

    [Theory(DisplayName = "R44: DeletePermanently still deletes a volume-GUID path on a positively identified no-bin volume")]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    public void DeletePermanently_VolumeGuidPathOnNoBinVolume_Deletes(DriveType type)
    {
        var deleted = new List<string>();
        var bin = new WindowsRecycleBin(null, new Settings(), driveTypeOf: _ => type, mountPointOf: _ => GuidRoot, fileDelete: deleted.Add);

        bin.DeletePermanently(GuidPhoto);

        Assert.Equal(GuidPhoto, Assert.Single(deleted));
    }

    [Theory(DisplayName = "R44: real UNC syntaxes (share and extended UNC) stay permanent-delete eligible without a drive query")]
    [InlineData(@"\\nas\share\a.jpg")]
    [InlineData(@"\\?\UNC\nas\share\a.jpg")]
    [InlineData(@"\\?\unc\nas\share\a.jpg")]
    public void DeletePermanently_RealUncForms_Delete(string path)
    {
        var deleted = new List<string>();
        var bin = new WindowsRecycleBin(null, new Settings(), driveTypeOf: _ => null, mountPointOf: _ => null, fileDelete: deleted.Add);

        bin.DeletePermanently(path);

        Assert.Equal(path, Assert.Single(deleted));
    }

    [Theory(DisplayName = "R44: a mount point that is a real UNC share allows permanent delete, a volume-GUID mount is judged by its drive type")]
    [InlineData(@"\\nas\share\", DriveType.Fixed, true)]
    [InlineData(@"\\?\UNC\nas\share\", DriveType.Fixed, true)]
    [InlineData(GuidRoot, DriveType.Fixed, false)]
    [InlineData(GuidRoot, DriveType.Removable, true)]
    public void IsPermanentDeleteAllowed_MountPointForms(string mount, DriveType type, bool expected)
    {
        Assert.Equal(expected, RecycleEligibility.IsPermanentDeleteAllowed(@"E:\folder\a.jpg", _ => type, _ => mount));
    }

    [Theory(DisplayName = "R44: CanRecycle never recycles through a volume-GUID/device path (the shell would delete it permanently) nor UNC")]
    [InlineData(GuidPhoto)]
    [InlineData(@"\\.\Volume{0a089459-c105-4107-99e6-657d2fddd42b}\x\a.jpg")]
    [InlineData(@"\\?\UNC\nas\share\a.jpg")]
    [InlineData(@"\\nas\share\a.jpg")]
    public void CanRecycle_NonDriveLetterExtendedAndUncPaths_AreRefused(string path)
    {
        Assert.False(RecycleEligibility.CanRecycle(path, _ => DriveType.Fixed, _ => GuidRoot));
    }

    [Fact(DisplayName = "R44: CanRecycle keeps accepting ordinary and extended drive-letter paths on a fixed volume")]
    public void CanRecycle_DriveLetterForms_OnFixedVolume_Accepted()
    {
        Assert.True(RecycleEligibility.CanRecycle(@"E:\x\a.jpg", _ => DriveType.Fixed, _ => @"E:\"));
        Assert.True(RecycleEligibility.CanRecycle(@"\\?\E:\x\a.jpg", _ => DriveType.Fixed, _ => @"E:\"));
        Assert.False(RecycleEligibility.IsPermanentDeleteAllowed(@"\\?\E:\x\a.jpg", _ => DriveType.Fixed, _ => @"E:\"));
    }

    [Fact(DisplayName = "R26: SendToRecycleBin re-reads the size and refuses a file that outgrew the bin after the preflight, never calling the shell")]
    public void SendToRecycleBin_FileGrewAfterPreflight_RefusedWithoutShell()
    {
        var shellCalls = 0;
        long size = 1 * MiB;
        var bin = new WindowsRecycleBin(null, new Settings(), _ => shellCalls++, driveTypeOf: _ => DriveType.Fixed,
            mountPointOf: _ => @"C:\", fileLength: _ => size);

        Assert.True(bin.FitsInRecycleBin(@"C:\photos\a.jpg", size)); // the caller's preflight: fits

        size = 500 * MiB; // the file keeps growing between preflight and the shell call
        var ex = Assert.Throws<IOException>(() => bin.SendToRecycleBin(@"C:\photos\a.jpg"));

        Assert.Equal(Tr.CoreRecycleBinCannotHold("a.jpg"), ex.Message);
        Assert.Equal(0, shellCalls);
    }

    [Fact(DisplayName = "R26: SendToRecycleBin refuses when the size cannot be read and does not call the shell")]
    public void SendToRecycleBin_SizeUnreadable_RefusedWithoutShell()
    {
        var shellCalls = 0;
        var bin = new WindowsRecycleBin(null, new Settings(), _ => shellCalls++, driveTypeOf: _ => DriveType.Fixed,
            mountPointOf: _ => @"C:\", fileLength: _ => throw new IOException("locked"));

        Assert.Throws<IOException>(() => bin.SendToRecycleBin(@"C:\photos\a.jpg"));

        Assert.Equal(0, shellCalls);
    }
}
