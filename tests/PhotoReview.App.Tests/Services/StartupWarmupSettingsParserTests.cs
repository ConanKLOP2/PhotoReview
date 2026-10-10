using System.Text.Json;
using PhotoReview.App.Services;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// P-1 startup: <see cref="StartupWarmup.WarmSettingsParser"/> runs the config.json parse pipeline once on built-in
/// defaults from App's static constructor. It must finish cleanly and leave no trace: parsing a real config afterwards
/// gives exactly what it gave before.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class StartupWarmupSettingsParserTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static string RealConfigText()
    {
        var settings = new AppSettings { LoadingMode = LoadingMode.Original, ClickZoomPercent = 250 };
        return JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
    }

    [Fact(DisplayName = "The warm-up completes without faulting")]
    public async Task WarmSettingsParser_Completes_WithoutFaulting()
    {
        var task = StartupWarmup.WarmSettingsParser();

        await task.WaitAsync(Timeout);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact(DisplayName = "Parsing a real config gives the same result before and after the warm-up")]
    public async Task ParseText_AfterWarmUp_ReturnsTheSameResultAsBefore()
    {
        var text = RealConfigText();
        var before = SettingsStore.ParseText(text);

        await StartupWarmup.WarmSettingsParser().WaitAsync(Timeout);
        await StartupWarmup.WarmSettingsParser().WaitAsync(Timeout); // idempotent, repeatable
        var after = SettingsStore.ParseText(text);

        Assert.Equal(LoadingMode.Original, after.Settings.LoadingMode);
        Assert.Equal(250, after.Settings.ClickZoomPercent);
        Assert.Equal(
            JsonSerializer.Serialize(before.Settings, AppSettingsJsonContext.Default.AppSettings),
            JsonSerializer.Serialize(after.Settings, AppSettingsJsonContext.Default.AppSettings));
        Assert.Equal(before.Salvaged, after.Salvaged);
        Assert.Equal(before.ResetSettings, after.ResetSettings);
        Assert.Equal(before.DisabledShortcuts, after.DisabledShortcuts);
        Assert.Null(after.SalvageCause);
    }
}
