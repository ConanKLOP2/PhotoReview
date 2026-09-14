using System.IO;
using System.Text;

namespace PhotoReview.App;

public static class AppLog
{
    private static readonly object Sync = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "logs", "app.log");
    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);
    private static void Write(string level, string message, Exception? exception)
    {
        try { lock (Sync) { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}{exception}{Environment.NewLine}", Encoding.UTF8); } } catch { }
    }
}
