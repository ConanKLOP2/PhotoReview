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

    internal static string CurrentUserSid { get; } = WindowsIdentity.GetCurrent().User?.Value ?? "nosid";

    internal static int CurrentSessionId { get; } = System.Diagnostics.Process.GetCurrentProcess().SessionId;

    public static InstanceKeys For(InstanceMode mode, string? folder, string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var mutex = mode == InstanceMode.PerFolder
            ? InstanceLock.MutexNameFor(folder, prefix)
            : "Local\\" + prefix + "_App_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("app|" + CurrentUserSid)));
        return new InstanceKeys(mutex, InstanceForwardPipe.NameForMutex(mutex, prefix));
    }
}
