using System.Runtime.InteropServices;

/// <summary>
/// Q-R26: this process's cumulative page-fault count (soft + hard), read per perf-session step so a navigation step
/// can show whether it touched memory the OS had trimmed or compressed. Returns -1 when the call fails.
/// </summary>
internal static class ProcessPageFaults
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    public static long Read()
    {
        var size = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
        return GetProcessMemoryInfo(GetCurrentProcess(), out var counters, size) ? counters.PageFaultCount : -1;
    }
}
