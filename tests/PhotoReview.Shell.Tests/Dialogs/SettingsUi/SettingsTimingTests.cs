using System.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using Xunit.Abstractions;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a (thủ công): thời gian dựng khung (khai báo trang, tạo cửa sổ + điều khiển, bố cục) trong tiến trình test (JIT, nên là cận
/// trên; số của exe R2R/AOT thật đo bằng probe riêng, xem tài liệu GĐ3a). Chạy: <c>--filter FullyQualifiedName~SettingsTimingTests</c>.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class SettingsTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void Report_OpenTimes()
    {
        OnSta(() =>
        {
            for (var round = 1; round <= 6; round++)
            {
                var clock = Stopwatch.StartNew();
                var dialog = new SettingsDialog(new AppSettings(), null, null, null, null, Options(new FakeWarnings(), new FakeFiles()));
                dialog.Presenter.ShellIntegration = new FakeShell();
                dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
                var constructed = clock.Elapsed.TotalMilliseconds;
                double opened = 0;
                ShowOnOwner(dialog, (d, _) =>
                {
                    opened = clock.Elapsed.TotalMilliseconds;
                    d.Close();
                });
                output.WriteLine($"round {round}: construct={constructed:F1} ms, ready={opened:F1} ms (create+init+layout={opened - constructed:F1} ms)");
            }

            return 0;
        });
    }
}
