using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE: lối vào thử, CHỈ khi đặt biến môi trường <c>PHOTOREVIEW_SETTINGS_D2D</c> (hoặc tham số <c>--settings-d2d</c>): mở trang
/// Display dựng bằng MỘT bề mặt Direct2D và chạy vòng lặp thông điệp riêng, rồi thoát. Giá trị: <c>hybrid</c> = ô nhập/combobox là
/// control Win32 thật (c1); <c>light</c> = bảng màu cổ điển; cách nhau bằng dấu phẩy. Không đụng đường chạy mặc định.
/// </summary>
internal static unsafe class D2DSettingsSpike
{
    public const string EnvironmentVariable = "PHOTOREVIEW_SETTINGS_D2D";
    public const string Argument = "--settings-d2d";

    public static int? TryRun(string[] args)
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var flag = args.Any(a => string.Equals(a, Argument, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(env) && !flag)
        {
            return null;
        }

        var mode = env ?? string.Empty;
        var options = new D2DSettingsHostOptions(
            Dark: !mode.Contains("light", StringComparison.OrdinalIgnoreCase),
            EditMode: mode.Contains("hybrid", StringComparison.OrdinalIgnoreCase) ? D2DEditMode.HostedWin32 : D2DEditMode.SelfDrawn,
            QuitOnClose: true);
        var presenter = new SettingsPresenter(new AppSettings());
        var state = presenter.LoadFields();
        using var window = new D2DSettingsHost(options, () => Tr.SettingsNavDisplay, v => new D2DDisplayPage(v));
        using var uia = D2DUiaProvider.Install(window); // cây UIA (Narrator) của lối vào thử
        var page = (D2DDisplayPage)window.Page;
        page.Load(state);
        window.View.Layout(window.View.ClientWidth, window.View.ClientHeight, window.Scale);
        window.PushValuesToHosted();
        window.SyncHosted();
        window.View.ButtonInvoked += button =>
        {
            if (button.Id is "Save" or "Cancel")
            {
                User32.DestroyWindow(window.Hwnd);
            }
        };
        window.Show();
        Msg message;
        while (User32.GetMessage(&message, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(&message);
            User32.DispatchMessage(&message);
        }

        return 0;
    }
}