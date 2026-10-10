using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>
/// Dark mode cho menu native (NE-4 = a). Windows không công bố API này: uxtheme.dll xuất theo ordinal
/// (135 = SetPreferredAppMode, 136 = FlushMenuThemes, 133 = AllowDarkModeForWindow) từ Windows 10 1903 (build 18362).
/// Thiếu DLL/ordinal hoặc build cũ thì <see cref="TryEnable"/> trả false và menu vẫn hiện bình thường (nền sáng) - không ném lỗi.
/// </summary>
internal sealed unsafe class DarkModeMenus
{
    internal const int MinimumBuild = 18362;
    internal const int SetPreferredAppModeOrdinal = 135;
    internal const int FlushMenuThemesOrdinal = 136;
    internal const int AllowDarkModeForWindowOrdinal = 133;

    /// <summary>PreferredAppMode: 0 mặc định, 1 AllowDark (theo hệ thống), 2 ForceDark, 3 ForceLight.</summary>
    internal const int ForceDark = 2;

    private readonly Func<int, nint> _resolveOrdinal;
    private readonly int _osBuild;
    private bool _attempted;
    private bool _enabled;

    /// <param name="resolveOrdinal">Trả địa chỉ hàm xuất của uxtheme theo ordinal, hoặc 0 khi không có.</param>
    /// <param name="osBuild">Số build Windows.</param>
    internal DarkModeMenus(Func<int, nint> resolveOrdinal, int osBuild)
    {
        _resolveOrdinal = resolveOrdinal;
        _osBuild = osBuild;
    }

    /// <summary>Bản dùng uxtheme.dll thật của máy này.</summary>
    internal static DarkModeMenus CreateForCurrentSystem()
    {
        var module = Kernel32.LoadLibraryEx("uxtheme.dll", 0, Kernel32.LoadLibrarySearchSystem32);
        return new DarkModeMenus(ordinal => module == 0 ? 0 : Kernel32.GetProcAddress(module, ordinal), Environment.OSVersion.Version.Build);
    }

    /// <summary>True khi đã bật được menu tối (kết quả lần thử đầu được nhớ).</summary>
    internal bool TryEnable()
    {
        if (_attempted) return _enabled;
        _attempted = true;
        if (_osBuild < MinimumBuild) return false;

        var setMode = (delegate* unmanaged<int, int>)_resolveOrdinal(SetPreferredAppModeOrdinal);
        if (setMode is null) return false;
        setMode(ForceDark);

        var flush = (delegate* unmanaged<void>)_resolveOrdinal(FlushMenuThemesOrdinal);
        if (flush is not null) flush();
        _enabled = true;
        return true;
    }

    /// <summary>AllowDarkModeForWindow cho cửa sổ chủ của menu (tuỳ chọn; false khi thiếu hàm).</summary>
    internal bool TryAllowForWindow(nint hwnd)
    {
        if (!_enabled) return false;
        var allow = (delegate* unmanaged<nint, int, int>)_resolveOrdinal(AllowDarkModeForWindowOrdinal);
        if (allow is null) return false;
        return allow(hwnd, 1) != 0;
    }
}
