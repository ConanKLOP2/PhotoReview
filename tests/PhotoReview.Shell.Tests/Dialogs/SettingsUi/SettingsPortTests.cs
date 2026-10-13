using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsUi.SettingsDialogKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a: cổng Win32 nhận Settings (cờ ẩn <c>PHOTOREVIEW_SETTINGS_WIN32=1</c> hoặc khi đủ mọi trang), mở <see cref="SettingsDialog"/> qua
/// DI thật tối thiểu; Dispose cổng đóng hộp thoại đang mở.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class SettingsPortTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public SettingsPortTests() => SettingsPresenter.RememberPage("General");

    public void Dispose()
    {
        SettingsPresenter.RememberPage("General");
        _data.Dispose();
    }

    private static ServiceProvider Services(SettingsStore? store)
    {
        var services = new ServiceCollection();
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void SettingsIsSupported_OnlyWhenEnabled()
    {
        OnSta(() =>
        {
            using var provider = Services(null);
            using var off = new Win32SecondaryWindowPort(provider, settingsEnabled: () => false);
            using var on = new Win32SecondaryWindowPort(provider, settingsEnabled: () => true);
            Assert.False(off.Supports(SecondaryWindowKind.Settings));
            Assert.True(on.Supports(SecondaryWindowKind.Settings));
            Assert.True(on.Supports(SecondaryWindowKind.Recovery), "WP-35 GĐ4: Recovery is a Win32 window of its own, independent of the Settings flag");
            Assert.True(off.Supports(SecondaryWindowKind.Recovery));
            return 0;
        });
    }

    [Fact]
    public void TheHiddenEnvironmentFlag_EnablesSettings_WithoutTheCatalogBeingComplete()
    {
        Assert.False(SettingsPageCatalog.IsComplete, "while pages are missing the flag is the only way in");
        var previous = Environment.GetEnvironmentVariable(Win32SecondaryWindowPort.SettingsEnvironmentVariable);
        try
        {
            OnSta(() =>
            {
                using var provider = Services(null);
                using var port = new Win32SecondaryWindowPort(provider);
                Environment.SetEnvironmentVariable(Win32SecondaryWindowPort.SettingsEnvironmentVariable, null);
                Assert.False(port.Supports(SecondaryWindowKind.Settings));
                Environment.SetEnvironmentVariable(Win32SecondaryWindowPort.SettingsEnvironmentVariable, "0");
                Assert.False(port.Supports(SecondaryWindowKind.Settings));
                Environment.SetEnvironmentVariable(Win32SecondaryWindowPort.SettingsEnvironmentVariable, "1");
                Assert.True(port.Supports(SecondaryWindowKind.Settings));
                return 0;
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(Win32SecondaryWindowPort.SettingsEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void ShowSettings_OpensTheDialog_AndReturnsTrueAfterSave()
    {
        using var temp = new TempRoot("settings-port");
        var store = NewStore(temp.Path);
        var result = OnSta(() =>
        {
            using var provider = Services(store);
            var owner = CreateOwner();
            using var port = new Win32SecondaryWindowPort(provider, ready: d =>
            {
                var dialog = (SettingsDialog)d;
                Assert.False(PhotoReview.Shell.Interop.User32.IsWindowEnabled(owner), "modal: the owner is blocked");
                var ids = ((GeneralSettingsPage)dialog.Pages[0]).Ids;
                Choose(dialog, ids.InstanceMode, 1);
                Command(dialog, SettingsDialog.SaveId);
            }, settings: Options(new FakeWarnings(), new FakeFiles()), settingsEnabled: () => true);
            var saved = port.ShowSettings(owner, SettingsTarget.Default);
            Assert.True(PhotoReview.Shell.Interop.User32.IsWindowEnabled(owner));
            DestroyOwner(owner);
            return saved;
        });

        Assert.True(result);
        Assert.Equal(InstanceMode.PerFolder, store.Current.InstanceMode);
    }

    [Fact]
    public void ShowSettings_Cancelled_ReturnsFalse_AndLeavesTheStoreAlone()
    {
        using var temp = new TempRoot("settings-port-cancel");
        var store = NewStore(temp.Path);
        var result = OnSta(() =>
        {
            using var provider = Services(store);
            using var port = new Win32SecondaryWindowPort(provider, ready: d =>
            {
                var dialog = (SettingsDialog)d;
                Choose(dialog, ((GeneralSettingsPage)dialog.Pages[0]).Ids.InstanceMode, 1);
                d.Close();
            }, settings: Options(new FakeWarnings(), new FakeFiles()), settingsEnabled: () => true);
            return port.ShowSettings(CreateOwner(), SettingsTarget.Default);
        });

        Assert.False(result);
        Assert.Equal(InstanceMode.SingleWindow, store.Current.InstanceMode);
    }

    [Fact]
    public void ShowSettings_WithoutAStore_DoesNothing()
    {
        var result = OnSta(() =>
        {
            using var provider = Services(null);
            using var port = new Win32SecondaryWindowPort(provider, ready: d => throw new InvalidOperationException("must not open"),
                settingsEnabled: () => true);
            return port.ShowSettings(0, SettingsTarget.Default);
        });

        Assert.False(result);
    }

    [Fact]
    public void ShowSettings_ForTheExternalEditorTarget_OpensTheFilesPage_WithoutRememberingIt()
    {
        using var temp = new TempRoot("settings-port-editor");
        var store = NewStore(temp.Path);
        var key = OnSta(() =>
        {
            using var provider = Services(store);
            string? opened = null;
            using var port = new Win32SecondaryWindowPort(provider, ready: d =>
            {
                opened = ((SettingsDialog)d).ActivePageKey;
                d.Close();
            }, settings: Options(new FakeWarnings(), new FakeFiles()), settingsEnabled: () => true);
            port.ShowSettings(CreateOwner(), SettingsTarget.ExternalEditor);
            return opened;
        });

        Assert.Equal("Files", key);
        Assert.Equal("General", SettingsPresenter.RememberedPageKey);
    }

    [Fact]
    public void DisposingThePort_ClosesAnOpenSettingsDialog()
    {
        using var temp = new TempRoot("settings-port-dispose");
        var store = NewStore(temp.Path);
        var closed = OnSta(() =>
        {
            using var provider = Services(store);
            Win32SecondaryWindowPort? port = null;
            var wasClosed = false;
            port = new Win32SecondaryWindowPort(provider, ready: d =>
            {
                Assert.Equal(1, port!.OpenCount);
                port.Dispose();
                wasClosed = d.IsClosed;
            }, settings: Options(new FakeWarnings(), new FakeFiles()), settingsEnabled: () => true);
            port.ShowSettings(CreateOwner(), SettingsTarget.Default);
            return wasClosed;
        });

        Assert.True(closed);
    }
}
