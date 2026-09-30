using System.IO;
using System.Reflection;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

public sealed partial class MainViewModelAdvancedTests
{
    private async Task<MainViewModel> OpenRawFolderAsync(string name, RawPairMode pairMode, bool rawSupport = true)
    {
        var folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png", ValidPngBytes);
        File.WriteAllBytes(Path.Combine(folder, "2.cr2"), new byte[64]);
        _settings.RawSupportEnabled = rawSupport;
        _settings.RawPairMode = pairMode;
        _settingsStore.Save(_settings);
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        await vm.FolderLoadTask;
        return vm;
    }

    [Fact]
    public async Task ShowSettings_WhenRawSupportToggledWithOtherChanges_StillAppliesTheSharedSettings()
    {
        var vm = await OpenRawFolderAsync("raw_toggle_shared", RawPairMode.Separate);
        Assert.NotEqual(ScalingQuality.Linear, _viewerState.ScalingQuality);

        _dialogService.OnShowSettings = () =>
        {
            _settings.RawSupportEnabled = false;
            _settings.ScalingQuality = ScalingQuality.Linear;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();
        await vm.FolderLoadTask;

        Assert.Equal(ScalingQuality.Linear, _viewerState.ScalingQuality); // was skipped by the early return
        Assert.Equal(1, vm.TotalFiles);
    }

    [Theory]
    [InlineData(RawPairMode.Separate, RawPairMode.PreferJpeg)]
    [InlineData(RawPairMode.PreferJpeg, RawPairMode.Separate)]
    public async Task ShowSettings_WhenRawPairModeChangedWhileRawIsOn_ReloadsTheFolder(RawPairMode before, RawPairMode after)
    {
        var vm = await OpenRawFolderAsync("raw_pair_reload_" + before, before);
        var loadBefore = vm.FolderLoadTask;

        _dialogService.OnShowSettings = () =>
        {
            _settings.RawPairMode = after;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.NotSame(loadBefore, vm.FolderLoadTask);
        await vm.FolderLoadTask;
    }

    [Fact]
    public async Task ShowSettings_ReloadWhileAFileActionIsInFlight_WaitsForTheActionThenReloads()
    {
        var vm = await OpenRawFolderAsync("raw_reload_gate", RawPairMode.Separate);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recycleBin.SendGate = gate.Task;
        var recycle = vm.RecycleAsync(); // the current photo goes to the bin on a pool thread, blocked on the gate
        var loadBefore = vm.FolderLoadTask;
        _dialogService.OnShowSettings = () =>
        {
            _settings.RawPairMode = RawPairMode.PreferJpeg;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.Same(loadBefore, vm.FolderLoadTask); // no reload while the action runs (it would make the action "late")
        gate.SetResult();
        await recycle;
        await vm.SettingsRefreshTask;
        Assert.NotSame(loadBefore, vm.FolderLoadTask);
        await vm.FolderLoadTask;
        Assert.Equal(1, vm.TotalFiles); // reloaded from disk after the recycle: only the RAW is left
    }

    [Fact]
    public async Task ShowSettings_ReloadFailure_IsObservedAndDoesNotFaultTheRefreshTask()
    {
        var vm = await OpenRawFolderAsync("raw_reload_fail", RawPairMode.Separate);
        vm.FolderOwnership = new ThrowingOwnership();
        _dialogService.OnShowSettings = () =>
        {
            _settings.RawPairMode = RawPairMode.PreferJpeg;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        await vm.SettingsRefreshTask; // would rethrow if the failure were left unobserved
        Assert.True(vm.SettingsRefreshTask.IsCompletedSuccessfully);
    }

    private sealed class ThrowingOwnership : PhotoReview.Core.Instance.IFolderOwnership
    {
        public Task<PhotoReview.Core.Instance.FolderOpenDecision> BeforeOpenAsync(string folder, string? initialPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ownership check failed");
        public void OnFolderShown(string folder) { }
        public void AfterOpen(string folder, string? shownFolder) { }
    }

    [Fact]
    public async Task ShowSettings_WhenRawPairModeChangedWhileRawIsOff_DoesNotReload()
    {
        var vm = await OpenRawFolderAsync("raw_pair_off", RawPairMode.Separate, rawSupport: false);
        var loadBefore = vm.FolderLoadTask;

        _dialogService.OnShowSettings = () =>
        {
            _settings.RawPairMode = RawPairMode.PreferJpeg;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.Same(loadBefore, vm.FolderLoadTask);
    }

    [Fact]
    public async Task Compare_OnCaptureThenNext_KeepsComparingTheNextCapturePair()
    {
        var jpegA = CreateImageFile(_tempDir, "a.jpg");
        var rawA = CreateImageFile(_tempDir, "a-member.jpg", DifferentPngBytes);
        var jpegB = CreateImageFile(_tempDir, "b.jpg");
        var rawB = CreateImageFile(_tempDir, "b-member.jpg", DifferentPngBytes);
        _catalog.Reset(
        [
            new CatalogEntry(jpegA) { CaptureGroup = new CaptureGroup(jpegA, rawA) },
            new CatalogEntry(jpegB) { CaptureGroup = new CaptureGroup(jpegB, rawB) },
        ], RawPairMode.Separate);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);

        vm.ToggleCompare();
        await vm.CompareToggleTask;
        Assert.Equal(rawA, vm.Compare.RightPath);

        await vm.NextAsync();

        Assert.True(vm.Compare.IsVisible);
        Assert.Equal(jpegB, vm.Compare.LeftPath); // the pair used to be dropped on navigation while Compare stayed visible
        Assert.Equal(rawB, vm.Compare.RightPath);
    }

    [Fact]
    public async Task ToggleCaptureGroupMemberAsync_WhenTheOtherMemberFailsToDecode_KeepsThePresentedPathOnTheShownMember()
    {
        var jpeg = CreateImageFile(_tempDir, "pair.jpg");
        var brokenMember = CreateImageFile(_tempDir, "pair-member.jpg", [1, 2, 3, 4]);
        _catalog.Reset([new CatalogEntry(jpeg) { CaptureGroup = new CaptureGroup(jpeg, brokenMember) }], RawPairMode.Separate);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);
        Assert.Equal(jpeg, vm.Presenter.CurrentPresentedPath);

        await vm.ToggleCaptureGroupMemberAsync();

        Assert.Equal(jpeg, vm.Presenter.CurrentPresentedPath); // the JPEG is still what is on screen
        Assert.Equal(Tr.MainCapturePairBadgeJpeg, vm.CapturePairBadge);
    }

    [Fact]
    public async Task StatusText_WhileRawDecodeIndicatorIsVisible_DoesNotHideActionStatus()
    {
        var plain = CreateImageFile(_tempDir, "plain.jpg");
        _catalog.Reset([plain]);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);
        var setIndicator = typeof(ZoomDetailLoader).GetMethod("SetRawDecodeIndicatorVisible", BindingFlags.Instance | BindingFlags.NonPublic)!;

        setIndicator.Invoke(vm.Presenter.ZoomDetail, [true]);
        Assert.Equal(StatusFormatter.DecodingRaw(), vm.StatusText); // no other status: indicator shown

        vm.StatusText = "Action failed: boom";
        Assert.Equal("Action failed: boom", vm.StatusText);
    }
}
