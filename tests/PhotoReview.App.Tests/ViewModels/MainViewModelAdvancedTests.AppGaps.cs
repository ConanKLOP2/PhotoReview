using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Stryker gap pins (round 3) for <see cref="MainViewModel"/>: fallbacks between the presenter, the catalog and the session.</summary>
public sealed partial class MainViewModelAdvancedTests
{
    // ---- external editor target ----

    [Fact]
    public async Task OpenInExternalEditor_WithoutASelection_UsesThePresentedPhotoEvenWhenTheCatalogMovedOn()
    {
        var a = CreateImageFile(_tempDir, "ed_presented_a.png");
        var b = CreateImageFile(_tempDir, "ed_presented_b.png");
        _catalog.Reset([a, b]);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);
        _catalog.SetCurrent(1); // the catalog moved, the presenter still shows a
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        string? launched = null;
        vm.StartExternalEditor = (_, filePath) => launched = filePath;

        vm.OpenInExternalEditor();

        Assert.Equal(a, launched);
    }

    [Fact]
    public void OpenInExternalEditor_WhenNothingIsPresentedYet_FallsBackToTheCatalogCurrent()
    {
        var a = CreateImageFile(_tempDir, "ed_catalog_a.png");
        _catalog.Reset([a]);
        var (vm, _) = CreateViewModel();
        _catalog.SetCurrent(0);
        _settings.ExternalEditorPath = @"C:\Tools\editor.exe";
        string? launched = null;
        vm.StartExternalEditor = (_, filePath) => launched = filePath;

        vm.OpenInExternalEditor();

        Assert.Equal(a, launched);
    }

    // ---- start folder of the dialogs: session folder first, else the catalog folder, else none ----

    [Fact]
    public async Task PickAndOpenFolderAsync_PrefersTheSessionFolderOverTheCatalogFolder()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("pick_session_first");
        var other = Path.Combine(_tempDir, "pick_session_first_other");
        Directory.CreateDirectory(other);
        _catalog.Reset([CreateImageFile(other, "x.png")]);
        _catalog.SetCurrent(0);
        _dialogService.PickedFolderResponse = null;

        await vm.PickAndOpenFolderAsync();

        Assert.Equal(folder, _dialogService.LastPickInitialFolder);
    }

    [Fact]
    public async Task PickAndOpenFolderAsync_WithoutASession_StartsInTheCatalogFolder()
    {
        var folder = Path.Combine(_tempDir, "pick_catalog_only");
        Directory.CreateDirectory(folder);
        _catalog.Reset([CreateImageFile(folder, "x.png")]);
        var (vm, _) = CreateViewModel();
        _catalog.SetCurrent(0);
        _dialogService.PickedFolderResponse = null;

        await vm.PickAndOpenFolderAsync();

        Assert.Equal(folder, _dialogService.LastPickInitialFolder);
    }

    [Fact]
    public async Task PickAndOpenFolderAsync_WithNothingOpen_StartsNowhere()
    {
        var (vm, _) = CreateViewModel();
        _dialogService.PickedFolderResponse = null;

        await vm.PickAndOpenFolderAsync();

        Assert.Null(_dialogService.LastPickInitialFolder);
    }

    [Fact]
    public async Task ShowBenchmark_PrefersTheSessionFolderOverTheCatalogFolder()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("bench_session_first");
        var other = Path.Combine(_tempDir, "bench_session_first_other");
        Directory.CreateDirectory(other);
        _catalog.Reset([CreateImageFile(other, "x.png")]);
        _catalog.SetCurrent(0);

        vm.ShowBenchmark();

        Assert.Equal(folder, _dialogService.LastBenchmarkFolder);
    }

    [Fact]
    public void ShowBenchmark_WithoutASession_UsesTheCatalogFolder_AndNothingWithoutACatalog()
    {
        var (vm, _) = CreateViewModel();
        vm.ShowBenchmark();
        Assert.Null(_dialogService.LastBenchmarkFolder);

        var folder = Path.Combine(_tempDir, "bench_catalog_only");
        Directory.CreateDirectory(folder);
        _catalog.Reset([CreateImageFile(folder, "x.png")]);
        _catalog.SetCurrent(0);

        vm.ShowBenchmark();

        Assert.Equal(folder, _dialogService.LastBenchmarkFolder);
    }

    // ---- sink callbacks ----

    [Fact]
    public async Task OpeningAFolder_RunsTheResetCachesAction()
    {
        var calls = 0;
        var folder = Path.Combine(_tempDir, "reset_caches");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png");
        var (vm, _) = CreateViewModel(resetCaches: () => calls++);

        await vm.OpenFolderAsync(folder);

        Assert.True(calls >= 1);
    }

    [Fact]
    public async Task OpeningAFolder_WithNothingSkipped_DoesNotRaiseTheSkippedEntriesChange()
    {
        var folder = Path.Combine(_tempDir, "no_skipped_notify");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png");
        var (vm, _) = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.OpenFolderAsync(folder);

        Assert.DoesNotContain(nameof(MainViewModel.SkippedEntries), raised);
    }

    // ---- settings change with nothing open ----

    [Fact]
    public void ShowSettings_WithNoFolderOpen_DoesNotRePresentAnything()
    {
        var (vm, _) = CreateViewModel();
        _dialogService.OnShowSettings = () =>
        {
            _settings.LoadingMode = LoadingMode.Original;
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.Same(Task.CompletedTask, vm.SettingsRefreshTask); // nothing was scheduled
        Assert.Equal(0, _sink.PresentationCount);
    }

    // ---- skip waits for the Explorer order ----

    [Fact]
    public async Task SkipAsync_WhileTheExplorerOrderIsPending_WaitsForIt()
    {
        var folder = Path.Combine(_tempDir, "skip_waits_order");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "1.png");
        CreateImageFile(folder, "2.png");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.Gate = gate.Task;
        _settings.ImageSortMode = ImageSortMode.Name; // an Explorer-order mode, so the query is started
        _settingsStore.Save(_settings);
        var (vm, _) = CreateViewModel();

        var load = vm.OpenFolderAsync(folder, Path.Combine(folder, "1.png")); // presents the opened file; the Explorer order stays pending
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_sink.PresentationCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(_sink.PresentationCount > 0);
        var skip = vm.SkipAsync();
        await Task.Delay(200);
        Assert.False(skip.IsCompleted);

        gate.SetResult();
        await skip.WaitAsync(TimeSpan.FromSeconds(30));
        await load.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---- the default initial-view application of the composition root ----

    [Fact]
    public void ApplyInitialView_AppliesTheConfiguredModeAndClickZoomPercent()
    {
        var (vm, _) = CreateViewModel();
        _settings.InitialViewMode = InitialViewMode.ClickZoomLevel;
        _settings.ClickZoomPercent = 300;

        PhotoReview.App.Composition.MainViewModelCompositionRoot.ApplyInitialView(vm, new PhotoReview.App.Services.ViewportSizeSource(), _settingsStore);

        Assert.Equal(3.0, vm.Viewer.Zoom, 9);
    }

    [Fact]
    public void ApplyInitialView_WhenZoomIsKeptAcrossImages_LeavesTheZoomAlone()
    {
        var (vm, _) = CreateViewModel();
        vm.Viewer.SetZoom(1.5);
        _settings.InitialViewMode = InitialViewMode.Percent200;
        _settings.KeepZoomAcrossImages = true;

        PhotoReview.App.Composition.MainViewModelCompositionRoot.ApplyInitialView(vm, new PhotoReview.App.Services.ViewportSizeSource(), _settingsStore);

        Assert.Equal(1.5, vm.Viewer.Zoom, 9);
    }

    [Fact]
    public void ApplyInitialView_WithoutAViewModel_DoesNothing()
    {
        Assert.Null(Record.Exception(() => PhotoReview.App.Composition.MainViewModelCompositionRoot.ApplyInitialView(null, new PhotoReview.App.Services.ViewportSizeSource(), _settingsStore)));
    }
}
