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

    public static bool Enabled
    {
        get => _instance.Enabled;
        set => _instance.Enabled = value;
    }

    public static string FilePath => _instance.FilePath;

    public static void Info(string message) => _instance.Info(message);

    public static void Warn(string message) => _instance.Warn(message);

    public static void Error(string message, Exception? exception = null) => _instance.Error(message, exception);

    public static void Flush() => _instance.Flush();

    public static void Shutdown() => _instance.Shutdown();
}
