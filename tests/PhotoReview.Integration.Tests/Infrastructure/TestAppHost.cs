using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Integration.Tests.Infrastructure;

/// <summary>
/// AR02b: builds <see cref="MainWindow"/> for integration tests through the same
/// <see cref="AppHost.BuildServices"/> composition root production uses, layering the AR02a DI
/// seams (<see cref="IExplorerOrderProvider"/>, <see cref="IRecycleBin"/>,
/// <see cref="IPresentationObserver"/>, <see cref="IMoveOverride"/>, <see cref="IPreloadController"/>)
/// on top instead of building a second, non-DI view-model graph.
/// </summary>
/// <remarks>
/// Must be called on the STA thread (<see cref="StaTestHost.RunAsync(System.Func{Task})"/>) so the
/// <c>DispatcherUiScheduler</c> registered by <see cref="PhotoReview.App.App.ConfigureServices"/>
/// binds to that thread's dispatcher.
/// </remarks>
internal static class TestAppHost
{
    /// <summary>
    /// Builds the production service graph (with the requested test overrides), resolves
    /// <see cref="MainWindow"/> from it, and starts loading <paramref name="initialPath"/> if given.
    /// The window disposes its own <see cref="ServiceProvider"/> when closed.
    /// </summary>
    internal static MainWindow CreateMainWindow(string? initialPath, TestHostHooks? hooks = null)
    {
        var sp = AppHost.BuildServices(services =>
        {
            if (hooks?.Explorer is { } explorerOrder)
                services.AddSingleton(explorerOrder);
            // Never the real WindowsRecycleBin: a test that deletes must pass its own bin (TestHostHooks.RecycleBin).
            services.AddSingleton(hooks?.RecycleBin ?? ThrowingRecycleBin.Instance);
            if (hooks?.OnPresented is { } onPresented)
                services.AddSingleton<IPresentationObserver>(new DelegatePresentationObserver(onPresented));
            if (hooks?.MoveOverride is { } moveOverride)
                services.AddSingleton<IMoveOverride>(new DelegateMoveOverride(moveOverride));
            if (hooks?.DisablePreload == true)
                services.AddSingleton<IPreloadController>(new NoOpPreloadController());
        });

        var window = sp.GetRequiredService<MainWindow>();
        window.Closed += (_, _) => sp.Dispose();
        window.InitializeWithInitialPath(initialPath);
        return window;
    }
}

/// <summary>
/// T14a test seam, migrated by AR02b (AR02-single-composition-root.md, AR02b step 2) from the
/// App project's now-deleted <c>MainWindowTestHooks.cs</c> (removed by AR02d once the last
/// non-DI <c>MainWindow</c> constructors and <c>MainWindowHelpers.CreateTestViewModel</c> were
/// deleted): plain data describing which AR02a DI seams a given test wants overridden, consumed
/// only by <see cref="TestAppHost.CreateMainWindow"/>.
/// </summary>
internal sealed class TestHostHooks
{
    /// <summary>Alternate Explorer order provider (INV-7, INV-9).</summary>
    public IExplorerOrderProvider? Explorer { get; init; }

    /// <summary>Alternate recycle-bin implementation.</summary>
    public IRecycleBin? RecycleBin { get; init; }

    /// <summary>Raised once an image has finished presenting on screen.</summary>
    public Action<string>? OnPresented { get; init; }

    /// <summary>Overrides the physical file-move step; (source, destination).</summary>
    public Func<string, string, Task>? MoveOverride { get; init; }

    /// <summary>
    /// AR02b: tests now run the real production preload graph by default (no override
    /// registered). Set true only for a test that asserts exact read/decode counts and is
    /// destabilised by background preload racing those counts; explain why at the call site.
    /// </summary>
    public bool DisablePreload { get; init; }
}

internal sealed class DelegatePresentationObserver(Action<string> onPresented) : IPresentationObserver
{
    public void OnPresented(string path) => onPresented(path);
}

internal sealed class DelegateMoveOverride(Func<string, string, Task> moveAsync) : IMoveOverride
{
    public Task MoveAsync(string source, string destination) => moveAsync(source, destination);
}

/// <summary>
/// Default <see cref="IRecycleBin"/> of <see cref="TestAppHost.CreateMainWindow"/>: fails loudly instead of letting a test
/// send files to (or restore from) the user's real Recycle Bin.
/// </summary>
internal sealed class ThrowingRecycleBin : IRecycleBin
{
    public static readonly IRecycleBin Instance = new ThrowingRecycleBin();

    public void SendToRecycleBin(string path) =>
        throw new InvalidOperationException($"Test used the Recycle Bin without passing TestHostHooks.RecycleBin: {path}");

    public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) =>
        throw new InvalidOperationException($"Test used the Recycle Bin without passing TestHostHooks.RecycleBin: {originalPath}");
}

/// <summary>Used only when a test opts out of preload via <see cref="TestHostHooks.DisablePreload"/>.</summary>
internal sealed class NoOpPreloadController : IPreloadController
{
    public Task PreloadAroundAsync(int center) => Task.CompletedTask;
    public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
    public void Cancel() { }
    public void RemovePreloadedKeysForPath(string normalizedPath) { }
    public void ClearPreloadedKeys() { }
}
