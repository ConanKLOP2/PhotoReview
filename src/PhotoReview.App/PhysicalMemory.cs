using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App;

internal sealed class PhysicalMemory : IMemoryProbe
{
    public static readonly PhysicalMemory Instance = new();

    // WPF codecs also allocate native memory, which GC snapshots can miss or report late.
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

    public bool HasHeadroom(double maximumLoad, long reserveBytes)
    {
        var snapshot = GetSnapshot();
        if (snapshot is null) return false;
        // Keep an emergency reserve while allowing the review workstation
        // to use substantially more RAM for decoded previews.
        return snapshot.Value.LoadPercent < maximumLoad * 100 && snapshot.Value.AvailableBytes >= (ulong)reserveBytes;
    }

    public MemorySnapshot? GetSnapshot()
    {
        var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        if (GlobalMemoryStatusEx(ref status)) return new(status.Load, status.AvailablePhysical);
        AppLog.Error($"GlobalMemoryStatusEx failed: Win32Error={Marshal.GetLastWin32Error()}");
        return null;
    }

    public bool IsMemoryPressureHigh() => !HasHeadroom(0.85, AppConstants.MemoryReserveBytes);

    public long GetAvailableMemoryBytes() => (long)(GetSnapshot()?.AvailableBytes ?? 0);

    /// <param name="maximumLoad">A 0-1 fraction of physical memory load, not a percentage.</param>
    internal static bool HasHeadroom(double maximumLoad) => Instance.HasHeadroom(maximumLoad, AppConstants.MemoryReserveBytes);
}
