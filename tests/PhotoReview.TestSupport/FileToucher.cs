using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoReview.TestSupport;

/// <summary>
/// Changes a file's modification time on a background thread until disposed, so any stat comparison taken before
/// and after a read sees a different timestamp (values strictly increase, so they can never coincide).
/// </summary>
public sealed class FileToucher : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _iterations;

    public FileToucher(string path)
    {
        _loop = Task.Run(() =>
        {
            var when = DateTime.UtcNow.AddDays(1);
            while (!_stop.IsCancellationRequested)
            {
                try { File.SetLastWriteTimeUtc(path, when); }
                catch (IOException) { /* a sharing violation with the reader: try again with the next value */ }
                when = when.AddTicks(10_000);
                Volatile.Write(ref _iterations, 1);
            }
        });
        SpinWait.SpinUntil(() => Volatile.Read(ref _iterations) > 0);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _loop.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
