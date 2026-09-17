using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace PhotoReview.Platform.Windows;

public sealed class InstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsOwner { get; }

    public InstanceLock(string? path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path ?? "PhotoReview"))));
        _mutex = new Mutex(true, "Local\\PhotoReview_" + key, out var created);
        IsOwner = created;
    }

    public void Dispose()
    {
        if (IsOwner) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
    }
}
