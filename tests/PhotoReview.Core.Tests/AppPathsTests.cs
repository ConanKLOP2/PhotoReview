using System.IO;
using PhotoReview.Core.Abstractions;
using Xunit;

namespace PhotoReview.Core.Tests;

[Trait("Category", "HotPath")]
public class AppPathsTests
{
    private static readonly string MockLocalAppData = Path.Combine("C:", "Users", "Tester", "AppData", "Local");
    private static readonly string MockAppRoot = Path.Combine(MockLocalAppData, "PhotoReview");
    private static readonly string MockOverride = Path.Combine("D:", "IsolatedDataRoot");

    [Fact]
    public void WithoutOverrideResolvesDefaultLayout()
    {
        var paths = new AppPaths(MockLocalAppData);

        Assert.Equal(Path.Combine(MockAppRoot, "config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine(MockAppRoot, "Data", "operations.jsonl"), paths.JournalFile);
        Assert.Equal(Path.Combine(MockAppRoot, "Data", "Sessions"), paths.SessionsDir);
        Assert.Equal(Path.Combine(MockAppRoot, "logs", "app.log"), paths.LogFile);
        Assert.Equal(Path.Combine(MockAppRoot, "cache"), paths.PreviewCacheDir);
        Assert.Equal(Path.Combine(MockAppRoot, "thumbnails"), paths.ThumbnailCacheDir);
        Assert.Equal(Path.Combine(MockAppRoot, "window-placement.json"), paths.WindowPlacementFile);
    }

    [Fact]
    public void WithOverrideRedirectsJournalSessionsAndLogWhileKeepingConfigAndCache()
    {
        var paths = new AppPaths(MockLocalAppData, MockOverride);

        // ConfigFile vÃ  cache khÃ´ng bá»‹ Ä‘á»•i theo override
        Assert.Equal(Path.Combine(MockAppRoot, "config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine(MockAppRoot, "cache"), paths.PreviewCacheDir);
        Assert.Equal(Path.Combine(MockAppRoot, "thumbnails"), paths.ThumbnailCacheDir);
        Assert.Equal(Path.Combine(MockAppRoot, "window-placement.json"), paths.WindowPlacementFile);

        // JournalFile, SessionsDir, LogFile dÃ¹ng override root
        Assert.Equal(Path.Combine(MockOverride, "operations.jsonl"), paths.JournalFile);
        Assert.Equal(Path.Combine(MockOverride, "Sessions"), paths.SessionsDir);
        Assert.Equal(Path.Combine(MockOverride, "logs", "app.log"), paths.LogFile);
    }

    [Fact]
    public void WithOverrideAndIsolateConfigRedirectsConfigFile()
    {
        var paths = new AppPaths(MockLocalAppData, MockOverride, isolateConfig: true);

        Assert.Equal(Path.Combine(MockOverride, "config.json"), paths.ConfigFile);
    }

    [Fact]
    public void WithoutOverrideAndIsolateConfigLeavesConfigFileInAppRoot()
    {
        var paths = new AppPaths(MockLocalAppData, dataRootOverride: null, isolateConfig: true);

        Assert.Equal(Path.Combine(MockAppRoot, "config.json"), paths.ConfigFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ThrowsWhenLocalAppDataIsNullOrEmptyOrWhiteSpace(string? invalidLocalAppData)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(invalidLocalAppData!));
    }

    [Fact]
    public void FromEnvironmentReturnsValidInstanceWithAbsolutePaths()
    {
        var paths = AppPaths.FromEnvironment();

        Assert.NotNull(paths);
        Assert.True(Path.IsPathRooted(paths.ConfigFile));
        Assert.True(Path.IsPathRooted(paths.JournalFile));
        Assert.True(Path.IsPathRooted(paths.SessionsDir));
        Assert.True(Path.IsPathRooted(paths.LogFile));
        Assert.True(Path.IsPathRooted(paths.PreviewCacheDir));
        Assert.True(Path.IsPathRooted(paths.ThumbnailCacheDir));
        Assert.True(Path.IsPathRooted(paths.WindowPlacementFile));
    }

    [Fact]
    public void ImplementsIAppPathsInterface()
    {
        var paths = new AppPaths(MockLocalAppData);

        Assert.IsAssignableFrom<IAppPaths>(paths);
    }
}

