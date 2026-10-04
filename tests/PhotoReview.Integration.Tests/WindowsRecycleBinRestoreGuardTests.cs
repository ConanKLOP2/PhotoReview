using System.IO;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>RV-P02: <see cref="WindowsRecycleBin.TryRestore"/> guard, driven through the shell-restore seam (never reaches the real Recycle Bin).</summary>
[Trait("Category", "Integration")]
public sealed class WindowsRecycleBinRestoreGuardTests
{
    [Fact(DisplayName = "RV-P02: TryRestore refuses a file already at the original path without any shell call (no modal replace prompt)")]
    public void TryRestore_FileExistsAtOriginalPath_ReturnsFalseWithoutShellCall()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoReviewRestore_" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, [1]);
        try
        {
            var shellCalls = 0;
            var bin = new WindowsRecycleBin(null, new NoSettings(), shellRestore: (_, _, _) => { shellCalls++; return true; });

            var restored = bin.TryRestore(path, 1, File.GetLastWriteTimeUtc(path));

            Assert.False(restored);
            Assert.Equal(0, shellCalls);
        }
        finally { File.Delete(path); }
    }

    [Fact(DisplayName = "RV-P02: TryRestore with a free original path delegates to the shell restore")]
    public void TryRestore_PathFree_DelegatesToShellRestore()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoReviewRestore_" + Guid.NewGuid().ToString("N") + ".jpg");
        var stamp = new DateTime(2026, 9, 25, 1, 2, 3, DateTimeKind.Utc);
        (string Path, long Size, DateTime Stamp)? seen = null;
        var bin = new WindowsRecycleBin(null, new NoSettings(), shellRestore: (p, size, t) => { seen = (p, size, t); return true; });

        Assert.True(bin.TryRestore(path, 7, stamp));
        Assert.Equal(path, seen!.Value.Path);
        Assert.Equal(7L, seen.Value.Size);
        Assert.Equal(stamp, seen.Value.Stamp);
    }

    [Fact(DisplayName = "R18: the restore proof compares size AND write time (a same-timestamp file of another size is not the restored photo)")]
    public void IsExpectedFile_SameTimestampDifferentSize_IsNotTheRestoredFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoReviewRestore_" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var stamp = new DateTime(2026, 9, 25, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        try
        {
            Assert.True(WindowsRecycleBin.IsExpectedFile(path, 3, stamp));
            Assert.False(WindowsRecycleBin.IsExpectedFile(path, 4, stamp));
            Assert.False(WindowsRecycleBin.IsExpectedFile(path, 3, stamp.AddSeconds(1)));
            Assert.False(WindowsRecycleBin.IsExpectedFile(path + ".missing", 3, stamp));
        }
        finally { File.Delete(path); }
    }

    private sealed class NoSettings : IRecycleBinSettingsSource
    {
        public RecycleBinPolicy ReadPolicy() => throw new InvalidOperationException("not used by TryRestore");
        public string? GetVolumeGuid(string path) => throw new InvalidOperationException("not used by TryRestore");
        public DriveType? GetVolumeDriveType(string path) => throw new InvalidOperationException("not used by TryRestore");
        public RecycleBinVolumeSettings ReadVolume(string volumeGuid) => throw new InvalidOperationException("not used by TryRestore");
        public long? GetUserQuotaBytes(string path) => throw new InvalidOperationException("not used by TryRestore");
    }
}
