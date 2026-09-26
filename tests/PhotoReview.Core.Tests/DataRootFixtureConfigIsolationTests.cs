using System.IO;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Core.Tests;

/// <summary>
/// Regression: tests that build the production DI graph under <see cref="DataRootFixture"/> and save settings
/// (e.g. SettingsJournalDurabilityTests) used to overwrite the user's real %LOCALAPPDATA%\PhotoReview\config.json
/// with defaults, because PHOTOREVIEW_DATA_ROOT alone never moved the config file.
/// </summary>
[Collection("GlobalState")]
public sealed class DataRootFixtureConfigIsolationTests
{
    [Fact]
    public void DataRootFixture_MovesConfigFileUnderTheTempRoot()
    {
        var realConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "config.json");

        using (var fixture = new DataRootFixture())
        {
            var configFile = AppPaths.FromEnvironment().ConfigFile;
            Assert.NotEqual(realConfig, configFile, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(fixture.Path, configFile, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(Environment.GetEnvironmentVariable(AppPaths.IsolateConfigEnvironmentVariable));
    }
}
