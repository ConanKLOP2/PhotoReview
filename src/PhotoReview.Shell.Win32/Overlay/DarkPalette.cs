using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-17: bảng màu tối của app cho overlay Direct2D - SAO giá trị từ <c>PhotoReview.App/Themes/DarkPalette.xaml</c>
/// (khoá "Dark.*"). <c>DarkPaletteTests</c> so từng khoá/giá trị với XAML (thiếu hoặc lệch = đỏ). Màu <see cref="ColorF"/>
/// là alpha thẳng (như <c>#AARRGGBB</c> của WPF).
/// </summary>
public static class DarkPalette
{
    private static readonly (string Key, uint Argb)[] Table =
    [
        ("Dark.Text", 0xFFE5E5E5),
        ("Dark.TextStrong", 0xFFFFFFFF),
        ("Dark.TextMuted", 0xFF9A9A9A),
        ("Dark.Input", 0xFF252525),
        ("Dark.Panel", 0xFF1E1E1E),
        ("Dark.Border", 0xFF3C3C3C),
        ("Dark.BorderStrong", 0xFF5A5A5A),
        ("Dark.Button", 0xFF2D2D2D),
        ("Dark.ButtonHover", 0xFF383838),
        ("Dark.ButtonPressed", 0xFF444444),
        ("Dark.Accent", 0xFF3A7BD5),
        ("Dark.AccentHover", 0xFF4A8BE5),
        ("Dark.Window", 0xFF171717),
        ("Dark.Backdrop", 0xFF181818),
        ("Dark.Canvas", 0xFF101010),
        ("Dark.Tooltip", 0xFF202020),
        ("Dark.Surface", 0xFF2A2A2A),
        ("Dark.RowAlt", 0xFF242424),
        ("Dark.Overlay", 0xB0181818),
        ("Dark.Selection", 0xFF3A5A80),
        ("Dark.SuccessBackground", 0xFF1E3B24),
        ("Dark.ErrorBackground", 0xFF3B1E1E),
        ("Dark.WindowText", 0xFFF5F5F5),
        ("Dark.TextBright", 0xFFEEEEEE),
        ("Dark.TextSoft", 0xFFCFCFCF),
        ("Dark.TextSecondary", 0xFFBBBBBB),
        ("Dark.TextSubtle", 0xFFAAAAAA),
        ("Dark.TextFaint", 0xFF777777),
        ("Dark.Warning", 0xFFFFC857),
        ("Dark.Caution", 0xFFF0B040),
        ("Dark.ErrorText", 0xFFFF9A9A),
        ("Dark.BorderSubtle", 0xFF3A3A3A),
        ("Dark.BorderToolbar", 0xFF454545),
        ("Dark.GridLine", 0xFF333333),
        ("Dark.ScrollTrack", 0xFF161616),
    ];

    private static readonly Dictionary<string, ColorF> ByKey = Build();

    /// <summary>Mọi khoá "Dark.*" và màu của nó.</summary>
    public static IReadOnlyDictionary<string, ColorF> All => ByKey;

    public static ColorF Overlay => ByKey["Dark.Overlay"];

    public static ColorF TextBright => ByKey["Dark.TextBright"];

    public static ColorF TextSecondary => ByKey["Dark.TextSecondary"];

    public static ColorF Warning => ByKey["Dark.Warning"];

    public static ColorF Button => ByKey["Dark.Button"];

    public static ColorF ButtonHover => ByKey["Dark.ButtonHover"];

    public static ColorF ButtonPressed => ByKey["Dark.ButtonPressed"];

    public static ColorF Tooltip => ByKey["Dark.Tooltip"];

    public static ColorF BorderStrong => ByKey["Dark.BorderStrong"];

    /// <summary>Màu theo khoá (vd. "Dark.Overlay"); ném <see cref="KeyNotFoundException"/> khi không có.</summary>
    public static ColorF Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ByKey.TryGetValue(key, out ColorF color) ? color : throw new KeyNotFoundException($"Unknown palette key '{key}'.");
    }

    /// <summary>ARGB 8 bit/kênh -&gt; <see cref="ColorF"/> (kênh / 255).</summary>
    public static ColorF FromArgb(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        ((argb >> 24) & 0xFF) / 255f);

    private static Dictionary<string, ColorF> Build()
    {
        var map = new Dictionary<string, ColorF>(Table.Length, StringComparer.Ordinal);
        foreach ((string key, uint argb) in Table)
        {
            map.Add(key, FromArgb(argb));
        }

        return map;
    }
}
