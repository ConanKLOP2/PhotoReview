using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.App.Tests.Golden;

/// <summary>
/// WP-10 G-KEY (<c>key-names.v1.json</c>): every <see cref="Key"/> name with its value and <see cref="KeyInterop.VirtualKeyFromKey"/>.
/// The Win32 shell maps WM_KEYDOWN virtual keys to <c>KeyId</c> (<c>KeyIdMapping</c>, WP-07) and must give the same names the
/// shortcut settings use (<c>ShortcutRouter</c> parses them with <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> on <see cref="Key"/>).
/// </summary>
public sealed class KeyNameGoldenTests
{
    internal const string Notes =
        "Every System.Windows.Input.Key name (aliases included: Return/Enter, Prior/PageUp, ...) in value order, with the numeric Key " +
        "value and KeyInterop.VirtualKeyFromKey(key) (0 = the key has no virtual key, e.g. None/DeadCharProcessed).";

    internal static IReadOnlyList<GoldenKeyName> Enumerate() =>
        Enum.GetValues<Key>()
            .Select(key => (Key: key, Name: Enum.GetName(key)!))
            .Concat(Enum.GetNames<Key>().Select(name => (Key: Enum.Parse<Key>(name), Name: name)))
            .DistinctBy(x => x.Name, StringComparer.Ordinal)
            .OrderBy(x => (int)x.Key)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new GoldenKeyName(x.Name, (int)x.Key, KeyInterop.VirtualKeyFromKey(x.Key)))
            .ToList();

    [Fact]
    public void KeyNames_CommittedGolden_MatchesTheWpfKeyEnumeration()
    {
        var committed = GoldenFile.Read(GoldenFile.KeyNames, GoldenJsonContext.Default.GoldenDocumentGoldenKeyName);

        Assert.Equal(Enum.GetNames<Key>().Length, committed.Items.Count);
        Assert.Equal(Enumerate(), committed.Items);
    }

    [Fact]
    public void KeyNames_EveryShortcutNameParsesToItsGoldenValue()
    {
        // The default shortcut strings the settings store (ShortcutMappings) are Key names: they must be golden names.
        var golden = GoldenFile.Read(GoldenFile.KeyNames, GoldenJsonContext.Default.GoldenDocumentGoldenKeyName).Items
            .ToDictionary(k => k.Name, StringComparer.Ordinal);
        var defaults = new ShortcutMappings();
        foreach (var property in typeof(ShortcutMappings).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            var name = (string?)property.GetValue(defaults);
            if (string.IsNullOrEmpty(name)) continue;
            Assert.True(golden.TryGetValue(name, out var entry), $"{property.Name}={name} is not a golden key name.");
            Assert.Equal((int)Enum.Parse<Key>(name), entry!.KeyValue);
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void RecordKeyNames_WritesTheGoldenFromTheWpfBuild()
    {
        Assert.True(GoldenFile.IsRecordingEnabled,
            $"Recording rewrites tests/Fixtures/golden. Set {GoldenFile.RecordEnvironmentVariable}=1 on purpose (tools/diag/record-golden.ps1).");
        var document = new GoldenDocument<GoldenKeyName>("key-names", 1, "PhotoReview.App.Tests.Golden.KeyNameGoldenTests (System.Windows.Input.Key)", Notes, Enumerate());

        GoldenFile.Write(GoldenFile.KeyNames, document, GoldenJsonContext.Default.GoldenKeyName);
    }
}
