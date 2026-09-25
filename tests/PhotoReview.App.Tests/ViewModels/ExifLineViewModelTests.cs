using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Metadata;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// MainViewModel's photo information line follows the presented image: the EXIF the decode carried, the field
/// settings, ShowExifInfo, and no stale EXIF from the previous image.
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // asserts Vietnamese text: must not overlap a test that switches the ambient Localizer
public sealed class ExifLineViewModelTests : IDisposable
{
    private static readonly byte[] ValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
        0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
        0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    private static readonly ExifSummary CanonExif = new()
    {
        CameraMake = "Canon",
        CameraModel = "Canon EOS R5",
        Iso = 400,
        ExposureTime = new ExifRational(1, 250),
    };

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_ExifLine_" + Guid.NewGuid().ToString("N"));
    private readonly ThumbnailCache _thumbnailCache;

    public ExifLineViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
        _thumbnailCache = new ThumbnailCache(diskDirectory: Path.Combine(_tempDir, "thumbs"), maxRamBytes: 1024 * 1024, persistNewThumbnails: false);
    }

    public void Dispose()
    {
        _thumbnailCache.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "The line shows the presented image's EXIF, honours field settings and ShowExifInfo, and never keeps the previous image's EXIF")]
    public async Task LineFollowsPresentedImageAndSettings()
    {
        var folder = Path.Combine(_tempDir, "album");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.jpg"), ValidPngBytes);
        File.WriteAllBytes(Path.Combine(folder, "b.jpg"), ValidPngBytes);
        File.WriteAllBytes(Path.Combine(folder, "c.jpg"), ValidPngBytes);      // "c.jpg" + "c (1).jpg" = a compare pair
        File.WriteAllBytes(Path.Combine(folder, "c (1).jpg"), ValidPngBytes);
        using var bGate = new ManualResetEventSlim(false);
        var vm = CreateViewModel(new ExifDecoder(path =>
        {
            if (!path.EndsWith("b.jpg", StringComparison.OrdinalIgnoreCase)) return CanonExif;
            Assert.True(bGate.Wait(TimeSpan.FromSeconds(10)));
            return null;
        }));
        vm.Settings = new AppSettings { LoadingMode = LoadingMode.Preview };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.OpenFolderAsync(folder);

        Assert.Equal("a.jpg · 6000×4000 · Canon EOS R5 · ISO 400 · 1/250 giây", vm.ExifText);
        Assert.True(vm.IsExifLineVisible);
        Assert.Contains(nameof(MainViewModel.ExifText), changed);
        Assert.Equal("Thông tin ảnh: " + vm.ExifText, vm.ExifAutomationName);

        // Integration: file info off keeps the panel for the EXIF line; the master switch (key I) hides the EXIF line too.
        vm.Settings.ShowFileInfo = false;
        vm.InfoOverlay.Refresh();
        Assert.True(vm.IsExifLineVisible);
        Assert.True(vm.IsStatusPanelVisible);
        vm.ToggleInfoOverlay();
        Assert.False(vm.Settings.ShowInfoOverlay);
        Assert.False(vm.IsExifLineVisible);
        Assert.False(vm.IsStatusPanelVisible);
        Assert.Contains(nameof(MainViewModel.IsStatusPanelVisible), changed);
        vm.ToggleInfoOverlay();
        vm.Settings.ShowFileInfo = true;
        vm.InfoOverlay.Refresh();
        Assert.True(vm.IsExifLineVisible);

        vm.Settings.ExifInfoFields = ExifInfoFields.Camera | ExifInfoFields.Iso | ExifInfoFields.ShutterSpeed;
        Assert.Equal("Canon EOS R5 · ISO 400 · 1/250 giây", vm.ExifText);

        vm.Settings.ShowExifInfo = false;
        Assert.False(vm.IsExifLineVisible);
        Assert.NotEmpty(vm.ExifText);
        vm.Settings.ShowExifInfo = true;
        vm.Settings.ExifInfoFields = ExifInfoFields.All;

        var next = vm.NextAsync(); // b.jpg: decode held until released, then decodes without EXIF
        Assert.False(next.IsCompleted);
        Assert.Equal(string.Empty, vm.ExifText); // while b loads, a's EXIF is not shown under b's status
        bGate.Set();
        await next.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("b.jpg · 6000×4000", vm.ExifText);
        Assert.DoesNotContain("Canon", vm.ExifText, StringComparison.Ordinal);

        vm.Settings.ExifInfoFields = ExifInfoFields.Camera | ExifInfoFields.Lens;
        Assert.Equal(string.Empty, vm.ExifText);
        Assert.False(vm.IsExifLineVisible); // nothing to show: hidden even though ShowExifInfo is on
        vm.Settings.ExifInfoFields = ExifInfoFields.All;

        await vm.NextAsync(); // a compare pair: two images, the compare status describes them, no single-image line

        Assert.True(vm.Compare.IsVisible || vm.CurrentImage is null);
        Assert.Equal(string.Empty, vm.ExifText);
        Assert.False(vm.IsExifLineVisible);
    }

    private MainViewModel CreateViewModel(IImageDecoder decoder)
    {
        MainViewModel? vm = null;
        var catalog = new ReviewCatalog();
        var clock = new GenerationClock();
        var metrics = new ReviewMetrics();
        var fileSystem = new PhysicalFileSystem();
        var appPaths = new AppPaths(_tempDir);
        var sessionStore = new SessionStore(appPaths, fileSystem);
        var settingsStore = new SettingsStore(appPaths, fileSystem, new NullLog());
        var compare = new CompareViewModel();
        var hashService = new FileHashService();
        var previewService = new PreviewImageService(metrics, () => false, () => new DecodeBox(1920, 1080),
            capacityBytes: 64 * 1024 * 1024, decoder: decoder, disableDiskCacheOverride: true);
        var preload = new NoPreload();

        var presenter = new ImagePresenter(catalog, clock, previewService, _thumbnailCache, preload, compare, hashService, metrics,
            () => vm!.Settings, sessionStore, new NullSink(), fileSystem, getSession: () => vm?.Session);
        var coordinator = new FolderLoadCoordinator(catalog, clock, new NoExplorerOrder(), fileSystem, sessionStore, settingsStore,
            new ForwardingFolderLoadSink(() => vm!));
        var journal = new OperationJournal(appPaths, fileSystem, new SystemClock());
        var fileActions = new FileActionService(journal, fileSystem, new SystemClock(), new NoRecycleBin());
        var undo = new UndoService(journal, fileSystem, new NoRecycleBin(), fileActions);

        vm = new MainViewModel(catalog, clock, coordinator, presenter, new ViewerState(), compare, settingsStore, sessionStore,
            fileSystem, fileActions, undo, new NoDialogs(), hashService, previewService, _thumbnailCache,
            new SessionWriter(sessionStore, new NullLog()), preloadController: preload);
        return vm;
    }

    private sealed class ExifDecoder(Func<string, ExifSummary?> exifFor) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request)
        {
            var pixels = new byte[4 * 3 * 4];
            var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgr32, null, pixels, 16);
            bitmap.Freeze();
            return new WpfDecodedImage(bitmap, downscaled: true, originalWidth: 6000, originalHeight: 4000, exif: exifFor(request.Path));
        }

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class ForwardingFolderLoadSink(Func<IFolderLoadSink> getSink) : IFolderLoadSink
    {
        public void ResetCaches() => getSink().ResetCaches();
        public void OnCatalogReady(string folder, int count) => getSink().OnCatalogReady(folder, count);
        public Task PresentAsync(int index, long presentationGeneration) => getSink().PresentAsync(index, presentationGeneration);
        public void OnEmpty(string folder) => getSink().OnEmpty(folder);
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) => getSink().OnOrderApplied(count, currentIndex, currentKept);
        public void OnFailed(string folder, Exception exception) => getSink().OnFailed(folder, exception);
    }

    private sealed class NullSink : IPresentationSink
    {
        public void SetCurrentImage(object? image) { }
        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }

    private sealed class NoPreload : IPreloadController
    {
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
#pragma warning disable CA1822 // interface members
        public void Recycle(string path) { }
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string path, long length, DateTime lastWriteUtc) => false;
        public bool IsAccessible => true;
#pragma warning restore CA1822
    }

    private sealed class NoDialogs : IDialogService
    {
        public bool ShowConfirmation(string title, string message) => false;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
    }

    private sealed class NoExplorerOrder : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }
}
