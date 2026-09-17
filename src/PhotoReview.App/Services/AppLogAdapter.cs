using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

internal sealed class AppLogAdapter : ILog
{
    public void Info(string message) => AppLog.Info(message);
    public void Warn(string message) => AppLog.Info($"[WARN] {message}");
    public void Error(string message, Exception? ex = null) => AppLog.Error(message, ex);
}
