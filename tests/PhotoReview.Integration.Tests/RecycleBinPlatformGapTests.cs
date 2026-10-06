using System.IO;
using Microsoft.Win32;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Branches of the Recycle Bin platform code that the guard tests do not reach: the registry reader (through its
/// (hive, key, value) seam, so no registry value is read or changed), the volume queries on the temp folder, and the
/// WindowsRecycleBin refusal paths. Fakes only: the shell, the real Recycle Bin and every delete are replaced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecycleBinPlatformGapTests : IDisposable
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
    private const string VolumeGuid = "{0a089459-c105-4107-99e6-657d2fddd42b}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewRbGap_" + System.Guid.NewGuid().ToString("N"));

    public RecycleBinPlatformGapTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeRegistry
    {
        public Dictionary<(RegistryHive Hive, string Key, string Name), object> Values { get; } = [];
        public List<(RegistryHive Hive, string Key, string Name)> Reads { get; } = [];

        public object? Read(RegistryHive hive, string key, string name)
        {
            Reads.Add((hive, key, name));
            return Values.GetValueOrDefault((hive, key, name));
        }

        public WindowsRecycleBinSettingsSource Source() => new(Read);
    }

    // ---- registry reader ----

    [Fact]
    public void ReadPolicy_NothingConfigured_IsAllOff()
    {
        var policy = new FakeRegistry().Source().ReadPolicy();

        Assert.Equal(new RecycleBinPolicy(false, null, false), policy);
    }

    [Theory]
    [InlineData(RegistryHive.CurrentUser)]
    [InlineData(RegistryHive.LocalMachine)]
    public void ReadPolicy_NoRecycleFilesInEitherHive_DisablesTheBin(RegistryHive hive)
    {
        var registry = new FakeRegistry();
        registry.Values[(hive, PolicyKey, "NoRecycleFiles")] = 1;

        Assert.True(registry.Source().ReadPolicy().NoRecycleFiles);
    }

    [Fact]
    public void ReadPolicy_NoRecycleFilesZero_LeavesTheBinOn()
    {
        var registry = new FakeRegistry();
        registry.Values[(RegistryHive.CurrentUser, PolicyKey, "NoRecycleFiles")] = 0;
        registry.Values[(RegistryHive.LocalMachine, PolicyKey, "NoRecycleFiles")] = 0;

        Assert.False(registry.Source().ReadPolicy().NoRecycleFiles);
    }

    [Theory]
    [InlineData(10, null, 10L)]
    [InlineData(null, 7, 7L)]
    [InlineData(10, 7, 7L)]
    [InlineData(5, 20, 5L)]
    public void ReadPolicy_RecycleBinSize_UsesTheStricterOfUserAndMachine(int? user, int? machine, long expected)
    {
        var registry = new FakeRegistry();
        if (user is { } u) registry.Values[(RegistryHive.CurrentUser, PolicyKey, "RecycleBinSize")] = u;
        if (machine is { } m) registry.Values[(RegistryHive.LocalMachine, PolicyKey, "RecycleBinSize")] = m;

        Assert.Equal(expected, registry.Source().ReadPolicy().MaxSizePercent);
    }

    [Fact]
    public void ReadPolicy_LegacyGlobalNukeOnDelete_ReadsTheBitBucketKeyOfTheUser()
    {
        var registry = new FakeRegistry();
        registry.Values[(RegistryHive.CurrentUser, BitBucketKey, "NukeOnDelete")] = 1;

        Assert.True(registry.Source().ReadPolicy().LegacyGlobalNukeOnDelete);
        Assert.False(new FakeRegistry().Source().ReadPolicy().LegacyGlobalNukeOnDelete);
    }

    [Fact]
    public void ReadPolicy_DwordWithHighBitSet_IsReadAsUnsigned()
    {
        var registry = new FakeRegistry();
        registry.Values[(RegistryHive.CurrentUser, PolicyKey, "RecycleBinSize")] = -1; // REG_DWORD 0xFFFFFFFF arrives as int -1

        Assert.Equal(uint.MaxValue, registry.Source().ReadPolicy().MaxSizePercent);
    }

    [Fact]
    public void ReadPolicy_ValueThatIsNotADword_ThrowsAndTheGuardFailsClosed()
    {
        var registry = new FakeRegistry();
        registry.Values[(RegistryHive.LocalMachine, PolicyKey, "NoRecycleFiles")] = "yes";
        var source = registry.Source();

        Assert.Throws<InvalidDataException>(() => source.ReadPolicy());
        Assert.Equal(RecycleCapacityVerdict.Unknown, RecycleBinCapacityGuard.Evaluate(Path.Combine(_root, "a.jpg"), 1, source));
    }

    [Fact]
    public void ReadVolume_ReadsBothValuesOfThatVolumesKey()
    {
        var registry = new FakeRegistry();
        var key = BitBucketKey + @"\Volume\" + VolumeGuid;
        registry.Values[(RegistryHive.CurrentUser, key, "NukeOnDelete")] = 1;
        registry.Values[(RegistryHive.CurrentUser, key, "MaxCapacity")] = 5120;
        registry.Values[(RegistryHive.CurrentUser, BitBucketKey + @"\Volume\{other}", "MaxCapacity")] = 1;

        Assert.Equal(new RecycleBinVolumeSettings(1, 5120), registry.Source().ReadVolume(VolumeGuid));
        Assert.Equal(new RecycleBinVolumeSettings(null, null), new FakeRegistry().Source().ReadVolume(VolumeGuid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ReadVolume_BlankVolumeName_Throws(string volume)
        => Assert.Throws<ArgumentException>(() => new FakeRegistry().Source().ReadVolume(volume));

    [Fact]
    public void Constructor_NullReader_Throws()
        => Assert.Throws<ArgumentNullException>(() => new WindowsRecycleBinSettingsSource(null!));

    [Fact]
    public void RealRegistryReader_AbsentVolumeKey_ReadsAsNotConfigured()
    {
        // The default reader only opens HKCU read-only; a volume key that does not exist reads as "not configured".
        var source = new WindowsRecycleBinSettingsSource();

        Assert.Equal(new RecycleBinVolumeSettings(null, null), source.ReadVolume(System.Guid.NewGuid().ToString("B")));
    }

    // ---- volume queries (kernel32, read-only) ----

    [Fact]
    public void VolumeQueries_ForAFileInTheTempFolder_AgreeWithEachOther()
    {
        var path = Path.Combine(_root, "probe.jpg");
        var source = new WindowsRecycleBinSettingsSource(static (_, _, _) => null);

        var mount = WindowsRecycleBinSettingsSource.GetVolumeMountPoint(path);

        Assert.NotNull(mount);
        Assert.EndsWith(@"\", mount, StringComparison.Ordinal);
        Assert.StartsWith(mount, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(source.GetVolumeDriveType(path));
        Assert.True(source.GetUserQuotaBytes(path) > 0);
        var guid = source.GetVolumeGuid(path);
        Assert.NotNull(guid);
        Assert.True(System.Guid.TryParseExact(guid, "B", out _));
    }

    // ---- WindowsRecycleBin refusals (fakes only) ----

    private sealed class FitsSettings : IRecycleBinSettingsSource
    {
        public RecycleBinPolicy ReadPolicy() => new(false, null, false);
        public string? GetVolumeGuid(string path) => VolumeGuid;
        public DriveType? GetVolumeDriveType(string path) => DriveType.Fixed;
        public RecycleBinVolumeSettings ReadVolume(string volumeGuid) => new(0, 10_240);
        public long? GetUserQuotaBytes(string path) => 100L * 1024 * 1024 * 1024;
    }

    private static WindowsRecycleBin Bin(Action<string>? shell = null, Func<string, DriveType?>? driveTypeOf = null,
        Func<string, string?>? mountPointOf = null, Action<string>? fileDelete = null)
        => new(null, new FitsSettings(), shell ?? (_ => throw new InvalidOperationException("the shell must not be called")),
            (_, _, _) => throw new InvalidOperationException("restore must not be called"),
            driveTypeOf ?? (_ => DriveType.Fixed), mountPointOf ?? (_ => @"C:\"), fileDelete ?? (_ => throw new InvalidOperationException("nothing may be deleted")),
            _ => 1024);

    [Theory]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    public void SendToRecycleBin_VolumeWithoutABin_ThrowsBeforeTheShellIsCalled(DriveType type)
    {
        var bin = Bin(driveTypeOf: _ => type);

        Assert.Throws<IOException>(() => bin.SendToRecycleBin(@"E:\photos\a.jpg"));
    }

    [Fact]
    public void SendToRecycleBin_ShellReturnsButTheFileIsStillThere_IsAnErrorNotASuccess()
    {
        var file = Path.Combine(_root, "stays.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        var shellCalls = 0;
        var bin = Bin(shell: _ => shellCalls++); // a cancelled shell operation: returns without deleting or throwing

        Assert.Throws<IOException>(() => bin.SendToRecycleBin(file));

        Assert.Equal(1, shellCalls);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void SendToRecycleBin_ShellRemovesTheFile_Succeeds()
    {
        var file = Path.Combine(_root, "gone.jpg");
        File.WriteAllBytes(file, [1]);
        var bin = Bin(shell: path => File.Delete(path)); // stands in for the shell moving it into the bin

        bin.SendToRecycleBin(file);

        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, false)]
    [InlineData(DriveType.Network, false)]
    public void CanRecycle_FollowsTheVolumeTypeOfTheMountPoint(DriveType type, bool expected)
    {
        var asked = new List<string>();
        var bin = Bin(driveTypeOf: mount => { asked.Add(mount); return type; }, mountPointOf: _ => @"D:\");

        Assert.Equal(expected, bin.CanRecycle(@"D:\photos\a.jpg"));
        Assert.Equal([@"D:\"], asked);
    }

    [Fact]
    public void CanRecycle_BlankPath_Throws()
        => Assert.Throws<ArgumentException>(() => Bin().CanRecycle(" "));

    [Fact]
    public void DeletePermanently_FileSurvivesTheDelete_IsAnError()
    {
        var file = Path.Combine(_root, "locked.jpg");
        File.WriteAllBytes(file, [1]);
        var deleted = new List<string>();
        var bin = Bin(driveTypeOf: _ => DriveType.Removable, fileDelete: deleted.Add); // the delete silently did nothing

        Assert.Throws<IOException>(() => bin.DeletePermanently(file));

        Assert.Equal([file], deleted);
        Assert.True(File.Exists(file));
    }

    // ---- RecycleEligibility on unusable input and without a mount-point resolver ----

    [Fact]
    public void Eligibility_PathThatCannotBeResolved_IsNeverRecyclableNorPermanentlyDeletable()
    {
        const string bad = "C:\\photos\\a\0b.jpg";

        Assert.False(RecycleEligibility.CanRecycle(bad, _ => DriveType.Fixed));
        Assert.False(RecycleEligibility.IsPermanentDeleteAllowed(bad, _ => DriveType.Removable));
    }

    [Theory]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, true)]
    [InlineData(DriveType.Fixed, false)]
    [InlineData(DriveType.Unknown, false)]
    public void IsPermanentDeleteAllowed_WithoutAResolver_JudgesTheTextualDriveRoot(DriveType type, bool expected)
    {
        var asked = new List<string>();

        var allowed = RecycleEligibility.IsPermanentDeleteAllowed(@"E:\photos\a.jpg", root => { asked.Add(root); return type; });

        Assert.Equal(expected, allowed);
        Assert.Equal([@"E:\"], asked);
    }

    [Fact]
    public void IsPermanentDeleteAllowed_MountPointLookupThrowsIo_RefusesFailClosed()
    {
        var allowed = RecycleEligibility.IsPermanentDeleteAllowed(@"E:\photos\a.jpg", _ => DriveType.Removable,
            _ => throw new IOException("mount lookup failed"));

        Assert.False(allowed);
    }

    [Fact]
    public void QueryDriveType_ForTheTempRoot_IsAKnownType()
    {
        var root = Path.GetPathRoot(_root)!;

        var type = RecycleEligibility.QueryDriveType(root);

        Assert.NotNull(type);
        Assert.NotEqual(DriveType.NoRootDirectory, type);
        Assert.Equal(type, RecycleEligibility.QueryDriveType(root.TrimEnd('\\'))); // with or without the trailing separator
    }

    // ---- WaitForRestore ----

    [Fact]
    public void WaitForRestore_ExpectedFilePresent_ReturnsTrueAtOnce()
    {
        var file = Path.Combine(_root, "restored.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        var written = File.GetLastWriteTimeUtc(file);

        Assert.True(WindowsRecycleBin.WaitForRestore(file, 3, written, attempts: 1, pauseMs: 1));
    }

    [Fact]
    public void WaitForRestore_FileNeverAppearsOrDiffers_ReturnsFalseAfterTheLastCheck()
    {
        var file = Path.Combine(_root, "other.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        var written = File.GetLastWriteTimeUtc(file);

        Assert.False(WindowsRecycleBin.WaitForRestore(file, 4, written, attempts: 2, pauseMs: 1)); // another size
        Assert.False(WindowsRecycleBin.WaitForRestore(Path.Combine(_root, "never.jpg"), 3, written, attempts: 2, pauseMs: 1));
    }
}
