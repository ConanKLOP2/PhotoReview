using System.IO;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-T67: edge cases of the real (read-only) Recycle Bin settings source and of <see cref="WindowsDisplayClock"/> that the
/// fake-based <see cref="RecycleBinCapacityGuardTests"/> cannot reach. Nothing here reads or touches Recycle Bin contents.
/// </summary>
public sealed class PlatformGapTests
{
    [Fact(DisplayName = "Real source: a not-yet-existing path on the temp drive resolves to the same {guid} as the temp folder")]
    [Trait("Category", "Native")]
    public void RealSource_GetVolumeGuid_NonexistentPathOnTempDrive_ResolvesTempDriveGuid()
    {
        var source = WindowsRecycleBinSettingsSource.Instance;
        var temp = source.GetVolumeGuid(Path.Combine(Path.GetTempPath(), "probe.jpg"));
        var missing = source.GetVolumeGuid(Path.Combine(Path.GetTempPath(), "photoreview-rvt67-" + Guid.NewGuid().ToString("N"), "deep", "x.jpg"));

        Assert.NotNull(temp);
        Assert.True(Guid.TryParseExact(temp, "B", out _), $"'{temp}' is not a braces GUID");
        Assert.Equal(temp, missing);
    }

    [Fact(DisplayName = "Real source: a malformed path makes Evaluate answer Unknown instead of throwing (fail closed)")]
    [Trait("Category", "Native")]
    public void Evaluate_RealSource_MalformedPath_ReturnsUnknownWithoutThrowing()
    {
        // Path.GetFullPath rejects the NUL character inside GetVolumeGuid; Evaluate must turn that into Unknown.
        var verdict = RecycleBinCapacityGuard.Evaluate("C:\bad\0name.jpg", 1, WindowsRecycleBinSettingsSource.Instance);

        Assert.Equal(RecycleCapacityVerdict.Unknown, verdict);
    }

    [Fact(DisplayName = "Real source: a drive letter that does not exist resolves no volume and Evaluate answers Unknown")]
    [Trait("Category", "Native")]
    public void Evaluate_RealSource_UnmountedDriveLetter_ReturnsUnknown()
    {
        var mounted = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).FirstOrDefault(c => !mounted.Contains(c));
        // APP-T27: First() threw an opaque InvalidOperationException when every letter D..Z is mounted; name the real cause.
        Assert.True(free != '\0', "every drive letter D..Z is mounted on this machine, so no unmounted letter exists to probe");
        var path = $@"{free}:\photoreview-rvt67\x.jpg";
        var source = WindowsRecycleBinSettingsSource.Instance;

        Assert.Null(source.GetVolumeGuid(path));
        Assert.Null(source.GetUserQuotaBytes(path));
        Assert.Equal(RecycleCapacityVerdict.Unknown, RecycleBinCapacityGuard.Evaluate(path, 1, source));
    }

    [Fact(DisplayName = "WindowsDisplayClock.GetTiming(IntPtr.Zero) takes the DWM fallback (no window, no vblank thread)")]
    [Trait("Category", "Native")]
    public void GetTiming_ZeroHandle_ReturnsDwmFallbackTiming()
    {
        var dwm = WindowsDisplayClock.DwmTiming();
        if (dwm is null) return; // no compositor timing on this machine/session: nothing to compare against

        var timing = WindowsDisplayClock.Instance.GetTiming(IntPtr.Zero);

        Assert.NotNull(timing);
        Assert.Equal(dwm.Value.RefreshPeriod, timing.Value.RefreshPeriod);
    }
}
