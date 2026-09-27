using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Regression: a test window that is really closed (Save placement) must write window-placement.json under the
/// test data root, never under the user's %LOCALAPPDATA%\PhotoReview. The real file is never read or written here:
/// the assertions compare resolved paths and inspect only the temp root.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WindowPlacementIsolationTests
{
    [Fact]
    public async Task ClosingAMainWindowFromTheProductionGraph_WritesPlacementUnderTheTestDataRoot()
    {
        using var dataRoot = new DataRootFixture();
        var realFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "window-placement.json");
        var expected = AppPaths.FromEnvironment().WindowPlacementFile;

        Assert.NotEqual(realFile, expected, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(dataRoot.Path, expected, StringComparison.OrdinalIgnoreCase);

        string? windowPlacementFile = null;
        await StaTestHost.RunAsync(() =>
        {
            // Standard production composition; no SuppressWindowPlacement, so Window_Closing really saves.
            using var sp = AppHost.BuildServices(s => s.AddSingleton(ThrowingRecycleBin.Instance));
            var window = sp.GetRequiredService<MainWindow>();
            windowPlacementFile = window.PlacementFile;
            window.Show();
            window.Close();
            return Task.CompletedTask;
        });

        Assert.Equal(expected, windowPlacementFile, ignoreCase: true);
        Assert.True(File.Exists(expected), "Closing the window did not write window-placement.json under the test data root.");
    }

    [Fact]
    public void ProcessBootstrap_RedirectsAppPathsIntoATempRoot_EvenWithoutTheFixture()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview");
        var paths = AppPaths.FromEnvironment();
        foreach (var p in new[] { paths.ConfigFile, paths.WindowPlacementFile, paths.PreviewCacheDir, paths.ThumbnailCacheDir, paths.LogFile, paths.JournalFile, paths.SessionsDir })
            Assert.False(p.StartsWith(real, StringComparison.OrdinalIgnoreCase), $"{p} points into the real user data dir.");
    }
}
