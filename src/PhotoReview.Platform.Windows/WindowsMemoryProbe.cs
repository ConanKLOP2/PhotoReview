using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows;

public sealed class WindowsMemoryProbe : IMemoryProbe
{
    public static readonly WindowsMemoryProbe Instance = new();
    private readonly ILog _log;

    public WindowsMemoryProbe(ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref Status status);

    public bool HasHeadroom(double maximumLoad, long reserveBytes) =>
        EvaluateHeadroom(GetSnapshot(), maximumLoad, reserveBytes);

    /// <summary>
    /// Pure headroom rule (unit-testable without the P/Invoke). Fails closed on purpose (CORE-11): if the snapshot is
    /// unavailable (GlobalMemoryStatusEx failed, logged by <see cref="GetSnapshot"/>) it reports "no headroom", so
    /// preload/cache growth backs off to the conservative low-RAM behaviour instead of risking an OOM on a machine whose
    /// memory state is unknown. Load must be strictly below the limit; available bytes must reach the reserve.
    /// </summary>
    public static bool EvaluateHeadroom(MemorySnapshot? snapshot, double maximumLoad, long reserveBytes)
    {
        if (snapshot is null) return false;
        return snapshot.Value.LoadPercent < maximumLoad * 100 && snapshot.Value.AvailableBytes >= (ulong)reserveBytes;
    }

    public MemorySnapshot? GetSnapshot()
    {
        var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        if (GlobalMemoryStatusEx(ref status)) return new(status.Load, status.AvailablePhysical);
        _log.Error($"GlobalMemoryStatusEx failed: Win32Error={Marshal.GetLastWin32Error()}");
        return null;
    }
}
