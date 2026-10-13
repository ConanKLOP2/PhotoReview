using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// The real shell (HWND, composer, GPU cache, presenter) with every decode behind a gate the test opens: the preview of the
/// image the user moved ON to finishes first, the preview of the image they LEFT finishes later. The result of the old one must
/// not reach the screen: the composer's current bitmap, the one the view model points at and the one that is drawn stay the new
/// image's. Deterministic: no delay, the test waits for the presenter's own navigation tasks.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellStalePreviewTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static bool WaitUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, Bound);

    private sealed class Gates : IDisposable
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        public TaskCompletionSource Gate(string path) =>
            _gates.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Open(string path) => Gate(path).TrySetResult();

        public void Wait(string path)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Gate(path).Task.Wait(Bound);
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
            foreach (var gate in _gates.Values) gate.TrySetResult();
        }
    }

    private sealed class GatedDecoderFactory(IImageDecoderFactory inner, Gates gates) : IImageDecoderFactory
    {
        public IImageDecoder Create(DecoderBackend backend) => new GatedDecoder(inner.Create(backend), gates);

        public bool IsRegistered(DecoderBackend backend) => inner.IsRegistered(backend);
    }

    private sealed class GatedDecoder(IImageDecoder inner, Gates gates) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request)
        {
            gates.Wait(request.Path);
            return inner.Decode(request);
        }

        public ImageInfo ReadInfo(string path) => inner.ReadInfo(path);
    }

    private static bool IsSettled(ShellRun run) => run.OnUi(() =>
    {
        var main = run.Main!;
        var composer = main.Composer;
        return composer is not null && composer.Pending is null
            && main.ViewModel.CurrentImage is PixelBuffer current && ReferenceEquals(composer.Current, current)
            && composer.LastDrawn is { } shown && ReferenceEquals(shown.Pixels, current);
    });

    [Fact(DisplayName = "STALE shell: the preview of the image the user LEFT finishes after the new image was drawn: the new image stays")]
    public async Task OlderPreviewFinishingAfterTheNewOne_NeverReplacesTheDrawnImage()
    {
        var root = ShellRun.NewRoot("stale-shell");
        var files = new string[4];
        for (var i = 0; i < files.Length; i++) files[i] = TestJpeg.WriteTo(Path.Combine(root, "photos", $"s{i:D2}.jpg"));
        using var gates = new Gates();
        gates.Open(files[0]);                    // the first image is not part of the experiment
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
                    services.AddSingleton<IImageDecoderFactory>(sp => new GatedDecoderFactory(
                        (IImageDecoderFactory)(original.ImplementationFactory?.Invoke(sp)
                            ?? original.ImplementationInstance
                            ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
                        gates));
                },
            };
        });
        try
        {
            Assert.True(run.WaitForMain());
            Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
            Assert.True(WaitUntil(() => IsSettled(run)));

            // Two navigations in a row: s01, then s02. Their decodes are both pending behind closed gates.
            var toFirst = run.OnUi(() => run.Main!.ViewModel.NextAsync());
            var toSecond = run.OnUi(() => run.Main!.ViewModel.NextAsync());

            gates.Open(files[2]);                // the NEW image finishes first and is drawn
            await toSecond.WaitAsync(Bound);
            Assert.True(WaitUntil(() => IsSettled(run)));
            var (shownId, shownPath) = run.OnUi(() =>
                (run.Main!.Composer!.Current!.Id, run.Main!.ViewModel.Presenter.CurrentPresentedPath));
            Assert.EndsWith("s02.jpg", shownPath, StringComparison.OrdinalIgnoreCase);

            gates.Open(files[1]);                // the OLD image finishes late
            await toFirst.WaitAsync(Bound);
            Assert.True(WaitUntil(() => IsSettled(run)));

            var after = run.OnUi(() => (run.Main!.Composer!.Current!.Id, run.Main!.Composer!.LastDrawn!.Value.Pixels.Id,
                run.Main!.ViewModel.Presenter.CurrentPresentedPath, run.Main!.ViewModel.CurrentIndex));
            Assert.Equal(shownId, after.Item1);
            Assert.Equal(shownId, after.Item2);
            Assert.EndsWith("s02.jpg", after.Item3, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, after.Item4);
        }
        finally
        {
            gates.Dispose();
            run.RequestClose();
            Assert.True(run.WaitForExit(Bound), "the shell did not exit after WM_CLOSE: " + run.Diagnostics);
        }

        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }
}
