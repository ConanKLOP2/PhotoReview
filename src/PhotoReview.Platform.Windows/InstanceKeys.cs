using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// Q-R18: the single-instance mutex name and the forward pipe name that belong together.
/// <list type="bullet">
/// <item><see cref="InstanceMode.SingleWindow"/>: one pair per Windows user and logon session, independent of any folder.</item>
/// <item><see cref="InstanceMode.PerFolder"/>: one pair per canonical folder (R2-F-08 rules; identical to the names used
/// before Q-R18, so an older build on the same folder is still detected).</item>
/// </list>
/// Mutexes are <c>Local\</c> (session-local); pipes add the user SID and session id (see <see cref="InstanceForwardPipe"/>).
/// </summary>
public sealed record InstanceKeys(string MutexName, string PipeName)
{
    /// <summary>Production name prefix. Tests pass a unique prefix so they never collide with a running PhotoReview.</summary>
    public const string DefaultPrefix = "PhotoReview";

    internal static string CurrentUserSid { get; } = ReadUserSid();

    internal static int CurrentSessionId { get; } = ReadSessionId();

    // Both objects wrap OS handles; they are read once per process and released right away.
    private static string ReadUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "nosid";
    }

    private static int ReadSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }

    public static InstanceKeys For(InstanceMode mode, string? folder, string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var mutex = mode == InstanceMode.PerFolder
            ? MutexNameFor(folder, prefix)
            : "Local\\" + prefix + "_App_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("app|" + CurrentUserSid)));
        return new InstanceKeys(mutex, InstanceForwardPipe.NameForMutex(mutex, prefix));
    }

    /// <summary>
    /// R2-F-08: one mutex per folder regardless of spelling (<c>C:\Photos</c>, <c>c:\photos\</c>, a relative path), and one
    /// constant mutex for the no-folder launch instead of one that depends on the current directory.
    /// </summary>
    internal static string MutexNameFor(string? path) => MutexNameFor(path, DefaultPrefix);

    /// <summary>Q-R18: same rule with a name prefix (tests use a unique one so they never meet a real PhotoReview).</summary>
    internal static string MutexNameFor(string? path, string prefix) =>
        "Local\\" + prefix + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalKey(path))));

    /// <summary>
    /// R2-F-08 canonical folder key: full path, no trailing separator, upper case; one constant for "no folder".
    /// Text that is not a usable path (embedded NUL, ...) keys on its own upper-cased spelling instead of throwing,
    /// so a bad folder argument reaches the normal "cannot open folder" handling rather than crashing the lock lookup.
    /// </summary>
    internal static string CanonicalKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "PhotoReview";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }
}
