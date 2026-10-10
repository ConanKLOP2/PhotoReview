namespace PhotoReview.App.Input;

/// <summary>
/// C-06: ánh xạ giữa <see cref="KeyId"/> và mã phím ảo Win32 / tên trong config.json. Thực thi ở WP-07
/// (phải khớp <c>KeyInterop.KeyFromVirtualKey</c>/<c>VirtualKeyFromKey</c> của WPF, kiểm bằng golden G-KEY).
/// </summary>
public static class KeyIdMapping
{
    /// <summary>Khớp <c>KeyInterop.KeyFromVirtualKey</c> (WPF).</summary>
    public static KeyId FromVirtualKey(int virtualKey, bool isExtended) => throw new NotImplementedException();

    /// <summary>Khớp <c>KeyInterop.VirtualKeyFromKey</c> (WPF).</summary>
    public static int ToVirtualKey(KeyId key) => throw new NotImplementedException();

    /// <summary><c>Enum.TryParse(ignoreCase: false)</c> + <c>ShortcutKeyCanonical</c> (Core).</summary>
    public static bool TryParse(string name, out KeyId key) => throw new NotImplementedException();
}
