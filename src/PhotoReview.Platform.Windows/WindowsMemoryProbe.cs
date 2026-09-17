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

    public bool HasHeadroom(double maximumLoad, long reserveBytes)
    {
        var snapshot = GetSnapshot();
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

    public bool IsMemoryPressureHigh() => !HasHeadroom(0.85, 2L * 1024 * 1024 * 1024);

    public long GetAvailableMemoryBytes() => (long)(GetSnapshot()?.AvailableBytes ?? 0);
}
