using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App;

/// <summary>
/// Static facade for <see cref="FileLog"/> providing backward compatibility for UI callers.
/// Forwards to <see cref="FileLog.Default"/> by default.
/// </summary>
public static class AppLog
{
    private static FileLog _instance = FileLog.Default;

    public static FileLog Instance
    {
        get => _instance;
        set => _instance = value ?? FileLog.Default;
    }

    private static int _enabledWrites;

    public static bool Enabled
    {
        get => _instance.Enabled;
        set
        {
            Interlocked.Increment(ref _enabledWrites);
            _instance.Enabled = value;
        }
    }

    /// <summary>Counts every write to <see cref="Enabled"/>, so a temporary override can tell whether someone else changed it meanwhile.</summary>
    public static int EnabledWriteCount => Volatile.Read(ref _enabledWrites);

    public static string FilePath => _instance.FilePath;

    public static void Info(string message) => _instance.Info(message);

    public static void Warn(string message) => _instance.Warn(message);

    public static void Error(string message, Exception? exception = null) => _instance.Error(message, exception);

    /// <summary>
    /// Serializes the "force logging on, write, flush, restore" sequences: two racing callers (AppDomain handler on a
    /// pool thread and the dispatcher handler) each saved the other's forced value as "previous state" and could leave
    /// logging permanently on. (Moved from App.xaml.cs by WP-09 so the shared composition can log startup errors.)
    /// </summary>
    private static readonly object ForcedLogLock = new();

    internal static void WriteForced(Action write)
    {
        lock (ForcedLogLock)
        {
            var wasEnabled = Enabled;
            Enabled = true;
            try
            {
                write();
                Flush();
            }
            finally
            {
                Enabled = wasEnabled;
            }
        }
    }

    /// <summary>Records an error even while logging is off (forces it on just long enough to persist the entry), then restores the flag.</summary>
    internal static void ErrorForced(string message, Exception ex) => WriteForced(() => Error(message, ex));

    public static void Flush() => _instance.Flush();

    public static void Shutdown() => _instance.Shutdown();
}
