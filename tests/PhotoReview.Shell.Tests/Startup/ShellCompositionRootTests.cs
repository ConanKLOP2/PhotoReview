using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Startup;

/// <summary>
/// WP-20 (C-16 phần shell): đồ thị DI của bản Win32 = <c>AddPhotoReviewShared</c> (codec <c>PixelBufferImageCodec</c>, không
/// decoder WPF) + dịch vụ host Win32. Test dựng đồ thị THẬT trên UI thread (dispatcher thật, chưa có cửa sổ).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ShellCompositionRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-comp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Win32UiDispatcher _dispatcher;
    private readonly ShellPresentationHub _hub = new();

    public ShellCompositionRootTests()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = new Win32UiDispatcher();
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ServiceProvider Build(Func<nint>? owner = null) => ShellCompositionRoot.Build(
        new ShellHostServices(_dispatcher, owner ?? (() => 0)), _hub,
        services => services.AddSingleton<IAppPaths>(new AppPaths(_root, Path.Combine(_root, "data"), isolateConfig: true)));

    [Fact]
    public void HostServices_AreTheWin32Ones()
    {
        using var provider = Build(() => 1234);

        Assert.Same(_dispatcher, provider.GetRequiredService<IUiScheduler>());
        Assert.Same(_dispatcher, provider.GetRequiredService<IUiDispatcher>());
        Assert.IsType<Win32ClipboardService>(provider.GetRequiredService<IClipboardService>());
        Assert.IsType<Win32DialogService>(provider.GetRequiredService<IDialogService>());
        Assert.IsType<ShellFolderPickerAdapter>(provider.GetRequiredService<IFolderPicker>());
        Assert.IsType<Win32PresentationSinkFactory>(provider.GetRequiredService<IPresentationSinkFactory>());
        Assert.Same(_hub, provider.GetRequiredService<ShellPresentationHub>());
    }

    [Fact]
    public void MainViewModel_ResolvesOnTheUiThread_WithTheShellSink()
    {
        using var provider = Build();

        var viewModel = provider.GetRequiredService<MainViewModel>();

        Assert.NotNull(viewModel);
        Assert.IsType<Win32PresentationSink>(viewModel.Presenter.Sink);
        // Hai MainViewModel khác nhau chia sẻ cùng dịch vụ singleton (cache, settings), không chia sẻ trạng thái.
        Assert.Same(provider.GetRequiredService<PreviewImageService>(), provider.GetRequiredService<PreviewImageService>());
    }

    [Fact]
    public void LaterFeatureRegistrations_OverrideTheDefaults()
    {
        // RegisterFeatures của WP-21 thay IPresentationSinkFactory bằng MỘT dòng đăng ký: đăng ký sau thắng.
        var custom = new CustomFactory();
        using var provider = ShellCompositionRoot.Build(new ShellHostServices(_dispatcher, () => 0), _hub, services =>
        {
            services.AddSingleton<IAppPaths>(new AppPaths(_root, Path.Combine(_root, "data"), isolateConfig: true));
            services.AddSingleton<IPresentationSinkFactory>(custom);
        });

        Assert.Same(custom, provider.GetRequiredService<IPresentationSinkFactory>());
    }

    [Fact]
    public void MissingHostServices_FailLoudlyNotWithADefault()
    {
        // AddPhotoReviewShared không có mặc định ẩn: thiếu IPresentationSinkFactory thì resolve MainViewModel ném.
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new AppPaths(_root, Path.Combine(_root, "data"), isolateConfig: true));
        services.AddSingleton<IUiScheduler>(_dispatcher);
        services.AddPhotoReviewShared(new SharedServiceOptions(
            PhotoReview.Imaging.Decoding.PixelBufferImageCodec.Instance, null,
            _ => new PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder(PhotoReview.Imaging.Decoding.PixelBufferImageCodec.Instance)));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MainViewModel>());
    }

    private sealed class CustomFactory : IPresentationSinkFactory
    {
        public IPresentationSink Create(MainViewModelSinkCallbacks callbacks) => throw new NotSupportedException();
    }
}
