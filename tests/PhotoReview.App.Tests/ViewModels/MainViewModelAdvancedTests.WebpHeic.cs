using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

public sealed partial class MainViewModelAdvancedTests
{
    // Q-FMT-WEBP-HEIC: like RAW support, the WebP/HEIC switch decides which files the open folder lists, so a change reloads it.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task ShowSettings_WhenWebpHeicSwitchChanges_ReloadsTheFolderListing(bool enabledBefore, int expectedAfter)
    {
        var folder = Path.Combine(_tempDir, "webp_heic_toggle_" + enabledBefore);
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png", ValidPngBytes);
        File.WriteAllBytes(Path.Combine(folder, "2.webp"), new byte[64]);
        File.WriteAllBytes(Path.Combine(folder, "3.heic"), new byte[64]);
        _settings.WebpHeicSupportEnabled = enabledBefore;

        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        await vm.FolderLoadTask;
        Assert.Equal(enabledBefore ? 3 : 1, vm.TotalFiles);

        _dialogService.SettingsResponse = true;
        _dialogService.OnShowSettings = () =>
        {
            _settings.WebpHeicSupportEnabled = !enabledBefore;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();
        await vm.FolderLoadTask;

        Assert.Equal(expectedAfter, vm.TotalFiles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenPathAsync_WebpFile_IsOpenedOnlyWhileTheWebpHeicSwitchIsOn(bool enabled)
    {
        var folder = Path.Combine(_tempDir, "webp_open_" + enabled);
        Directory.CreateDirectory(folder);
        var webp = Path.Combine(folder, "photo.webp");
        File.WriteAllBytes(webp, new byte[64]);
        _settings.WebpHeicSupportEnabled = enabled;

        var (vm, _) = CreateViewModel();
        await vm.OpenPathAsync(webp);
        if (vm.FolderLoadTask is { } load) await load;

        Assert.Equal(enabled ? 1 : 0, vm.TotalFiles);
    }
}
