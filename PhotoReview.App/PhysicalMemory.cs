using System.Runtime.InteropServices;

namespace PhotoReview.App;

internal static class PhysicalMemory
{
    internal readonly record struct MemorySnapshot(uint LoadPercent, ulong AvailableBytes);
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

    /// <param name="maximumLoad">A 0-1 fraction of physical memory load, not a percentage.</param>
    internal static bool HasHeadroom(double maximumLoad)
    {
        var snapshot = GetSnapshot();
        if (snapshot is null) return false;
        // Keep a 2 GiB emergency reserve while allowing the 32 GiB review
        // workstation to use substantially more RAM for decoded previews.
        return snapshot.Value.LoadPercent < maximumLoad * 100 && snapshot.Value.AvailableBytes >= (ulong)AppConstants.MemoryReserveBytes;
    }

    internal static MemorySnapshot? GetSnapshot()
    {
        var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        if (GlobalMemoryStatusEx(ref status)) return new(status.Load, status.AvailablePhysical);
        AppLog.Error($"GlobalMemoryStatusEx failed: Win32Error={Marshal.GetLastWin32Error()}");
        return null;
    }
}
