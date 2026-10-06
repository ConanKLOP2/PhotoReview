using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoReview.Core.Tests.Properties;

/// <summary>
/// Properties of the one config.json parse pipeline (<see cref="SettingsStore.ParseText"/>) over generated JSON:
/// never throws anything but <see cref="JsonException"/>, always yields in-range values, is a fixed point
/// (normalise twice == once, serialise-parse-serialise is stable) and salvage keeps the valid keys.
/// </summary>
public sealed class SettingsParsePropertyTests
{
    private static readonly string[] BoolKeys =
        [nameof(AppSettings.ShowInfoOverlay), nameof(AppSettings.ConfirmBeforeDelete), nameof(AppSettings.ShowZoomIndicator),
         nameof(AppSettings.ToolbarAutoHide), nameof(AppSettings.KineticPanEnabled), nameof(AppSettings.KeepZoomAcrossImages)];

    private static readonly string[] Strings = ["NaN", "1e999", "-1", "0", "100", "Fit", "Original", "Nope", ""];

    private static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

    private static JsonNode? RandomValue(Random rng, int depth = 0) => rng.Next(depth > 1 ? 7 : 9) switch
    {
        0 => null,
        1 => JsonValue.Create(rng.Next(2) == 0),
        2 => JsonValue.Create(rng.Next(-5, 5000)),
        3 => JsonValue.Create(PropertyRunner.EdgyLong(rng)),
        4 => JsonValue.Create(rng.NextDouble() * 4 - 1),
        5 => JsonValue.Create(PropertyRunner.RandomString(rng, "abc 019-.\"\\é", 6)),
        6 => JsonValue.Create(Strings[rng.Next(Strings.Length)]),
        7 => new JsonArray(Enumerable.Range(0, rng.Next(0, 3)).Select(_ => RandomValue(rng, depth + 1)).ToArray()),
        _ => new JsonObject(Enumerable.Range(0, rng.Next(0, 3)).Select(i => KeyValuePair.Create("k" + i, RandomValue(rng, depth + 1))).ToArray()),
    };

    private static string RandomConfigText(Random rng)
    {
        var node = (JsonObject)JsonNode.Parse(Serialize(new AppSettings()))!;
        var keys = node.Select(p => p.Key).ToArray();
        for (var i = rng.Next(0, 8); i > 0; i--)
        {
            var key = keys[rng.Next(keys.Length)];
            switch (rng.Next(4))
            {
                case 0: node.Remove(key); break;
                case 1: node[key] = RandomValue(rng); break;
                case 2: node["Unknown" + rng.Next(5)] = RandomValue(rng); break;
                default: node[key] = JsonValue.Create(PropertyRunner.EdgyLong(rng)); break;
            }
        }
        var text = node.ToJsonString();
        return rng.Next(12) switch
        {
            0 => text[..rng.Next(0, text.Length)], // truncated
            1 => PropertyRunner.RandomString(rng, "{}[]\":,0a \n", 20), // garbage
            _ => text,
        };
    }

    private static void AssertInRange(AppSettings s)
    {
        Assert.InRange(s.ImageCacheRamPercent, PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent);
        Assert.InRange(s.PreloadForwardCount, PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount);
        Assert.InRange(s.PreloadBackwardCount, PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);
        Assert.InRange(s.PreloadWorkerCount, 1, PerformanceOptions.MaxPreloadWorkerCount);
        Assert.True(s.PreloadMemoryLoadLimit > 0 && s.PreloadMemoryLoadLimit <= 1, $"PreloadMemoryLoadLimit {s.PreloadMemoryLoadLimit}");
        Assert.True(s.ImageCacheCapacityBytes > 0);
        Assert.True(s.SourceBytesCapacityBytes > 0);
        Assert.True(s.MemoryReserveBytes >= 0);
        Assert.True(s.PreviewDiskCacheCapacityBytes >= 0);
        Assert.NotNull(s.Shortcuts);
        Assert.NotNull(s.Actions);
        Assert.DoesNotContain(null, s.Actions);
    }

    [Fact(DisplayName = "ParseText on generated config text only ever throws JsonException; every result is in range and a fixed point of normalisation")]
    public void ParseText_NeverCrashes_InRange_FixedPoint()
    {
        PropertyRunner.Check("SettingsStore.ParseText generated JSON", iterations: 120, (rng, _) =>
        {
            var text = RandomConfigText(rng);
            SettingsParseResult result;
            try { result = SettingsStore.ParseText(text); }
            catch (JsonException) { return; } // documented: not a JSON object at all

            var settings = result.Settings;
            AssertInRange(settings);

            // normalise(normalise(x)) == normalise(x)
            var before = Serialize(settings);
            Assert.Empty(SettingsNormalizer.Normalize(settings));
            Assert.Empty(SettingsNormalizer.DisableConflictingOptionalShortcuts(settings));
            Assert.Equal(before, Serialize(settings));

            // serialise -> parse -> serialise is stable and needs no further repair
            var again = SettingsStore.ParseText(before);
            Assert.Empty(again.Repairs);
            Assert.Equal(before, Serialize(again.Settings));
        });
    }

    [Fact(DisplayName = "Salvage keeps every valid key when another key has the wrong type")]
    public void Salvage_KeepsValidKeys()
    {
        PropertyRunner.Check("SettingsStore.ParseText salvage", iterations: 50, (rng, _) =>
        {
            var node = (JsonObject)JsonNode.Parse(Serialize(new AppSettings()))!;
            var percent = rng.Next(PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent + 1);
            var forward = rng.Next(PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount + 1);
            node[nameof(AppSettings.ImageCacheRamPercent)] = percent;
            node[nameof(AppSettings.PreloadForwardCount)] = forward;
            var bools = BoolKeys.ToDictionary(k => k, _ => rng.Next(2) == 0);
            foreach (var (key, value) in bools) node[key] = value;
            var broken = BoolKeys.Where(_ => rng.Next(3) == 0).Take(1).Append(nameof(AppSettings.LoggingEnabled)).First();
            node[broken] = "definitely-not-a-bool";
            if (rng.Next(2) == 0) node[nameof(AppSettings.ClickZoomPercent)] = new JsonObject();

            var result = SettingsStore.ParseText(node.ToJsonString());

            Assert.NotNull(result.SalvageCause);
            Assert.Contains(broken, result.Salvaged);
            Assert.Equal(percent, result.Settings.ImageCacheRamPercent);
            Assert.Equal(forward, result.Settings.PreloadForwardCount);
            foreach (var (key, value) in bools.Where(b => b.Key != broken))
                Assert.Equal(value, (bool)typeof(AppSettings).GetProperty(key)!.GetValue(result.Settings)!);
        });
    }
}
