using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a: danh sách trang Win32 đã làm. THÊM TRANG = tạo file <c>Pages/XxxSettingsPage.cs</c> và thêm ĐÚNG MỘT dòng vào
/// <see cref="Factories"/> (xếp theo thứ tự chữ cái để merge không xung đột). Trang chưa có thì khung hiện <see cref="PendingSettingsPage"/>
/// (giữ nguyên giá trị đã nạp). Khi mọi trang có mặt <see cref="IsComplete"/> = true và cổng Win32 nhận Settings làm mặc định.
/// </summary>
internal static class SettingsPageCatalog
{
    private static readonly Dictionary<string, Func<ISettingsPageView>> Factories = new(StringComparer.Ordinal)
    {
        ["Diagnostics"] = static () => new DiagnosticsSettingsPage(),
        ["Display"] = static () => new DisplaySettingsPage(),
        ["General"] = static () => new GeneralSettingsPage(),
        ["Shortcuts"] = static () => new ShortcutsSettingsPage(),
    };

    /// <summary>Khoá các trang đã có bản Win32.</summary>
    public static IReadOnlyCollection<string> ImplementedKeys => Factories.Keys;

    /// <summary>Khoá các trang CHƯA có (theo thứ tự điều hướng).</summary>
    public static IReadOnlyList<string> PendingKeys
        => SettingsPresenter.Pages.Select(page => page.Key).Where(key => !Factories.ContainsKey(key)).ToArray();

    /// <summary>Mọi trang đã có: cổng Win32 nhận Settings làm mặc định (không cần cờ môi trường).</summary>
    public static bool IsComplete => PendingKeys.Count == 0;

    /// <summary>Một trang cho MỖI mục của <see cref="SettingsPresenter.Pages"/>, theo thứ tự đó (trang chưa có = <see cref="PendingSettingsPage"/>).</summary>
    public static IReadOnlyList<ISettingsPageView> CreatePages()
        => SettingsPresenter.Pages
            .Select(page => Factories.TryGetValue(page.Key, out var factory) ? factory() : new PendingSettingsPage(page.Key))
            .ToArray();
}

/// <summary>Chỗ giữ chỗ của trang chưa có bản Win32: một ghi chú; không đọc/ghi giá trị nào (Save giữ nguyên cài đặt của trang đó).</summary>
internal sealed class PendingSettingsPage(string key) : SettingsPageBase
{
    public override string Key => key;

    public override void Declare(SettingsPageBuilder page) => page.Note(() => Tr.SettingsPagePending, aligned: false);

    public override void Load(SettingsFormState state)
    {
    }

    public override void Collect(SettingsFormState state)
    {
    }
}
