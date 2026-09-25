using System.Text;

namespace PhotoReview.Core.Instance;

/// <summary>
/// Q-R10 wire format: UTF-8 lines <c>PHOTOREVIEW-OPEN 1</c>, one absolute path per line, then an empty line. The receiver
/// treats every byte as untrusted: size, count, encoding, shape and existence are all checked before any path is used.
/// </summary>
public static class ForwardedPathProtocol
{
    public const string Header = "PHOTOREVIEW-OPEN 1";
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxPaths = 16;
    public const int MaxPathChars = 32767;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > MaxPaths) throw new ArgumentException("Too many paths.", nameof(paths));
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var p in paths) sb.Append(p).Append('\n');
        sb.Append('\n');
        var bytes = StrictUtf8.GetBytes(sb.ToString());
        if (bytes.Length > MaxMessageBytes) throw new ArgumentException("Message too large.", nameof(paths));
        return bytes;
    }

    /// <summary>True when <paramref name="message"/> is complete, i.e. ends with the empty terminator line.</summary>
    public static bool IsComplete(ReadOnlySpan<byte> message) =>
        message.Length >= 2 && message[^1] == (byte)'\n' && message[^2] == (byte)'\n';

    /// <summary>Decodes and validates. An empty list is valid: it only asks the running instance to come to the front.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> message, Func<string, bool> pathExists, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (message.Length is 0 or > MaxMessageBytes || !IsComplete(message)) return false;
        string text;
        try { text = StrictUtf8.GetString(message); }
        catch (ArgumentException) { return false; }

        var lines = text[..^2].Split('\n');
        if (lines.Length == 0 || lines[0] != Header) return false;
        if (lines.Length - 1 > MaxPaths) return false;

        var result = new List<string>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            if (!IsAcceptablePath(lines[i]) || !pathExists(lines[i])) return false;
            result.Add(lines[i]);
        }
        paths = result;
        return true;
    }

    private static bool IsAcceptablePath(string p)
    {
        if (p.Length == 0 || p.Length > MaxPathChars) return false;
        foreach (var c in p) if (char.IsControl(c)) return false;
        // Device / extended-length namespaces are never produced by Explorer's file association and would bypass normalisation.
        // Every spelling: "/" is a separator too, and the NT object prefix \??\ reaches the same namespaces.
        if (p.Length >= 3 && IsSep(p[0]) && (IsSep(p[1]) || p[1] == '?') && p[2] is '?' or '.') return false;
        // ".." segments would let a forwarded path escape the folder the sender named.
        foreach (var segment in p.Split('\\', '/')) if (segment == "..") return false;
        return Path.IsPathFullyQualified(p);
    }

    private static bool IsSep(char c) => c is '\\' or '/';
}
