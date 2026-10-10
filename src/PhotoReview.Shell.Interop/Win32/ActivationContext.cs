using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-19a: activation context (SxS) - cho test native nạp comctl32 v6 từ manifest nhúng của PhotoReview.dll trong tiến trình
// không có manifest riêng (testhost). Exe thật đã có manifest (WP-01) nên shell không cần lớp này.
internal static unsafe partial class ActivationContext
{
    public const uint FlagResourceNameValid = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct ActCtx
    {
        public uint Size;
        public uint Flags;
        public char* Source;
        public ushort ProcessorArchitecture;
        public ushort LangId;
        public char* AssemblyDirectory;
        public nint ResourceName;
        public char* ApplicationName;
        public nint Module;
    }

    /// <summary>Trả handle context, hoặc -1 (INVALID_HANDLE_VALUE) khi lỗi.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateActCtxW", SetLastError = true)]
    public static partial nint CreateActCtx(ActCtx* context);

    [LibraryImport("kernel32.dll", EntryPoint = "ActivateActCtx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ActivateActCtx(nint context, nuint* cookie);

    [LibraryImport("kernel32.dll", EntryPoint = "DeactivateActCtx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeactivateActCtx(uint flags, nuint cookie);

    [LibraryImport("kernel32.dll", EntryPoint = "ReleaseActCtx")]
    public static partial void ReleaseActCtx(nint context);
}
