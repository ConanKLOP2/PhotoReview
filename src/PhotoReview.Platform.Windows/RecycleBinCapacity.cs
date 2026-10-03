using System.Runtime.InteropServices;
using Microsoft.Win32;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows;

/// <summary>What <see cref="RecycleBinCapacityGuard.Evaluate"/> concluded about recycling one file (F-WIN-2).</summary>
internal enum RecycleCapacityVerdict
{
    /// <summary>The volume's Recycle Bin is on and large enough: the shell will recycle, not delete.</summary>
    Fits,
    /// <summary>The bin is turned off for the volume (NukeOnDelete) or by policy (NoRecycleFiles).</summary>
    BinDisabled,
    /// <summary>The file is larger than the bin's maximum size (minus a safety margin): the shell would delete it permanently.</summary>
    TooLarge,
    /// <summary>The settings could not be read or resolved; treated as "would delete permanently" (fail closed).</summary>
    Unknown,
}

/// <summary>Values of <c>HKCU\...\Explorer\BitBucket\Volume\{guid}</c>; null when the value is absent.</summary>
internal readonly record struct RecycleBinVolumeSettings(long? NukeOnDelete, long? MaxCapacityMb);

/// <summary>
/// Machine/user-wide switches: the "Do not move deleted files to the Recycle Bin" policy (<c>NoRecycleFiles</c>), the
/// "Maximum allowed Recycle Bin size" policy (<c>RecycleBinSize</c>, percent of the volume quota) and the legacy global
/// <c>BitBucket\NukeOnDelete</c>. Null/false when absent.
/// </summary>
internal readonly record struct RecycleBinPolicy(bool NoRecycleFiles, long? MaxSizePercent, bool LegacyGlobalNukeOnDelete);

/// <summary>Reads the Recycle Bin settings Windows applies to a path. The seam that tests replace with a fake.</summary>
internal interface IRecycleBinSettingsSource
{
    RecycleBinPolicy ReadPolicy();

    /// <summary>The <c>{guid}</c> of the volume holding <paramref name="path"/> (the BitBucket key name), or null.</summary>
    string? GetVolumeGuid(string path);

    RecycleBinVolumeSettings ReadVolume(string volumeGuid);

    /// <summary>Size of that volume as the current user sees it (respects NTFS per-user quotas), or null.</summary>
    long? GetUserQuotaBytes(string path);
}

/// <summary>
/// F-WIN-2: decides BEFORE a recycle whether the shell will really put the file in the Recycle Bin. With
/// <c>FOF_NOCONFIRMATION</c> (what <c>FileSystem.DeleteFile(OnlyErrorDialogs, SendToRecycleBin)</c> uses) the shell deletes
/// a file PERMANENTLY without asking when the volume's bin is turned off or the file is larger than the bin's maximum size.
/// Every doubt (unreadable or malformed settings, unresolvable volume) answers <see cref="RecycleCapacityVerdict.Unknown"/>
/// so the caller refuses; a false refusal keeps the file, a false "fits" would lose it.
/// </summary>
internal static class RecycleBinCapacityGuard
{
    internal const long Mebibyte = 1024L * 1024L;

    /// <summary>
    /// The bin also stores a small <c>$I</c> record and rounds the file up to whole clusters, so a file within this margin
    /// of the maximum is treated as too large.
    /// </summary>
    internal const long SafetyMarginBytes = Mebibyte;

    /// <summary>
    /// Divisor for the bin size when the volume has no BitBucket entry yet: Windows' default is 10 % of the first 40 GB of
    /// the user's quota plus 5 % of the rest, so 5 % (1/20) of the quota is a lower bound of it.
    /// </summary>
    internal const long DefaultCapacityDivisor = 20;

    /// <param name="fileSize">The file's size; null checks only the size-independent switches (bin turned off / unknown volume).</param>
    internal static RecycleCapacityVerdict Evaluate(string path, long? fileSize, IRecycleBinSettingsSource source, ILog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            return EvaluateCore(path, fileSize, source);
        }
        catch (Exception ex)
        {
            // Fail closed: whatever went wrong, the file is not handed to the shell.
            (log ?? NullLog.Instance).Error($"Recycle Bin settings could not be read: {Path.GetFileName(path)}", ex);
            return RecycleCapacityVerdict.Unknown;
        }
    }

    private static RecycleCapacityVerdict EvaluateCore(string path, long? fileSize, IRecycleBinSettingsSource source)
    {
        var policy = source.ReadPolicy();
        if (policy.NoRecycleFiles || policy.LegacyGlobalNukeOnDelete) return RecycleCapacityVerdict.BinDisabled;

        var guid = source.GetVolumeGuid(path);
        if (string.IsNullOrEmpty(guid)) return RecycleCapacityVerdict.Unknown;

        var volume = source.ReadVolume(guid);
        if (volume.NukeOnDelete is { } nuke && nuke != 0) return RecycleCapacityVerdict.BinDisabled;

        if (fileSize is not { } size) return RecycleCapacityVerdict.Fits;
        if (size < 0) return RecycleCapacityVerdict.Unknown;

        long capacity;
        if (policy.MaxSizePercent is { } percent)
        {
            // The policy overrides the per-volume setting; if both exist the smaller one is used (conservative).
            if (source.GetUserQuotaBytes(path) is not { } quota || quota <= 0) return RecycleCapacityVerdict.Unknown;
            capacity = quota / 100 * Math.Clamp(percent, 0, 100);
            if (volume.MaxCapacityMb is { } policyVolumeMb) capacity = Math.Min(capacity, policyVolumeMb * Mebibyte);
        }
        else if (volume.MaxCapacityMb is { } megabytes)
        {
            if (megabytes <= 0) return RecycleCapacityVerdict.Unknown; // Windows' minimum is 1 MB: anything else is not a setting we understand
            capacity = megabytes * Mebibyte;
        }
        else
        {
            if (source.GetUserQuotaBytes(path) is not { } quota || quota <= 0) return RecycleCapacityVerdict.Unknown;
            capacity = quota / DefaultCapacityDivisor;
        }

        return size > capacity - SafetyMarginBytes ? RecycleCapacityVerdict.TooLarge : RecycleCapacityVerdict.Fits;
    }
}

/// <summary>
/// The real settings source: per-user registry (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket</c>),
/// the Explorer policies in HKCU and HKLM, and the volume mount manager. Registry values that are present but not a DWORD
/// throw, which <see cref="RecycleBinCapacityGuard.Evaluate"/> turns into <see cref="RecycleCapacityVerdict.Unknown"/>.
/// No file data is read.
/// </summary>
internal sealed class WindowsRecycleBinSettingsSource : IRecycleBinSettingsSource
{
    public static readonly WindowsRecycleBinSettingsSource Instance = new();

    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
    private const int MaxPathChars = 32768;

    public RecycleBinPolicy ReadPolicy()
    {
        var noRecycle = IsNonZero(ReadDword(Registry.CurrentUser, PolicyKey, "NoRecycleFiles"))
            || IsNonZero(ReadDword(Registry.LocalMachine, PolicyKey, "NoRecycleFiles"));
        var userPercent = ReadDword(Registry.CurrentUser, PolicyKey, "RecycleBinSize");
        var machinePercent = ReadDword(Registry.LocalMachine, PolicyKey, "RecycleBinSize");
        long? percent = userPercent is { } u && machinePercent is { } m ? Math.Min(u, m) : userPercent ?? machinePercent;
        var legacyNuke = IsNonZero(ReadDword(Registry.CurrentUser, BitBucketKey, "NukeOnDelete"));
        return new RecycleBinPolicy(noRecycle, percent, legacyNuke);
    }

    public string? GetVolumeGuid(string path)
    {
        var mountPoint = GetVolumeMountPoint(path);
        if (mountPoint is null) return null;
        var buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length)) return null;
        return ExtractGuid(new string(buffer).TrimEnd('\0'));
    }

    public RecycleBinVolumeSettings ReadVolume(string volumeGuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuid);
        var key = BitBucketKey + @"\Volume\" + volumeGuid;
        return new RecycleBinVolumeSettings(ReadDword(Registry.CurrentUser, key, "NukeOnDelete"), ReadDword(Registry.CurrentUser, key, "MaxCapacity"));
    }

    public long? GetUserQuotaBytes(string path)
    {
        var mountPoint = GetVolumeMountPoint(path);
        if (mountPoint is null) return null;
        if (!GetDiskFreeSpaceEx(mountPoint, out _, out ulong total, out _)) return null;
        return total is 0 or > long.MaxValue ? null : (long)total;
    }

    /// <summary>"\\?\Volume{guid}\" -> "{guid}"; anything else -> null.</summary>
    internal static string? ExtractGuid(string volumeName)
    {
        const string prefix = @"\\?\Volume{";
        if (!volumeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var close = volumeName.IndexOf('}', prefix.Length);
        if (close < 0) return null;
        var guid = volumeName.Substring(prefix.Length - 1, close - prefix.Length + 2);
        return Guid.TryParseExact(guid, "B", out _) ? guid : null;
    }

    private static string? GetVolumeMountPoint(string path)
    {
        // The mount point is a prefix of the full path (plus a trailing separator), so full length + 2 always suffices.
        var full = Path.GetFullPath(path);
        var buffer = new char[Math.Clamp(full.Length + 2, 262, MaxPathChars)];
        if (!GetVolumePathName(full, buffer, buffer.Length)) return null;
        var mountPoint = new string(buffer).TrimEnd('\0');
        return mountPoint.Length == 0 ? null : mountPoint;
    }

    private static bool IsNonZero(long? value) => value is { } v && v != 0;

    /// <summary>Null when the key or value is absent; a REG_DWORD as its unsigned value; any other type throws.</summary>
    private static long? ReadDword(RegistryKey hive, string subKey, string name)
    {
        using var key = hive.OpenSubKey(subKey, writable: false);
        var value = key?.GetValue(name);
        return value switch
        {
            null => null,
            int dword => unchecked((uint)dword),
            _ => throw new InvalidDataException($"Registry value {subKey}\\{name} is not a DWORD."),
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNameW", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
