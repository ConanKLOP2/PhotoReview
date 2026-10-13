using PhotoReview.App.Presenters;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a (thủ công): ảnh chụp cửa sổ Settings Win32 từng trang, en + vi, 100 % và 150 %, cuộn xuống cuối trang Display.
/// <c>PHOTOREVIEW_DIALOG_SNAPSHOTS=&lt;thư mục&gt; dotnet test --filter FullyQualifiedName~SettingsSnapshotTests</c>; không có biến thì bỏ qua.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class SettingsSnapshotTests
{
    private static string? OutputDirectory => Environment.GetEnvironmentVariable("PHOTOREVIEW_DIALOG_SNAPSHOTS");

    [Fact]
    public void WriteSnapshots_OfEveryPage_InEnglishAndVietnamese()
    {
        var directory = OutputDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        foreach (var (code, localizer) in new[] { ("en", TestLocalization.English), ("vi", TestLocalization.Vietnamese) })
        {
            using var scope = TestLocalization.Use(localizer);
            OnSta(() =>
            {
                using var styles = ActivateVisualStyles();
                var settings = new AppSettings { ToolbarAutoHide = true, ShowExifInfo = true, ToolbarOpacityPercent = 70 };
                foreach (var (index, name) in new[] { (0, "general"), (1, "display"), (6, "diagnostics"), (2, "mouse-pending") })
                {
                    Snap(directory, $"settings-{name}-{code}", settings, d => GoTo(d, index));
                    Snap(directory, $"settings-{name}-{code}-150", settings, d =>
                    {
                        GoTo(d, index);
                        ChangeDpi(d, 144);
                    });
                }

                Snap(directory, $"settings-display-{code}-bottom", settings, d =>
                {
                    GoTo(d, 1);
                    User32Scroll(d);
                });
                return 0;
            });
        }
    }

    private static unsafe void User32Scroll(SettingsDialog d)
        => PhotoReview.Shell.Interop.User32.SendMessage(d.Hwnd, PhotoReview.Shell.Interop.ControlConstants.WmVScroll,
            PhotoReview.Shell.Interop.ScrollConstants.SbBottom, d.ControlHandle(SettingsDialog.ScrollId));

    private static void Snap(string directory, string name, AppSettings settings, Action<SettingsDialog> before)
    {
        var dialog = new SettingsDialog(settings, null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
        dialog.Presenter.ShellIntegration = new FakeShell();
        dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
        ShowOnOwner(dialog, (d, _) =>
        {
            before((SettingsDialog)d);
            Drain(d);
            NativeDialogSnapshotTests.CaptureWindow(Path.Combine(directory, name + ".png"), d.Hwnd);
            d.Close();
        });
    }
}
