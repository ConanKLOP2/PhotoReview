namespace PhotoReview.Imaging.Preload;

/// <summary>
/// Q-R17: running mean of decoded preview sizes at one decode box, fed by preload as images land in
/// the RAM cache. A sample at a different box (window resized, DPI change) restarts the mean, since
/// previews at the old box say nothing about the new one. Thread-safe: preload workers record
/// concurrently while the scheduler loop reads.
/// </summary>
public sealed class PreviewSizeSampler
{
    /// <summary>Samples needed before the mean replaces the box upper bound.</summary>
    public const int MinimumSamples = 8;

    private readonly object _gate = new();
    private DecodeBox _box;
    private long _totalBytes;
    private int _count;

    public void Record(DecodeBox box, long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            if (box != _box)
            {
                _box = box;
                _totalBytes = 0;
                _count = 0;
            }
            _totalBytes += bytes;
            _count++;
        }
    }

    /// <summary>Mean preview bytes at <paramref name="box"/>, or null until <see cref="MinimumSamples"/> were recorded at that box.</summary>
    public double? MeanBytes(DecodeBox box)
    {
        lock (_gate)
            return box == _box && _count >= MinimumSamples ? (double)_totalBytes / _count : null;
    }
}
