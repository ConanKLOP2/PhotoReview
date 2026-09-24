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
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path ?? "PhotoReview"))));
        _mutex = new Mutex(true, "Local\\PhotoReview_" + key, out var created);
        IsOwner = created;
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
