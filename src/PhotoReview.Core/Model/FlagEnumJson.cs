using System.Text.Json;

namespace PhotoReview.Core.Model;

/// <summary>
/// Shared lenient JSON handling of the persisted flag enums (<see cref="ExifInfoFields"/>, <see cref="TitleBarFields"/>).
/// Writing names the single flags (never the <c>All</c>/<c>Default</c> aliases, whose meaning changes between versions);
/// reading keeps every valid name of a list, drops unknown ones and treats negative numbers as unreadable.
/// </summary>
internal static class FlagEnumJson
{
    private static readonly string[] AliasNames = ["None", "All", "Default"];

    internal static T Read<T>(ref Utf8JsonReader reader, T all, T fallback) where T : struct, Enum
    {
        var allBits = Convert.ToInt64(all, System.Globalization.CultureInfo.InvariantCulture);
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // A valid non-negative number is a real bitmask value (unknown high bits stripped against All); a negative
                // one (-1 would become "everything on") or an out-of-range one is unreadable.
                return reader.TryGetInt64(out var number) && number >= 0 ? FromBits<T>(number & allBits) : fallback;
            case JsonTokenType.String:
                return ParseText(reader.GetString(), allBits, fallback);
            default:
                reader.Skip();
                return fallback;
        }
    }

    internal static string Format<T>(T value, T all) where T : struct, Enum
    {
        var bits = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
                   & Convert.ToInt64(all, System.Globalization.CultureInfo.InvariantCulture);
        if (bits == 0) return "None";
        var names = new List<string>();
        foreach (var name in Enum.GetNames<T>())
        {
            if (Array.IndexOf(AliasNames, name) >= 0) continue;
            var flag = Convert.ToInt64(Enum.Parse<T>(name), System.Globalization.CultureInfo.InvariantCulture);
            if (flag != 0 && (flag & (flag - 1)) == 0 && (bits & flag) != 0) names.Add(name);
        }

        return string.Join(", ", names);
    }

    private static T ParseText<T>(string? text, long allBits, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        long bits = 0;
        var anyValid = false;
        foreach (var token in text.Split(','))
        {
            var part = token.Trim();
            if (part.Length == 0 || !Enum.TryParse<T>(part, ignoreCase: true, out var parsed)) continue;
            var value = Convert.ToInt64(parsed, System.Globalization.CultureInfo.InvariantCulture);
            if (value < 0) continue; // "-1" parses as a number: unreadable, not "everything"
            bits |= value;
            anyValid = true;
        }

        return anyValid ? FromBits<T>(bits & allBits) : fallback;
    }

    private static T FromBits<T>(long bits) where T : struct, Enum => (T)Enum.ToObject(typeof(T), bits);
}
