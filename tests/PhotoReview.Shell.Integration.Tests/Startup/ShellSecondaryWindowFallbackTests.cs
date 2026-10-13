using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// NOWPF-AOT-SETTINGS-FALLBACK: shell THẬT (HWND trên desktop ẩn, WARP) không có cổng cửa sổ phụ đăng ký (như bản Native
/// AOT): lệnh Settings/Diagnostics đi hết đường lệnh thật tới launcher giả và dòng trạng thái; không ném, không treo. Không
/// giả định kích thước màn hình.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellSecondaryWindowFallbackTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class RecordingLauncher : IShellLauncher
    {
        private readonly List<(string Path, string Verb)> _opens = [];

        public (string Path, string Verb)[] Opens
        {
            get { lock (_opens) { return [.. _opens]; } }
        }

        public bool TryOpenFile(string path, string verb)
        {
            lock (_opens)
            {
                _opens.Add((path, verb));
            }

            return true;
        }

        public bool TryRun(string fileName, string arguments) => false;
    }

    [Fact]
    public void SettingsAndRecovery_WithNoBridge_OpenConfigJsonAndShowTheStatusNotice()
    {
        var root = ShellRun.NewRoot("aotfallback");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var launcher = new RecordingLauncher();
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(launcher);
                services.AddSingleton<SecondaryWindowPortLoader>((_, _) => null); // mô phỏng bản AOT: cầu vắng
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ");

        run.OnUi(() => ((IContextMenuActions)run.Main!).ShowSettings());

        var opens = launcher.Opens;
        Assert.Single(opens);
        Assert.Equal(run.Paths.ConfigFile, opens[0].Path);
        Assert.Equal("open", opens[0].Verb);
        Assert.Equal(Tr.StatusSecondaryUnavailableSettings, run.OnUi(() => run.Main!.ViewModel.StatusText));

        // WP-35 GĐ1: Diagnostics đã có hộp thoại Win32 gốc nên không còn rơi về cổng tạm; Recovery thì vẫn (chưa có view Win32).
        run.OnUi(() => run.Main!.ViewModel.ShowRecovery());
        Assert.Equal(Tr.StatusSecondaryUnavailableRecovery, run.OnUi(() => run.Main!.ViewModel.StatusText));
        Assert.Single(launcher.Opens); // Recovery không chạm launcher

        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE: " + run.Diagnostics);
        Assert.Null(run.Error);
    }
}
