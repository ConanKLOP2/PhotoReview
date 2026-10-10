using System.IO;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Audit B (mutation gap W9): <c>WindowsRecycleBin.FitsInRecycleBin</c> is the caller's preflight; it must answer false for
/// EVERY verdict other than "fits" (disabled bin, policy, non-fixed volume, unresolvable volume, unreadable settings, negative
/// size), because a false "fits" would let the shell delete a file permanently. Fakes only: no registry, no real Recycle Bin.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WindowsRecycleBinFitsVerdictTests
{
    private const string Photo = @"C:\photos\a.jpg";
    private const long MiB = RecycleBinCapacityGuard.Mebibyte;

    private sealed class StubSettings : IRecycleBinSettingsSource
    {
        public RecycleBinPolicy Policy { get; set; }
        public string? VolumeGuid { get; set; } = "{0a089459-c105-4107-99e6-657d2fddd42b}";
        public DriveType? DriveType { get; set; } = System.IO.DriveType.Fixed;
        public RecycleBinVolumeSettings Volume { get; set; } = new(0, 10_240);
        public Exception? ThrowOnPolicy { get; set; }

        public RecycleBinPolicy ReadPolicy() => ThrowOnPolicy is null ? Policy : throw ThrowOnPolicy;
        public string? GetVolumeGuid(string path) => VolumeGuid;
        public DriveType? GetVolumeDriveType(string path) => DriveType;
        public RecycleBinVolumeSettings ReadVolume(string volumeGuid) => Volume;
        public long? GetUserQuotaBytes(string path) => 100 * 1024 * MiB;
    }

    private static bool Fits(StubSettings settings, long size = 1 * MiB) =>
        new WindowsRecycleBin(null, settings, _ => throw new InvalidOperationException("shell must not be called"))
            .FitsInRecycleBin(Photo, size);

    [Fact(DisplayName = "FitsInRecycleBin is true for a fixed volume with the bin on and enough room (control)")]
    public void Fits_HealthySettings_True() => Assert.True(Fits(new StubSettings()));

    [Fact(DisplayName = "FitsInRecycleBin is false when the settings cannot be read (fail closed)")]
    public void Fits_SettingsUnreadable_False() =>
        Assert.False(Fits(new StubSettings { ThrowOnPolicy = new IOException("registry unreadable") }));

    [Fact(DisplayName = "FitsInRecycleBin is false when the volume's bin is turned off")]
    public void Fits_VolumeBinOff_False() =>
        Assert.False(Fits(new StubSettings { Volume = new(1, 10_240) }));

    [Fact(DisplayName = "FitsInRecycleBin is false under the 'do not move deleted files to the Recycle Bin' policy")]
    public void Fits_NoRecycleFilesPolicy_False() =>
        Assert.False(Fits(new StubSettings { Policy = new(NoRecycleFiles: true, MaxSizePercent: null, LegacyGlobalNukeOnDelete: false) }));

    [Theory(DisplayName = "FitsInRecycleBin is false for a volume that is not positively Fixed")]
    [InlineData(null)]
    [InlineData(System.IO.DriveType.Removable)]
    [InlineData(System.IO.DriveType.Network)]
    [InlineData(System.IO.DriveType.Unknown)]
    public void Fits_NonFixedVolume_False(DriveType? type) =>
        Assert.False(Fits(new StubSettings { DriveType = type }));

    [Fact(DisplayName = "FitsInRecycleBin is false when the volume cannot be resolved to a bin")]
    public void Fits_UnresolvableVolumeGuid_False() =>
        Assert.False(Fits(new StubSettings { VolumeGuid = null }));

    [Fact(DisplayName = "FitsInRecycleBin is false for a negative (unknown) file size")]
    public void Fits_NegativeSize_False() =>
        Assert.False(Fits(new StubSettings(), size: -1));
}
