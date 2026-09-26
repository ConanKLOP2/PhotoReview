using System.Globalization;

namespace PhotoReview.Core.Updates;

/// <summary>Numeric major.minor.patch with an optional pre-release flag; parses "v2.0.93", "2.0.93+hash", "2.1.0-beta.1".</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, bool IsPrerelease) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) s = s[..plus];
        var prerelease = false;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            if (dash == s.Length - 1) return false;
            prerelease = true;
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var p = parts[i];
            if (p.Length == 0 || !p.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new AppVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>Compares numerically; a pre-release sorts below the release with the same numbers.</summary>
    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return other.IsPrerelease.CompareTo(IsPrerelease);
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
