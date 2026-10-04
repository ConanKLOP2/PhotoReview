using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// F-WIN-2: with FOF_NOCONFIRMATION the shell deletes a file PERMANENTLY when its volume's Recycle Bin is turned off or
/// smaller than the file. The guard must refuse those cases (and every unreadable case) before the shell is called.
/// Fakes only: no registry value is changed and the real Recycle Bin is never touched.
/// The file constructs <c>WindowsRecycleBin</c> (always with a fake settings source and a fake shell), hence the
/// Integration category required by rule TEST-OS; the tests still run in the default filter.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecycleBinCapacityGuardTests
{
    private const string Photo = @"C:\photos\a.jpg";
    private const string Guid = "{0a089459-c105-4107-99e6-657d2fddd42b}";
    private const long MiB = RecycleBinCapacityGuard.Mebibyte;
    private const long GiB = 1024 * MiB;

    private sealed class FakeSettings : IRecycleBinSettingsSource
    {
        public RecycleBinPolicy Policy { get; set; }
        public string? VolumeGuid { get; set; } = Guid;
        public RecycleBinVolumeSettings Volume { get; set; } = new(0, 10_240); // bin on, 10 GiB
        public long? QuotaBytes { get; set; } = 100 * GiB;
        public Exception? Throw { get; set; }
        public int QuotaCalls { get; private set; }
        public string? AskedGuid { get; private set; }

        public RecycleBinPolicy ReadPolicy() => Throw is null ? Policy : throw Throw;
        public string? GetVolumeGuid(string path) => VolumeGuid;
        public DriveType? VolumeDriveType { get; set; } = DriveType.Fixed;
        public DriveType? GetVolumeDriveType(string path) => VolumeDriveType;

        public RecycleBinVolumeSettings ReadVolume(string volumeGuid)
        {
            AskedGuid = volumeGuid;
            return Volume;
        }

        public long? GetUserQuotaBytes(string path)
        {
            QuotaCalls++;
            return QuotaBytes;
        }
    }

    private readonly FakeSettings _settings = new();

    private RecycleCapacityVerdict Evaluate(long? size) => RecycleBinCapacityGuard.Evaluate(Photo, size, _settings);

    [Fact(DisplayName = "A normal photo on a volume with the bin on fits, using that volume's settings")]
    public void Evaluate_SmallFileBinOn_Fits()
    {
        Assert.Equal(RecycleCapacityVerdict.Fits, Evaluate(20 * MiB));
        Assert.Equal(Guid, _settings.AskedGuid);
        Assert.Equal(0, _settings.QuotaCalls); // explicit MaxCapacity: no volume-size query
    }

    [Theory(DisplayName = "P-RB-01: a path whose real volume is not fixed (or unresolvable) is Unknown, with or without a size")]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Unknown)]
    [InlineData(null)]
    public void Evaluate_NonFixedVolume_Unknown(DriveType? type)
    {
        _settings.VolumeDriveType = type;

        Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(null));
        Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(1));
        Assert.Null(_settings.AskedGuid);
    }

    [Fact(DisplayName = "P-RB-01: SendToRecycleBin refuses a path on a non-fixed volume without calling the shell")]
    public void SendToRecycleBin_NonFixedVolume_RefusedWithoutShell()
    {
        _settings.VolumeDriveType = DriveType.Removable;
        var bin = new WindowsRecycleBin(null, _settings, _ => throw new InvalidOperationException("shell must not be called"));

        Assert.Throws<IOException>(() => bin.SendToRecycleBin(Photo));
    }

    [Fact(DisplayName = "A file larger than the volume's maximum bin size is too large")]
    public void Evaluate_FileLargerThanMaxCapacity_TooLarge()
    {
        _settings.Volume = new(0, 100);

        Assert.Equal(RecycleCapacityVerdict.TooLarge, Evaluate(150 * MiB));
    }

    [Theory(DisplayName = "The safety margin below the maximum bin size is refused, just below it fits")]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Evaluate_AtSafetyMargin_Boundary(long extra, bool tooLarge)
    {
        _settings.Volume = new(0, 100);

        Assert.Equal(tooLarge ? RecycleCapacityVerdict.TooLarge : RecycleCapacityVerdict.Fits, Evaluate((100 * MiB) - RecycleBinCapacityGuard.SafetyMarginBytes + extra));
    }

    [Theory(DisplayName = "'Don't move files to the Recycle Bin' on the volume is refused, with or without a size")]
    [InlineData(1L)]
    [InlineData(null)]
    public void Evaluate_NukeOnDelete_BinDisabled(long? size)
    {
        _settings.Volume = new(1, 10_240);

        Assert.Equal(RecycleCapacityVerdict.BinDisabled, Evaluate(size));
    }

    [Fact(DisplayName = "The NoRecycleFiles policy is refused even when the volume's bin is on")]
    public void Evaluate_NoRecycleFilesPolicy_BinDisabled()
    {
        _settings.Policy = new(NoRecycleFiles: true, MaxSizePercent: null, LegacyGlobalNukeOnDelete: false);

        Assert.Equal(RecycleCapacityVerdict.BinDisabled, Evaluate(null));
    }

    [Fact(DisplayName = "A legacy global NukeOnDelete is refused")]
    public void Evaluate_LegacyGlobalNuke_BinDisabled()
    {
        _settings.Policy = new(NoRecycleFiles: false, MaxSizePercent: null, LegacyGlobalNukeOnDelete: true);

        Assert.Equal(RecycleCapacityVerdict.BinDisabled, Evaluate(1));
    }

    [Theory(DisplayName = "A volume that cannot be resolved is unknown (refused)")]
    [InlineData(null)]
    [InlineData("")]
    public void Evaluate_NoVolumeGuid_Unknown(string? volumeGuid)
    {
        _settings.VolumeGuid = volumeGuid;

        Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(1));
    }

    [Fact(DisplayName = "Any failure while reading the settings is unknown (refused), never an exception")]
    public void Evaluate_SourceThrows_Unknown()
    {
        _settings.Throw = new InvalidDataException("malformed");

        Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(1));
    }

    [Fact(DisplayName = "A zero MaxCapacity is not a valid setting: unknown (refused)")]
    public void Evaluate_ZeroMaxCapacity_Unknown()
    {
        _settings.Volume = new(0, 0);

        Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(1));
    }

    [Theory(DisplayName = "No BitBucket entry for the volume: 5% of the user's quota is the bin size")]
    [InlineData(4, false)]
    [InlineData(6, true)]
    public void Evaluate_NoVolumeEntry_UsesFivePercentOfQuota(long gigabytes, bool tooLarge)
    {
        _settings.Volume = new(null, null);

        Assert.Equal(tooLarge ? RecycleCapacityVerdict.TooLarge : RecycleCapacityVerdict.Fits, Evaluate(gigabytes * GiB));
        Assert.Equal(1, _settings.QuotaCalls);
    }

    [Fact(DisplayName = "No BitBucket entry and no volume size: unknown (refused)")]
    public void Evaluate_NoVolumeEntryNoQuota_Unknown()
    {
        _settings.Volume = new(null, null);
        _settings.QuotaBytes = null;

        Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(1));
    }

    [Theory(DisplayName = "The RecycleBinSize policy (percent of quota) caps the bin below the volume setting")]
    [InlineData(512, false)]
    [InlineData(2048, true)]
    public void Evaluate_PolicyPercent_CapsCapacity(long megabytes, bool tooLarge)
    {
        _settings.Policy = new(NoRecycleFiles: false, MaxSizePercent: 1, LegacyGlobalNukeOnDelete: false); // 1% of 100 GiB = 1 GiB
        _settings.Volume = new(0, 50_000);

        Assert.Equal(tooLarge ? RecycleCapacityVerdict.TooLarge : RecycleCapacityVerdict.Fits, Evaluate(megabytes * MiB));
    }

    [Fact(DisplayName = "A negative size is unknown (refused)")]
    public void Evaluate_NegativeSize_Unknown() => Assert.Equal(RecycleCapacityVerdict.Unknown, Evaluate(-1));

    [Theory(DisplayName = "ExtractGuid takes the braces GUID from a volume name and rejects anything else")]
    [InlineData(@"\\?\Volume{0a089459-c105-4107-99e6-657d2fddd42b}\", "{0a089459-c105-4107-99e6-657d2fddd42b}")]
    [InlineData(@"\\?\Volume{not-a-guid}\", null)]
    [InlineData(@"C:\", null)]
    [InlineData(@"\\?\Volume{0a089459-c105-4107-99e6-657d2fddd42b", null)]
    public void ExtractGuid_ParsesVolumeName(string volumeName, string? expected) =>
        Assert.Equal(expected, WindowsRecycleBinSettingsSource.ExtractGuid(volumeName));

    [Fact(DisplayName = "Real settings (read-only): the temp folder's volume resolves and a 1-byte file fits a default-configured bin")]
    [Trait("Category", "Native")]
    public void RealSource_TempVolume_ResolvesAndSmallFileFits()
    {
        // Reads the registry and the volume mount manager only; nothing is deleted or written.
        var source = WindowsRecycleBinSettingsSource.Instance;
        var path = Path.Combine(Path.GetTempPath(), "probe.jpg");

        Assert.NotNull(source.GetVolumeGuid(path));
        Assert.True(source.GetUserQuotaBytes(path) > 0);
        Assert.Equal(RecycleCapacityVerdict.Fits, RecycleBinCapacityGuard.Evaluate(path, 1, source));
        Assert.Equal(RecycleCapacityVerdict.TooLarge, RecycleBinCapacityGuard.Evaluate(path, long.MaxValue / 2, source)); // larger than any volume
    }

    [Fact(DisplayName = "WindowsRecycleBin.FitsInRecycleBin is false for a file larger than the bin")]
    public void FitsInRecycleBin_TooLarge_False()
    {
        _settings.Volume = new(0, 100);
        var bin = new WindowsRecycleBin(null, _settings, _ => throw new InvalidOperationException("shell must not be called"));

        Assert.False(bin.FitsInRecycleBin(Photo, 200 * MiB));
        Assert.True(bin.FitsInRecycleBin(Photo, 1 * MiB));
    }

    [Fact(DisplayName = "WindowsRecycleBin.SendToRecycleBin refuses a disabled bin without calling the shell and keeps the file")]
    public void SendToRecycleBin_BinDisabled_ThrowsAndNeverCallsShell()
    {
        var file = Path.Combine(Path.GetTempPath(), "photoreview-fwin2-" + System.Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            _settings.Volume = new(1, 10_240);
            var shellCalls = 0;
            var bin = new WindowsRecycleBin(null, _settings, _ => shellCalls++);

            var ex = Assert.Throws<IOException>(() => bin.SendToRecycleBin(file));

            Assert.Equal(Tr.CoreRecycleBinCannotHold(Path.GetFileName(file)), ex.Message);
            Assert.Equal(0, shellCalls);
            Assert.True(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "WindowsRecycleBin.SendToRecycleBin with the bin on still calls the shell exactly once")]
    public void SendToRecycleBin_BinOn_CallsShellOnce()
    {
        var file = Path.Combine(Path.GetTempPath(), "photoreview-fwin2-" + System.Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var calls = new List<string>();
            // The fake "shell" deletes our own temp file; the real Recycle Bin is never involved.
            var bin = new WindowsRecycleBin(null, _settings, p => { calls.Add(p); File.Delete(p); });

            bin.SendToRecycleBin(file);

            Assert.Equal(file, Assert.Single(calls));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
