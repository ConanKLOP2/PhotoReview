using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-35 GĐ3b: imm32 mà ô bắt phím của trang Shortcuts cần (tắt IME trên ô + đọc mã phím thật khi IME báo VK_PROCESSKEY).
// CHỈ THÊM (file mới). LibraryImport (L-AOT), chỉ ở Shell.Interop (L-PINV).

internal static partial class Imm32
{
    /// <summary>Mã phím ảo mà WM_KEYDOWN mang khi một IME đang xử lý phím (VK_PROCESSKEY).</summary>
    public const int VkProcessKey = 0xE5;

    /// <summary>ImmAssociateContext: <paramref name="inputContext"/> = 0 gỡ IME khỏi cửa sổ (ô bắt phím không nhận chữ từ IME).</summary>
    [LibraryImport("imm32.dll", EntryPoint = "ImmAssociateContext")]
    public static partial nint ImmAssociateContext(nint hwnd, nint inputContext);

    /// <summary>ImmGetVirtualKey: mã phím thật của phím đã bị IME xử lý (WM_KEYDOWN báo VK_PROCESSKEY).</summary>
    [LibraryImport("imm32.dll", EntryPoint = "ImmGetVirtualKey")]
    public static partial uint ImmGetVirtualKey(nint hwnd);
}
