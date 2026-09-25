using System.Security.Cryptography;
using System.Text;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Platform.Windows;

public sealed class InstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly ILog _log;
    public bool IsOwner { get; }

    public InstanceLock(string? path, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _mutex = new Mutex(true, MutexNameFor(path), out var created);
        IsOwner = created;
    }

    /// <summary>
    /// R2-F-08: one mutex per folder regardless of spelling (<c>C:\Photos</c>, <c>c:\photos\</c>, a relative path), and one
    /// constant mutex for the no-folder launch instead of one that depends on the current directory.
    /// </summary>
    internal static string MutexNameFor(string? path)
    {
        var canonical = string.IsNullOrWhiteSpace(path)
            ? "PhotoReview"
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        return "Local\\PhotoReview_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                // Mutex is thread-affine: released from a thread other than the one that created it. Nothing to recover
                // (the handle is disposed below and the OS frees the name), but an ownership bug must leave a trace (CORE-09).
                _log.Warn($"InstanceLock release failed (mutex not owned by the disposing thread): {ex.Message}");
            }
        }
        _mutex.Dispose();
    }
}
