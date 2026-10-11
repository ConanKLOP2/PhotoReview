using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport;
using ImageSource = System.Windows.Media.ImageSource;

namespace PhotoReview.App.Tests.Composition;

/// <summary>
/// WP-09 (C-16): <see cref="SharedServiceRegistration.AddPhotoReviewShared"/> composed by a host that is NOT the WPF app (what the Win32
/// shell does in WP-20): the shared graph resolves with only the host services the card names, the clipboard and the presentation sink
/// come from the host (not from <c>new WpfClipboardService()</c> / <c>new WpfPresentationSink(...)</c> in the core), and the options
/// reach the decoders.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class SharedServiceRegistrationTests : IDisposable
{
    private readonly DataRootFixture _data = new();

    public void Dispose() => _data.Dispose();

    // ---- the host services (all WPF-free fakes) ----

    private sealed class HostUi : IUiScheduler
    {
        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class HostDialogs : IDialogService
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
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class HostFolderPicker : IFolderPicker
    {
        public string? PickFolder(string title, string? initialFolder) => null;
    }

    private sealed class HostClipboard : IClipboardService
    {
        public bool TrySetText(string text) => true;
    }

    private sealed class HostSink : IPresentationSink
    {
        public void SetCurrentImage(object? image, bool isFileChange = false) { }
        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }

    private sealed class HostSinkFactory : IPresentationSinkFactory
    {
        public int Created { get; private set; }
        public MainViewModelSinkCallbacks? Callbacks { get; private set; }

        public IPresentationSink Create(MainViewModelSinkCallbacks callbacks)
        {
            Created++;
            Callbacks = callbacks;
            return new HostSink();
        }
    }

    private sealed class RecordingObserver : IPresentationObserver
    {
        public List<string> Paths { get; } = [];
        public void OnPresented(string path) => Paths.Add(path);
    }

    private sealed class CountingCodec : IPlatformImageCodec
    {
        private int _fromPixels;
        public int FromPixelsCalls => Volatile.Read(ref _fromPixels);
        public string Name => "counting";

        public object FromPixels(PixelBuffer pixels)
        {
            Interlocked.Increment(ref _fromPixels);
            return PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec.Instance.FromPixels(pixels);
        }

        public PixelLease ToPixels(object platformImage) => PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec.Instance.ToPixels(platformImage);
    }

    private sealed class SentinelDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw new NotSupportedException("sentinel");
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException("sentinel");
    }

    private static SharedServiceOptions Options(IPlatformImageCodec? codec = null, Func<IServiceProvider, IImageDecoder>? wpf = null) =>
        new(codec ?? PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec.Instance, wpf, _ => new SentinelDecoder());

    private static ServiceCollection Host(SharedServiceOptions options, HostSinkFactory? sinks = null, bool clipboard = true,
        bool sinkFactory = true, IPresentationObserver? observer = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUiScheduler>(new HostUi());
        services.AddSingleton<IDialogService>(new HostDialogs());
        services.AddSingleton<IFolderPicker>(new HostFolderPicker());
        if (clipboard) services.AddSingleton<IClipboardService>(new HostClipboard());
        if (sinkFactory) services.AddSingleton<IPresentationSinkFactory>(sinks ?? new HostSinkFactory());
        services.AddPhotoReviewShared(options);
        if (observer is not null) services.AddSingleton(observer);
        return services;
    }

    // ---- registration contract ----

    [Fact]
    public void AddPhotoReviewShared_RejectsNullArguments_AndReturnsTheSameCollection()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddPhotoReviewShared(Options()));
        Assert.Throws<ArgumentNullException>(() => services.AddPhotoReviewShared(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddPhotoReviewShared(new SharedServiceOptions(null!, null, _ => new SentinelDecoder())));
        Assert.Throws<ArgumentNullException>(() => services.AddPhotoReviewShared(
            new SharedServiceOptions(PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec.Instance, null, null!)));
        Assert.Empty(services); // nothing was half-registered by the rejected calls
        Assert.Same(services, services.AddPhotoReviewShared(Options()));
    }

    [Fact]
    public void HostWithoutWpf_ResolvesTheSharedGraph_WithTheProductionDefaults()
    {
        using var provider = Host(Options()).BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<SettingsStore>(), provider.GetRequiredService<SettingsStore>()); // singleton
        Assert.Same(PhysicalSourceReader.Instance, provider.GetRequiredService<ISourceReader>());
        Assert.Same(NullPresentationObserver.Instance, provider.GetRequiredService<IPresentationObserver>());
        Assert.Same(FileLog.Default, provider.GetRequiredService<ILog>());
        Assert.IsType<WpfKeyNameValidator>(provider.GetRequiredService<IKeyNameValidator>());
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.NotSame(provider.GetRequiredService<PhotoReview.Core.Catalog.ReviewCatalog>(),
            provider.GetRequiredService<PhotoReview.Core.Catalog.ReviewCatalog>()); // transient
        _ = provider.GetRequiredService<IImageDecoderFactory>();
        _ = provider.GetRequiredService<ThumbnailCache>();
        _ = provider.GetRequiredService<PreviewImageService>();
        _ = provider.GetRequiredService<PhotoReview.App.Localization.LocalizationService>();
        _ = provider.GetRequiredService<RecoveryRetryService>();
        _ = provider.GetRequiredService<FileHashService>();
    }

    [Fact]
    public void MainViewModel_UsesTheHostClipboardAndTheHostSinkFactory()
    {
        var sinks = new HostSinkFactory();
        var observer = new RecordingObserver();
        var services = Host(Options(), sinks, observer: observer);
        var clipboard = new HostClipboard();
        services.AddSingleton<IClipboardService>(clipboard); // the later registration of the host wins
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsStore>().Current;
        settings.InitialViewMode = InitialViewMode.ClickZoomLevel;
        settings.ClickZoomPercent = 300;

        var vm = provider.GetRequiredService<MainViewModel>();

        Assert.Same(clipboard, vm.Clipboard);
        Assert.Equal(1, sinks.Created);
        var callbacks = sinks.Callbacks!;

        // The four sink callbacks are wired to the view model / observer / initial-view rule, not to no-ops.
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        callbacks.SetStatusText("status");
        Assert.NotEmpty(changed);
        changed.Clear();
        callbacks.SetCurrentImage(null, true);
        Assert.NotEmpty(changed);
        callbacks.OnPresented(@"C:\a\b.jpg");
        Assert.Equal([@"C:\a\b.jpg"], observer.Paths);
        callbacks.ApplyInitialViewMode();
        Assert.Equal(3.0, vm.Viewer.Zoom, 9);
    }

    [Fact]
    public void MainViewModel_WithoutAHostClipboard_FailsLoudlyInsteadOfFallingBackToTheRealOne()
    {
        using var provider = Host(Options(), clipboard: false).BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MainViewModel>());
        Assert.Contains(nameof(IClipboardService), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MainViewModel_WithoutAHostSinkFactory_FailsLoudly()
    {
        using var provider = Host(Options(), sinkFactory: false).BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MainViewModel>());
        Assert.Contains(nameof(IPresentationSinkFactory), error.Message, StringComparison.Ordinal);
    }

    // ---- options reach the decoders ----

    [Fact]
    public void Options_Codec_IsTheCodecEveryDecoderHandsItsPixelsTo()
    {
        var codec = new CountingCodec();
        using var root = new TempRoot("wp09-codec");
        var png = root.Combine("one.png");
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        using (var stream = File.Create(png))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }

        using var provider = Host(Options(codec)).BuildServiceProvider();
        provider.GetRequiredService<SettingsStore>().Current.WebpHeicSupportEnabled = false;
        var decoder = provider.GetRequiredService<IImageDecoderFactory>().Create(DecoderBackend.WicDirect);

        var decoded = decoder.Decode(new DecodeRequest(png, TargetWidth: 0));

        Assert.IsAssignableFrom<ImageSource>(decoded.PlatformImage);
        Assert.True(codec.FromPixelsCalls >= 1, "the WicDirect decoder must hand its pixels to the injected codec");
    }

    [Fact]
    public void Options_WpfFallbackDecoder_FillsTheWpfSlotAndTheWebpFallback_WhenSupplied()
    {
        var calls = 0;
        var sentinel = new SentinelDecoder();
        using var provider = Host(Options(wpf: _ =>
        {
            Interlocked.Increment(ref calls);
            return sentinel;
        })).BuildServiceProvider();

        var factory = provider.GetRequiredService<IImageDecoderFactory>();
        Assert.Equal(1, Volatile.Read(ref calls)); // the WebP/HEIC chain's WPF fallback
        Assert.True(factory.IsRegistered(DecoderBackend.Wpf));
        _ = factory.Create(DecoderBackend.Wpf);
        Assert.Equal(2, Volatile.Read(ref calls)); // the Wpf provider slot
    }

    [Fact]
    public void Options_WithoutWpfFallbackDecoder_MapsTheWpfSettingOntoWicDirect()
    {
        using var provider = Host(Options(wpf: null)).BuildServiceProvider();

        var factory = provider.GetRequiredService<IImageDecoderFactory>();

        Assert.True(factory.IsRegistered(DecoderBackend.Wpf)); // the saved "Wpf" setting still works (Win32 shell, NE-8)
        Assert.NotNull(factory.Create(DecoderBackend.Wpf));
        Assert.NotNull(factory.Create(DecoderBackend.WicDirect));
    }

    // ---- DecoderProviders / ServiceFactories seams ----

    [Fact]
    public void DecoderProviders_WithoutAWpfDecoder_ListsNoWpfProvider_AndRequiresTheCodec()
    {
        var codec = PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec.Instance;

        var without = DecoderProviders.Create(PhysicalSourceReader.Instance, codec, null, () => false);
        Assert.DoesNotContain(without, p => p.Backend == DecoderBackend.Wpf);
        Assert.Contains(without, p => p.Backend == DecoderBackend.WicDirect);

        var sentinel = new SentinelDecoder();
        var with = DecoderProviders.Create(PhysicalSourceReader.Instance, codec, () => sentinel, () => false);
        Assert.Same(sentinel, with.Single(p => p.Backend == DecoderBackend.Wpf).Factory());

        Assert.Throws<ArgumentNullException>(() => DecoderProviders.Create(PhysicalSourceReader.Instance, null!, null, () => false));
    }

    [Fact]
    public void CreateWebpHeicDecoder_WithoutAWpfFallback_IsWicDirectAlone_AndWithOneIsTheFallbackChain()
    {
        var codec = PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec.Instance;

        var alone = ServiceFactories.CreateWebpHeicDecoder(PhysicalSourceReader.Instance, codec, null, null, null);
        var chained = ServiceFactories.CreateWebpHeicDecoder(PhysicalSourceReader.Instance, codec, new SentinelDecoder(), null, null);

        Assert.IsType<PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder>(alone);
        Assert.IsType<FallbackImageDecoder>(chained);
        Assert.Throws<ArgumentNullException>(() => ServiceFactories.CreateWebpHeicDecoder(PhysicalSourceReader.Instance, null!, null, null, null));
    }

    // ---- the WPF host's own registrations (App.ConfigureServices) ----

    [Fact]
    public void WpfApp_SuppliesTheWpfHostServices_AndTheSharedGraph()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);

        Assert.Equal(typeof(WpfClipboardService), services.Last(d => d.ServiceType == typeof(IClipboardService)).ImplementationType);
        Assert.Equal(typeof(WpfPresentationSinkFactory), services.Last(d => d.ServiceType == typeof(IPresentationSinkFactory)).ImplementationType);
        Assert.Equal(typeof(WpfFolderPicker), services.Last(d => d.ServiceType == typeof(IFolderPicker)).ImplementationType);
        Assert.Equal(typeof(WpfDialogService), services.Last(d => d.ServiceType == typeof(IDialogService)).ImplementationType);
        Assert.Contains(services, d => d.ServiceType == typeof(MainViewModel)); // registered by AddPhotoReviewShared
    }

    [Fact]
    public void WpfPresentationSinkFactory_BuildsAWpfSinkThatForwardsTheCallbacks_AndKeepsTheMetrics()
    {
        var metrics = new ReviewMetrics();
        var factory = new WpfPresentationSinkFactory(metrics);
        var log = new List<string>();
        var callbacks = new MainViewModelSinkCallbacks(
            (image, change) => log.Add($"image:{image}:{change}"),
            status => log.Add("status:" + status),
            () => log.Add("initial"),
            path => log.Add("presented:" + path));

        var sink = factory.Create(callbacks);

        Assert.IsType<WpfPresentationSink>(sink);
        sink.SetCurrentImage("img", true);
        sink.SetStatusText("s");
        sink.ApplyInitialViewMode();
        sink.OnPresented("p");
        Assert.Equal(["image:img:True", "status:s", "initial", "presented:p"], log);
        var metricsField = typeof(WpfPresentationSink).GetField("_metrics", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(metrics, metricsField.GetValue(sink));
        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }
}
