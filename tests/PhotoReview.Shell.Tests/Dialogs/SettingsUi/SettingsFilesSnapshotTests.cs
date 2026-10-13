using PhotoReview.App.Presenters;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Tests.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3b (thủ công): ảnh chụp trang Files của Settings Win32, en + vi, 100 % và 150 % (đầu trang và cuối trang, có đường dẫn trình soạn
/// thảo tiếng Việt + radio "an toàn khi mất điện" đã chọn). <c>PHOTOREVIEW_DIALOG_SNAPSHOTS=&lt;thư mục&gt; dotnet test --filter
/// FullyQualifiedName~SettingsFilesSnapshotTests</c>; không có biến thì bỏ qua.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class SettingsFilesSnapshotTests
{
    private static string? OutputDirectory => Environment.GetEnvironmentVariable("PHOTOREVIEW_DIALOG_SNAPSHOTS");

    [Fact]
    public void WriteSnapshots_OfTheFilesPage_InEnglishAndVietnamese()
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
                var settings = new AppSettings
                {
                    ExternalEditorPath = @"D:\Ứng dụng\Trình soạn thảo ảnh\Sửa ảnh.exe",
                    JournalDurability = JournalDurability.PowerLossSafe,
                    ConfirmBeforeDelete = true,
                    RawPairMode = RawPairMode.PreferJpeg,
                };
                Snap(directory, $"settings-files-{code}", settings, d => GoTo(d, 5));
                Snap(directory, $"settings-files-{code}-bottom", settings, d =>
                {
                    GoTo(d, 5);
                    Scroll(d);
                });
                Snap(directory, $"settings-files-{code}-150", settings, d =>
                {
                    GoTo(d, 5);
                    ChangeDpi(d, 144);
                });
                Snap(directory, $"settings-files-{code}-150-bottom", settings, d =>
                {
                    GoTo(d, 5);
                    ChangeDpi(d, 144);
                    Scroll(d);
                });
                return 0;
            });
        }
    }

    private static unsafe void Scroll(SettingsDialog d)
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
