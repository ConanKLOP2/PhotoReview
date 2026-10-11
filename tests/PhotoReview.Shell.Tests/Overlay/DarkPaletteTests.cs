using System.Globalization;
using System.Text.RegularExpressions;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>WP-17: <see cref="DarkPalette"/> khớp từng khoá và giá trị của <c>PhotoReview.App/Themes/DarkPalette.xaml</c>.</summary>
public sealed partial class DarkPaletteTests
{
    [GeneratedRegex("""<SolidColorBrush\s+x:Key="(?<key>Dark\.[A-Za-z0-9]+)"\s+Color="#(?<hex>[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})"\s*/>""")]
    private static partial Regex BrushRegex();

    private static string XamlPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "PhotoReview.App", "Themes", "DarkPalette.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("DarkPalette.xaml not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, uint> ReadXaml()
    {
        string text = File.ReadAllText(XamlPath());
        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (Match m in BrushRegex().Matches(text))
        {
            string hex = m.Groups["hex"].Value;
            uint value = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            map.Add(m.Groups["key"].Value, hex.Length == 6 ? 0xFF000000u | value : value);
        }

        return map;
    }

    [Fact]
    public void EveryXamlKey_IsInTheTable_WithTheSameColor()
    {
        Dictionary<string, uint> xaml = ReadXaml();

        Assert.True(xaml.Count >= 30, $"parsed only {xaml.Count} brushes from the XAML");
        foreach ((string key, uint argb) in xaml)
        {
            Assert.True(DarkPalette.All.ContainsKey(key), $"{key} missing from DarkPalette");
            Assert.Equal(DarkPalette.FromArgb(argb), DarkPalette.Get(key));
        }
    }

    [Fact]
    public void TableHasNoKeyThatTheXamlLacks()
    {
        Dictionary<string, uint> xaml = ReadXaml();

        foreach (string key in DarkPalette.All.Keys)
        {
            Assert.True(xaml.ContainsKey(key), $"{key} is not in DarkPalette.xaml");
        }

        Assert.Equal(xaml.Count, DarkPalette.All.Count);
    }

    [Fact]
    public void ArgbConversion_UsesStraightAlpha()
    {
        // Dark.Overlay = #B0181818: A = 0xB0, RGB = 0x18.
        var overlay = DarkPalette.Overlay;
        Assert.Equal(0xB0 / 255f, overlay.A, 5);
        Assert.Equal(0x18 / 255f, overlay.R, 5);
        Assert.Equal(overlay.R, overlay.G);
        Assert.Equal(overlay.R, overlay.B);
        var c = DarkPalette.FromArgb(0x33FF0080);
        Assert.Equal((0x33 / 255f, 1f, 0f, 0x80 / 255f), (c.A, c.R, c.G, c.B));
    }

    [Fact]
    public void Get_UnknownKey_Throws() =>
        Assert.Throws<KeyNotFoundException>(() => DarkPalette.Get("Dark.DoesNotExist"));
}
