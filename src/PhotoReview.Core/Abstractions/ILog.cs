using System.Diagnostics.CodeAnalysis;

namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc ghi log chẩn đoán của ứng dụng.
/// </summary>
public interface ILog
{
    bool Enabled { get; }

    void Info(string message);
    void Warn(string message);

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Matches the Error log method convention used across PhotoReview and standard loggers.")]
    void Error(string message, Exception? ex = null);
}
