using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Stryker gap pins for <see cref="MainViewModel"/> (settings reaction, external editor, dialogs' start folders,
/// title-bar dimensions, skipped-files lifecycle, sink callbacks the controllers use). See docs/MUTATION-TESTING.md.
/// </summary>
public sealed partial class MainViewModelAdvancedTests
{
    private async Task<(MainViewModel Vm, string Folder)> OpenSingleImageFolderAsync(string name)
    {
        var folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png");
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        return (vm, folder);
    }

    // ---- ShowSettings: what a changed setting must reset ----

    [Fact]
    public async Task ShowSettings_WhenOnlyTheDecoderBackendChanged_CancelsPreloadAndDropsThePreloadedKeys()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("settings_backend_only");
        _preloadController.CancelCalled = false;
        _preloadController.ClearKeysCalled = false;
        _dialogService.OnShowSettings = () =>
        {
            _settings.DecoderBackend = DecoderBackend.Wpf;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.True(_preloadController.CancelCalled);
        Assert.True(_preloadController.ClearKeysCalled);
        await vm.SettingsRefreshTask;
    }

    [Fact]
    public async Task ShowSettings_WhenOnlyTheLoadingModeChanged_KeepsThePreloadedKeys()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("settings_mode_only");
        _preloadController.CancelCalled = false;
        _preloadController.ClearKeysCalled = false;
        _dialogService.OnShowSettings = () =>
        {
            _settings.LoadingMode = LoadingMode.Original;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.True(_preloadController.CancelCalled);
        Assert.False(_preloadController.ClearKeysCalled); // the keys are only dropped for a backend change or a folder reload
        await vm.SettingsRefreshTask;
    }

    [Fact]
    public async Task ShowSettings_WhenTheZoomStepChanged_AppliesItToTheViewer()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("settings_zoomstep");
        _dialogService.OnShowSettings = () =>
        {
            _settings.KeyboardZoomStepPercent = 50;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.Equal(0.5, vm.Viewer.ZoomStep, 9);
    }

    // ---- start folders handed to dialogs ----

    [Fact]
    public async Task PickAndOpenFolderAsync_StartsThePickerInTheOpenFolder()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("picker_start");
        _dialogService.PickedFolderResponse = null;

        await vm.PickAndOpenFolderAsync();

        Assert.Equal(folder, _dialogService.LastPickInitialFolder);
    }

    [Fact]
    public async Task ShowBenchmark_PassesTheOpenFolder()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("benchmark_start");

        vm.ShowBenchmark();

        Assert.Equal(folder, _dialogService.LastBenchmarkFolder);
    }

    // ---- external editor ----

    [Fact]
    public async Task OpenInExternalEditor_WithAComparisonSelection_OpensTheSelectedPhoto()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("editor_compare");
        var other = CreateImageFile(folder, "2.png");
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        vm.Compare.SelectedPath = other;
        string? launched = null;
        vm.StartExternalEditor = (_, filePath) => launched = filePath;

        vm.OpenInExternalEditor();

        Assert.Equal(other, launched);
    }

    [Theory]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FileNotFoundException))]
    public async Task OpenInExternalEditor_WhenTheLaunchFails_ShowsTheReasonInsteadOfThrowing(Type exceptionType)
    {
        var (vm, _) = await OpenSingleImageFolderAsync("editor_launch_fails_" + exceptionType.Name);
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        vm.StartExternalEditor = (_, _) => throw (Exception)Activator.CreateInstance(exceptionType, "no such editor")!;

        vm.OpenInExternalEditor();

        var error = Assert.Single(_dialogService.Errors);
        Assert.Equal(Tr.DialogExternalEditorFailedTitle, error.Title);
        Assert.Contains("no such editor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenInExternalEditor_WhenTheLaunchThrowsSomethingUnexpected_DoesNotHideIt()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("editor_launch_unexpected");
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        vm.StartExternalEditor = (_, _) => throw new ArgumentException("bug");

        Assert.Throws<ArgumentException>(vm.OpenInExternalEditor);
        Assert.Empty(_dialogService.Errors);
    }

    // ---- info overlay persistence ----

    [Fact]
    public async Task ToggleInfoOverlay_WhenSavingTheSettingFails_StillTogglesForThisSession()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("overlay_save_fails");
        var before = vm.Settings.ShowInfoOverlay;
        var config = new AppPaths(_tempDir).ConfigFile;
        File.Delete(config);
        Directory.CreateDirectory(config); // the settings file can no longer be written

        vm.ToggleInfoOverlay();

        Assert.Equal(!before, vm.Settings.ShowInfoOverlay);
    }

    // ---- title bar: dimensions of the presented photo ----

    [Fact]
    public async Task TitleBar_WithTheDimensionsField_ShowsThePresentedPhotosSize()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("title_dimensions");
        vm.Settings = new AppSettings { LoadingMode = LoadingMode.Preview, TitleBarFields = TitleBarFields.Dimensions };

        vm.NotifyPresentationChanged();

        Assert.Contains(Tr.ExifDimensions("1", "1"), vm.FolderTitle, StringComparison.Ordinal);
    }

    // ---- skipped files ----

    [Fact]
    public void SkippedWarningText_WithNothingSkipped_IsEmpty()
    {
        var (vm, _) = CreateViewModel();

        Assert.Equal(string.Empty, vm.SkippedWarningText);
        Assert.False(vm.HasSkippedEntries);
    }

    [Fact]
    public async Task OpeningAnotherFolder_ClearsTheSkippedFilesWarningOfThePreviousOne()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("skipped_first");
        ((IFolderLoadSink)vm).OnFilesSkipped(_tempDir, [new SkippedEntry(Path.Combine(_tempDir, "x.jpg"), "locked")]);
        Assert.True(vm.HasSkippedEntries);
        Assert.Equal(Tr.MainSkippedWarning(1), vm.SkippedWarningText);
        var other = Path.Combine(_tempDir, "skipped_second");
        Directory.CreateDirectory(other);
        CreateImageFile(other, "2.png");

        await vm.OpenFolderAsync(other);

        Assert.False(vm.HasSkippedEntries);
        Assert.Equal(string.Empty, vm.SkippedWarningText);
    }

    // ---- notifications between the overlay, the viewer and the view-model ----

    [Fact]
    public void InfoOverlayRefresh_RaisesTheStatusPanelVisibilityOncePerRefresh()
    {
        var (vm, _) = CreateViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsStatusPanelVisible)) raised++; };

        vm.InfoOverlay.Refresh();

        Assert.Equal(1, raised); // only the overlay's file-info visibility is a reason to re-evaluate the panel
    }

    [Fact]
    public void ViewerZoomChange_RefreshesTheZoomHud_ButOtherViewerChangesDoNot()
    {
        var (vm, _) = CreateViewModel();
        var overlayChanges = new List<string?>();
        vm.InfoOverlay.PropertyChanged += (_, e) => overlayChanges.Add(e.PropertyName);

        vm.Viewer.IsFullscreen = true;
        Assert.Empty(overlayChanges);

        vm.Viewer.SetZoom(2.0);
        Assert.Contains(nameof(InfoOverlayViewModel.ZoomIndicatorText), overlayChanges);
    }

    // ---- sink callbacks used by the controllers ----

    [Fact]
    public async Task UpdateSessionPath_RecordsTheCurrentPathOnTheSession()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("session_path");
        var path = Path.Combine(folder, "elsewhere.png");
        var before = vm.Session!.UpdatedUtc;

        ((IFileActionSink)vm).UpdateSessionPath(path);

        Assert.Equal(path, vm.Session.CurrentPath);
        Assert.True(vm.Session.UpdatedUtc >= before);
    }

    [Fact]
    public async Task NotifyNavigationStateChanged_FromAController_RefreshesTheNavigationFlags()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("controller_notify");
        var names = new List<string?>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        ((IFileActionSink)vm).NotifyNavigationStateChanged();

        Assert.Contains(nameof(MainViewModel.CanNavigateNext), names);
        Assert.Contains(nameof(MainViewModel.HasImages), names);
        Assert.Contains(nameof(MainViewModel.TotalFiles), names);
    }

    [Fact]
    public async Task CurrentHasComparePair_ForANumberedPair_IsTrueOnBothMembersAndFalseWithoutAFolder()
    {
        var (vm, _) = CreateViewModel();
        Assert.False(vm.CurrentHasComparePair);

        var first = CreateImageFile(_tempDir, "photo.png");
        var second = CreateImageFile(_tempDir, "photo (1).png");
        _catalog.Reset([first, second]);
        await vm.Presenter.PresentAsync(0);
        Assert.True(vm.CurrentHasComparePair);

        await vm.Presenter.PresentAsync(1);
        Assert.True(vm.CurrentHasComparePair);
    }
    // ---- status line attention reasons ----

    private static void SetRawDecodeIndicator(MainViewModel vm, bool visible) =>
        typeof(ZoomDetailLoader).GetMethod("SetRawDecodeIndicatorVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm.Presenter.ZoomDetail, [visible]);

    [Fact]
    public async Task StatusNeedsAttention_IsRaisedByEachReasonOnItsOwn()
    {
        var plain = CreateImageFile(_tempDir, "attention.jpg");
        _catalog.Reset([plain]);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);
        Assert.False(vm.StatusNeedsAttention); // the plain index/size/name line is not something the user must read

        SetRawDecodeIndicator(vm, true);
        Assert.True(vm.StatusNeedsAttention);
        SetRawDecodeIndicator(vm, false);
        Assert.False(vm.StatusNeedsAttention);

        vm.StatusText = "Action failed: boom";
        Assert.True(vm.StatusNeedsAttention);
        vm.StatusText = string.Empty;
        Assert.False(vm.StatusNeedsAttention);

        ((IFolderLoadSink)vm).OnFilesSkipped(_tempDir, [new SkippedEntry(Path.Combine(_tempDir, "x.jpg"), "locked")]);
        Assert.True(vm.StatusNeedsAttention);
    }

    [Fact]
    public async Task StatusText_WithoutTheRawDecodeIndicator_ShowsThePresentersLine()
    {
        var plain = CreateImageFile(_tempDir, "plain_status.jpg");
        _catalog.Reset([plain]);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);

        Assert.Equal(vm.Presenter.StatusText, vm.StatusText);
        Assert.NotEqual(StatusFormatter.DecodingRaw(), vm.StatusText);
    }

    [Fact]
    public async Task OnUnreadableRemoved_WhenTheCurrentImageWasTheLastOneLeft_ShowsTheEmptyState()
    {
        var (vm, _) = CreateViewModel();

        await ((IFolderLoadSink)vm).OnUnreadableRemovedAsync([], currentRemoved: true);

        Assert.Equal(StatusFormatter.NoImagesRemaining(), vm.StatusText);
    }
}
