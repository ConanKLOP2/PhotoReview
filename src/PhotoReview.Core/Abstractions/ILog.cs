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

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Khớp với quy ước phương thức log Error trong PhotoReview và các logger chuẩn.")]
    void Error(string message, Exception? ex = null);
}
