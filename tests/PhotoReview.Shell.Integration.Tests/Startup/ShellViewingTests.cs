using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-21: trình bày ảnh qua shell THẬT (HWND, ShellStartup, WARP, dữ liệu tạm): điều hướng, điều hướng dồn dập (INV-1), crossfade bật/tắt,
/// compare (chọn ô theo toạ độ), mất thiết bị giữa lúc mờ, "xoá cache" xoá cả cache GPU (INV-2). Pixel được kiểm ở Shell.Tests
/// (<c>PresentationRenderingTests</c>); ở đây là luồng và trạng thái.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellViewingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static string[] MakeFolder(string root, int count)
    {
        var files = new string[count];
        for (var i = 0; i < count; i++)
        {
            files[i] = TestJpeg.WriteTo(Path.Combine(root, "photos", $"p{i:D2}.jpg"));
        }

        return files;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null) => SpinWait.SpinUntil(condition, timeout ?? Bound);

    private static void Stop(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>Trạng thái trình bày đã lắng: ảnh chính = ảnh view model đang trỏ tới, không còn bản nâng cấp chờ, có khung mới sau đó.</summary>
    private static bool IsSettled(ShellRun run)
    {
        var main = run.Main!;
        return run.OnUi(() =>
        {
            var composer = main.Composer;
            return composer is not null && composer.Pending is null
                && main.ViewModel.CurrentImage is PixelBuffer current && ReferenceEquals(composer.Current, current)
                && composer.LastDrawn is { } shown && ReferenceEquals(shown.Pixels, current);
        });
    }

    private static string CurrentPath(ShellRun run) =>
        run.OnUi(() => run.Main!.ViewModel.Presenter.CurrentPresentedPath ?? string.Empty);

    private static Task<T> OnUiAsync<T>(ShellRun run, Func<Task<T>> body)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Main!.Dispatcher.Post(async () =>
        {
            try
            {
                done.SetResult(await body());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    [Fact]
    public async Task Navigate_EachStepShowsThatImage_AndTheDrawnBufferIsTheCurrentOne()
    {
        var root = ShellRun.NewRoot("view-nav");
        var files = MakeFolder(root, 4);
        using var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ");
        Assert.True(WaitUntil(() => IsSettled(run)));
        var shown = new List<long>();

        for (var i = 1; i < files.Length; i++)
        {
            await OnUiAsync(run, async () =>
            {
                await run.Main!.ViewModel.NextAsync();
                return true;
            }).WaitAsync(Bound);
            Assert.True(WaitUntil(() => CurrentPath(run).EndsWith($"p{i:D2}.jpg", StringComparison.OrdinalIgnoreCase)),
                $"đường dẫn hiện tại sau bước {i}: {CurrentPath(run)}");
            Assert.True(WaitUntil(() => IsSettled(run)), $"ảnh chưa lắng sau bước {i}");
            shown.Add(run.OnUi(() => run.Main!.Composer!.Current!.Id));
        }

        Assert.Equal(3, shown.Distinct().Count());        // mỗi lần điều hướng là một PixelBuffer khác
        Assert.True(run.OnUi(() => run.Main!.Stats.Total) > 3);
        Assert.Equal(0, run.OnUi(() => run.Main!.Composer!.Crossfade.StartCount));   // mặc định None: không hiệu ứng
        Stop(run);
    }

    [Fact]
    public void RapidNavigation_OneHundredSteps_AlwaysEndsOnTheImageOfTheFinalIndex()
    {
        // INV-1: điều hướng dồn dập với decode về lúc nào cũng được; ảnh cuối PHẢI là ảnh của chỉ số cuối.
        var root = ShellRun.NewRoot("view-rapid");
        var files = MakeFolder(root, 6);
        using var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        Assert.True(WaitUntil(() => IsSettled(run)));
        var rng = new Random(12345);

        for (var i = 0; i < 100; i++)
        {
            var forward = i % 7 < 4;
            run.OnUi(() =>
            {
                _ = forward ? run.Main!.ViewModel.NextAsync() : run.Main!.ViewModel.PreviousAsync();
            });
            if (rng.Next(3) == 0)
            {
                Pacing.PauseMs(rng.Next(1, 12));
            }
        }

        Assert.True(WaitUntil(() => IsSettled(run)), "trình bày không lắng sau điều hướng dồn dập");
        var (index, path, expected) = run.OnUi(() =>
        {
            var vm = run.Main!.ViewModel;
            return (vm.CurrentIndex, vm.Presenter.CurrentPresentedPath, vm.Catalog.PathAt(vm.CurrentIndex));
        });
        Assert.Equal(expected, path, StringComparer.OrdinalIgnoreCase);
        Assert.InRange(index, 0, files.Length - 1);
        Stop(run);
    }

    [Fact]
    public void RapidNavigation_WithRandomDecodeLatency_StillEndsOnTheImageOfTheFinalIndex()
    {
        // INV-1 với decode trễ ngẫu nhiên (0-45 ms theo từng lần): hoàn thành không theo thứ tự yêu cầu, ảnh cuối vẫn phải đúng.
        var root = ShellRun.NewRoot("view-latency");
        var files = MakeFolder(root, 60);
        var counter = 0;
        using var run = ShellRun.Start(root, [files[0]], configure: options =>
        {
            var inner = options.ServiceOverrides;
            return options with
            {
                ServiceOverrides = services =>
                {
                    inner?.Invoke(services);
                    var original = services.Last(d => d.ServiceType == typeof(IImageDecoderFactory));
                    services.Remove(original);
                    services.AddSingleton<IImageDecoderFactory>(sp => new DelayingDecoderFactory(
                        (IImageDecoderFactory)(original.ImplementationFactory?.Invoke(sp)
                            ?? original.ImplementationInstance
                            ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
                        () => (uint)(((uint)Interlocked.Increment(ref counter) * 2654435761UL) % 46)));
                },
            };
        });
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        Assert.True(WaitUntil(() => IsSettled(run)));
        var rng = new Random(777);

        for (var i = 0; i < 100; i++)
        {
            var forward = i % 5 < 4;
            run.OnUi(() =>
            {
                _ = forward ? run.Main!.ViewModel.NextAsync() : run.Main!.ViewModel.PreviousAsync();
            });
            Pacing.PauseMs(rng.Next(0, 9));
        }

        Assert.True(WaitUntil(() => IsSettled(run)), "trình bày không lắng sau điều hướng dồn dập có decode trễ");
        var (path, expected, drawnIsCurrent) = run.OnUi(() =>
        {
            var vm = run.Main!.ViewModel;
            return (vm.Presenter.CurrentPresentedPath, vm.Catalog.PathAt(vm.CurrentIndex),
                ReferenceEquals(run.Main!.Composer!.Current, vm.CurrentImage));
        });
        Assert.Equal(expected, path, StringComparer.OrdinalIgnoreCase);
        Assert.True(drawnIsCurrent);
        Assert.True(Volatile.Read(ref counter) > 10, "decoder trễ giả không được dùng: " + counter);
        Stop(run);
    }

    private sealed class DelayingDecoderFactory(IImageDecoderFactory inner, Func<uint> nextDelayMs) : IImageDecoderFactory
    {
        public IImageDecoder Create(DecoderBackend backend) => new DelayingDecoder(inner.Create(backend), nextDelayMs);

        public bool IsRegistered(DecoderBackend backend) => inner.IsRegistered(backend);
    }

    private sealed class DelayingDecoder(IImageDecoder inner, Func<uint> nextDelayMs) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request)
        {
            Pacing.PauseMs(nextDelayMs());
            return inner.Decode(request);
        }

        public ImageInfo ReadInfo(string path) => inner.ReadInfo(path);
    }

    [Fact]
    public async Task Crossfade_Enabled_StartsOnEveryFileChange_AndFinishesByItself()
    {
        var root = ShellRun.NewRoot("view-fade");
        var files = MakeFolder(root, 4);
        using var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        Assert.True(WaitUntil(() => IsSettled(run)));
        run.OnUi(() =>
        {
            run.Main!.ViewModel.Settings.ImageTransition = ImageTransition.Fade;
            run.Main!.ViewModel.Settings.ImageTransitionMs = 120;
        });

        for (var i = 1; i <= 2; i++)
        {
            var before = run.OnUi(() => run.Main!.Composer!.Crossfade.StartCount);
            await OnUiAsync(run, async () =>
            {
                await run.Main!.ViewModel.NextAsync();
                return true;
            }).WaitAsync(Bound);
            Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.Composer!.Crossfade.StartCount) > before),
                "hiệu ứng không bắt đầu khi chuyển ảnh");
            Assert.True(WaitUntil(() => !run.OnUi(() => run.Main!.Composer!.Crossfade.IsActive)), "hiệu ứng không tự kết thúc");
            Assert.True(WaitUntil(() => IsSettled(run)));
        }

        Assert.Equal(2, run.OnUi(() => run.Main!.Composer!.Crossfade.StartCount));
        Stop(run);
    }

    [Fact]
    public void Compare_TogglesShowsBothImages_AndClicksSelectTheCellUnderThePoint()
    {
        var root = ShellRun.NewRoot("view-compare");
        var first = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        TestJpeg.WriteTo(Path.Combine(root, "photos", "a (1).jpg"));
        using var run = ShellRun.Start(root, [first]);
        Assert.True(run.WaitForMain());
        var main = run.Main!;

        // Mở một ảnh thuộc cặp số (a.jpg + a (1).jpg) vào thẳng compare (như bản WPF).
        Assert.True(WaitUntil(() => run.OnUi(() =>
            main.ViewModel.Compare.IsVisible && main.ViewModel.Compare.LeftImage is PixelBuffer && main.ViewModel.Compare.RightImage is PixelBuffer),
            TimeSpan.FromSeconds(20)),
            "compare không hiện hai ảnh: " + run.OnUi(() =>
                $"visible={main.ViewModel.Compare.IsVisible} left={main.ViewModel.Compare.LeftImage?.GetType().Name} right={main.ViewModel.Compare.RightImage?.GetType().Name} status={main.ViewModel.StatusText} count={main.ViewModel.Catalog.Count}"));
        var framesBefore = run.OnUi(() => main.Stats.Total);
        run.OnUi(() => main.Window.Invalidate());
        Assert.True(WaitUntil(() => run.OnUi(() => main.Stats.Total) > framesBefore));

        var client = run.OnUi(() => main.Window.ClientSizeDip);
        var leftPoint = new PointD(client.Width * 0.25, client.Height / 2);
        var rightPoint = new PointD(client.Width * 0.75, client.Height / 2);
        Assert.True(run.OnUi(() => main.TrySelectCompareAt(rightPoint)));
        Assert.True(run.OnUi(() => main.ViewModel.Compare.IsRightSelected));
        Assert.True(run.OnUi(() => main.TrySelectCompareAt(leftPoint)));
        Assert.True(run.OnUi(() => main.ViewModel.Compare.IsLeftSelected));
        Assert.False(run.OnUi(() => main.TrySelectCompareAt(new PointD(2, 2))));      // lề ngoài panel: không bắt chuột

        run.OnUi(() => main.ViewModel.ToggleCompare());
        Assert.True(WaitUntil(() => run.OnUi(() => !main.ViewModel.Compare.IsVisible)));
        Assert.True(WaitUntil(() => IsSettled(run)), "thoát compare phải vẽ lại ảnh đơn");
        Assert.False(run.OnUi(() => main.TrySelectCompareAt(leftPoint)));             // panel đã ẩn
        Stop(run);
    }

    [Fact]
    public async Task DeviceLost_DuringACrossfade_RecreatesAndKeepsPresentingTheCurrentImage()
    {
        var root = ShellRun.NewRoot("view-lost");
        var files = MakeFolder(root, 3);
        using var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        Assert.True(WaitUntil(() => IsSettled(run)));
        var main = run.Main!;
        run.OnUi(() =>
        {
            main.ViewModel.Settings.ImageTransition = ImageTransition.Fade;
            main.ViewModel.Settings.ImageTransitionMs = 400;
        });
        var generation = run.OnUi(() => main.Surface!.DeviceGeneration);
        var injected = 0;
        run.OnUi(() => main.Surface!.Resources.PresentFault = hr =>
        {
            if (Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                return PhotoReview.Shell.Interop.Graphics.DxgiConstants.ErrorDeviceRemoved;
            }

            return hr;
        });

        await OnUiAsync(run, async () =>
        {
            await main.ViewModel.NextAsync();
            return true;
        }).WaitAsync(Bound);

        Assert.True(WaitUntil(() => run.OnUi(() => main.Surface!.DeviceGeneration) > generation), "thiết bị không được tạo lại");
        Assert.True(WaitUntil(() => IsSettled(run)), "sau mất thiết bị ảnh phải được vẽ lại");
        Assert.True(WaitUntil(() => !run.OnUi(() => main.Composer!.Crossfade.IsActive)));
        Assert.True(run.OnUi(() => main.ImageCache!.Contains(main.Composer!.Current!.Id)), "ảnh hiện tại phải được upload lại");
        Stop(run);
    }

    [Fact]
    public void ClearCache_AlsoClearsTheGpuCache_AndTheImageIsRedrawn()
    {
        var root = ShellRun.NewRoot("view-clear");
        var files = MakeFolder(root, 2);
        using var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        Assert.True(WaitUntil(() => IsSettled(run)));
        var main = run.Main!;
        var framesBefore = run.OnUi(() => main.Stats.Total);

        run.OnUi(() => main.ViewModel.PreviewService!.ClearCache());

        Assert.True(WaitUntil(() => run.OnUi(() => main.GpuCacheClearCount) == 1), "cache GPU không bị xoá cùng cache RAM");
        Assert.True(WaitUntil(() => run.OnUi(() => main.Stats.Total) > framesBefore), "phải vẽ lại sau khi xoá cache");
        Assert.True(WaitUntil(() => run.OnUi(() => main.ImageCache!.Contains(main.Composer!.Current!.Id))), "ảnh hiện tại phải upload lại");
        Stop(run);
    }

    // Kiểm thử đột biến thủ công WP-21 (mutB): các test dưới đây lấp những đột biến sống sót của bộ test trên. ----------------------

    /// <summary>Ảnh >= <see cref="ShellMainWindow.DeferUploadBytes"/> (16 MiB): nâng cấp cùng ảnh phải upload trước rồi mới thay.</summary>
    private static PixelBuffer BigImage() => PixelBuffer.Allocate(2304, 2304, PixelLayout.Pbgra32);

    private static ShellPresentationHub HubOf(ShellMainWindow main) =>
        (ShellPresentationHub)typeof(ShellMainWindow).GetField("_hub", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;

    /// <summary>Vẽ (RenderNow) cho tới khi có thêm một khung (khung có thể bị hoãn tối đa vài lần); trả số khung đã vẽ.</summary>
    private static long RenderUntilAFrameIsDrawn(ShellMainWindow main)
    {
        var before = main.Stats.Total;
        for (var attempt = 0; attempt < 8 && main.Stats.Total == before; attempt++)
        {
            main.RenderNow();
        }

        return main.Stats.Total - before;
    }

    private static ShellRun StartSettled(string label, int count, out ShellMainWindow main)
    {
        var root = ShellRun.NewRoot(label);
        var files = MakeFolder(root, count);
        var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ");
        Assert.True(WaitUntil(() => IsSettled(run)));
        main = run.Main!;
        return run;
    }

    private sealed class RenderedCapture : EventListener
    {
        private readonly List<(string Name, long Token)> _events = [];

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf")
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is "Rendered" or "Presented" or "RenderedFrame" && eventData.Payload is { Count: > 0 } payload)
            {
                lock (_events)
                {
                    _events.Add((eventData.EventName, Convert.ToInt64(payload[0], System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        public int CountFor(long token)
        {
            lock (_events)
            {
                return _events.Count(e => e.Token == token);
            }
        }
    }

    [Fact]
    public async Task Navigating_BackAndForth_FeedsTheDirectionToThePrefetcher()
    {
        using var run = StartSettled("view-dir", 4, out var main);
        Assert.Equal(1, run.OnUi(() => main.Prefetcher!.Direction));

        await OnUiAsync(run, async () =>
        {
            await main.ViewModel.NextAsync();
            return true;
        }).WaitAsync(Bound);
        Assert.True(WaitUntil(() => CurrentPath(run).EndsWith("p01.jpg", StringComparison.OrdinalIgnoreCase)));
        await OnUiAsync(run, async () =>
        {
            await main.ViewModel.PreviousAsync();
            return true;
        }).WaitAsync(Bound);
        Assert.True(WaitUntil(() => CurrentPath(run).EndsWith("p00.jpg", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(-1, run.OnUi(() => main.Prefetcher!.Direction));    // OnNavigationChanged phải gọi Navigated
        Stop(run);
    }

    [Fact]
    public void ClearCache_DropsEveryUnretainedGpuBitmap_NotOnlyTheCounter()
    {
        using var extra = PixelBuffer.Allocate(64, 64, PixelLayout.Pbgra32);   // trước run: giải phóng SAU khi shell đã đóng
        using var run = StartSettled("view-clear2", 2, out var main);
        run.OnUi(() => main.ImageCache!.GetOrUpload(extra));
        Assert.True(run.OnUi(() => main.ImageCache!.Contains(extra.Id)));
        var clears = run.OnUi(() => main.GpuCacheClearCount);

        run.OnUi(() => main.ViewModel.PreviewService!.ClearCache());

        Assert.True(WaitUntil(() => run.OnUi(() => main.GpuCacheClearCount) == clears + 1));
        Assert.False(run.OnUi(() => main.ImageCache!.Contains(extra.Id)), "bitmap GPU phải bị xoá cùng cache RAM (INV-2)");
        Stop(run);
    }

    [Fact]
    public async Task CurrentImage_IsRetainedOnTheGpu_WhenTheCacheIsOverBudget()
    {
        using var filler = PixelBuffer.Allocate(64, 64, PixelLayout.Pbgra32);
        using var run = StartSettled("view-retain", 3, out var main);
        await OnUiAsync(run, async () =>
        {
            await main.ViewModel.NextAsync();
            return true;
        }).WaitAsync(Bound);
        Assert.True(WaitUntil(() => CurrentPath(run).EndsWith("p01.jpg", StringComparison.OrdinalIgnoreCase)));
        Assert.True(WaitUntil(() => IsSettled(run)));

        var kept = run.OnUi(() =>
        {
            var cache = main.ImageCache!;
            var current = main.Composer!.Current!;
            var budget = cache.BudgetBytes;
            cache.BudgetBytes = 0;                    // mọi thứ không được Retain/bảo vệ đều bị đuổi
            cache.GetOrUpload(filler);
            var contained = cache.Contains(current.Id);
            cache.BudgetBytes = budget;
            return contained;
        });

        Assert.True(kept, "ảnh đang hiện phải được Retain (UpdateRetained) nên LRU không đuổi nó");
        Stop(run);
    }

    [Fact]
    public void ImageChanging_ThatIsNotAFileChange_NeverStartsACrossfade()
    {
        // Hợp đồng của view model: ImageChanging chỉ phát cho lần đổi file; handler vẫn tự canh IsFileChange (nâng cấp cùng ảnh
        // không được mờ). Gọi handler riêng tư trực tiếp vì không còn đường công khai nào phát sự kiện với false.
        using var run = StartSettled("view-changing", 2, out var main);
        run.OnUi(() =>
        {
            main.ViewModel.Settings.ImageTransition = ImageTransition.Fade;
            main.ViewModel.Settings.ImageTransitionMs = 120;
        });
        var handler = typeof(ShellMainWindow).GetMethod("OnImageChanging", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var before = run.OnUi(() => main.Composer!.Crossfade.StartCount);

        run.OnUi(() => handler.Invoke(main, [null, new ImageChangingEventArgs(false)]));
        Assert.Equal(before, run.OnUi(() => main.Composer!.Crossfade.StartCount));

        run.OnUi(() => handler.Invoke(main, [null, new ImageChangingEventArgs(true)]));   // đối chứng: đổi file thì có mờ
        Assert.Equal(before + 1, run.OnUi(() => main.Composer!.Crossfade.StartCount));
        Stop(run);
    }

    [Fact]
    public void FrameDrawnWhileAnUpgradeIsStillUploading_DoesNotCountAsTheNewImagePresented()
    {
        using var big = BigImage();
        using var capture = new RenderedCapture();
        using var run = StartSettled("view-pending", 2, out var main);
        var hub = HubOf(main);
        var token = 7_000_000 + Random.Shared.NextInt64(1, 1_000_000);

        // Một lượt UI: bản nâng cấp (cùng ảnh, lớn) vào hàng chờ upload dải; khung vẽ ngay sau đó vẫn là ảnh cũ.
        var (pendingHeld, frames) = run.OnUi(() =>
        {
            hub.SetCurrentImage(big, isFileChange: false);
            if (!ReferenceEquals(main.Composer!.Pending, big))
            {
                return (false, 0L);
            }

            hub.AddTrace(token, "preview", Stopwatch.GetTimestamp());
            var drawn = RenderUntilAFrameIsDrawn(main);
            return (ReferenceEquals(main.Composer.Pending, big), drawn);
        });

        Assert.True(pendingHeld, "tiền đề: bản nâng cấp phải còn chờ upload trong khung đó");
        Assert.True(frames > 0, "tiền đề: phải có khung được vẽ");
        Assert.Equal(0, capture.CountFor(token));            // khung chứa ảnh CŨ không được tính cho mốc của bản mới

        Assert.True(WaitUntil(() => capture.CountFor(token) == 3), "sau khi bản mới thành ảnh chính, mốc phải được phát");
        Assert.True(run.OnUi(() => ReferenceEquals(main.Composer!.Current, big)));
        Stop(run);
    }

    [Fact]
    public void AnUpgradeThatIsFullyUploadedWithoutTheCompletionEvent_IsPromotedByTheNextFrame()
    {
        using var big = BigImage();
        using var run = StartSettled("view-promote", 2, out var main);
        var hub = HubOf(main);

        var (pendingHeld, frames, promoted) = run.OnUi(() =>
        {
            hub.SetCurrentImage(big, isFileChange: false);
            if (!ReferenceEquals(main.Composer!.Pending, big))
            {
                return (false, 0L, false);
            }

            // Upload đồng bộ (không qua prefetch từng dải nên không có UploadCompleted): chỉ khung kế mới có thể chuyển nó.
            main.ImageCache!.GetOrUpload(big);
            var drawn = RenderUntilAFrameIsDrawn(main);
            return (true, drawn, ReferenceEquals(main.Composer.Current, big) && main.Composer.Pending is null);
        });

        Assert.True(pendingHeld, "tiền đề: bản nâng cấp phải vào hàng chờ");
        Assert.True(frames > 0, "tiền đề: phải có khung được vẽ");
        Assert.True(promoted, "Present phải gọi TryPromotePending trước layout");
        Stop(run);
    }

    [Fact]
    public void FirstImage_IsDrawnByThePresentationLayer_AndMarksCarryTheComposerState()
    {
        var root = ShellRun.NewRoot("view-first");
        var files = MakeFolder(root, 1);
        using var run = ShellRun.Start(root, [files[0]]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));

        var (current, pending, fadeActive, pacerDegraded) = run.OnUi(() =>
            (run.Main!.Composer!.Current, run.Main!.Composer!.Pending, run.Main!.Composer!.Crossfade.IsActive, run.Main!.Pacer.IsDegraded));

        Assert.NotNull(current);
        Assert.Equal((TestJpeg.Width, TestJpeg.Height), (current.Width, current.Height));
        Assert.Null(pending);
        Assert.False(fadeActive);
        Assert.False(pacerDegraded);
        Stop(run);
    }
}
