using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

public sealed partial class MainViewModelAdvancedTests
{
    [Fact]
    public async Task ShowSettings_ImageSortModeChanged_ReloadsTheFolderAtTheCurrentImage()
    {
        var vm = await OpenRawFolderAsync("sort_mode_reload", RawPairMode.Separate, rawSupport: false);
        var current = vm.Presenter.CurrentPresentedPath;
        Assert.NotNull(current);
        var loadBefore = vm.FolderLoadTask;
        var ownership = new RecordingOwnership();
        vm.FolderOwnership = ownership;
        _dialogService.OnShowSettings = () =>
        {
            _settings.ImageSortMode = ImageSortMode.SizeAscending;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();
        await vm.SettingsRefreshTask;

        Assert.NotSame(loadBefore, vm.FolderLoadTask); // reloaded so the new order applies to the open folder
        await vm.FolderLoadTask;
        Assert.Equal(current, ownership.InitialPath); // the current image stays the one shown
    }
}
