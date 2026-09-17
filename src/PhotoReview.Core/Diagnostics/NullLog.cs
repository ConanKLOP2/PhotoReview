using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Diagnostics;

/// <summary>
/// Triển khai rỗng (no-op) của <see cref="ILog"/> dùng khi tắt log hoặc trong unit test.
/// </summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
