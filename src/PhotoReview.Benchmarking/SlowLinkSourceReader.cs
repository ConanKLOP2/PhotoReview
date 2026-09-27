using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Benchmarking;

/// <summary>
/// Q-R29 option C-2 measurement harness (NOT a production default -- only wired in by
/// <c>tools/PhotoReview.Benchmark.Cli</c>'s <c>--perf-session</c> when <c>--slow-link-bandwidth-mbps</c>
/// is passed, onto the <see cref="ISourceReader"/> seam introduced for this measurement). Unlike
/// <see cref="SlowLinkFileSystem"/>'s <c>OpenReadShared</c> (which #202 found unreachable from the real
/// image-decode path -- see docs/refactoring/decisions/Q-R29-C.md part 2), this decorator wraps the
/// actual seam <c>SourceBytesCache</c>/the decoders/<c>PreviewImageService</c> now open source bytes
/// through, so the same <see cref="SharedBandwidthLimiter"/> instance genuinely throttles every reader
/// (foreground viewer decode and every preload worker) sharing one simulated link's bandwidth budget --
/// <see cref="SourceReadPriority"/> is accepted for future de-prioritization policy but this measurement
/// decorator itself is priority-blind (every lane draws from the same bucket), matching #202's original
/// "one shared link" model so the two harnesses stay comparable.
/// </summary>
public sealed class SlowLinkSourceReader : ISourceReader
{
    private readonly ISourceReader _inner;
    private readonly SharedBandwidthLimiter? _bandwidth;

    public SlowLinkSourceReader(ISourceReader inner, SharedBandwidthLimiter? bandwidth)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _bandwidth = bandwidth;
    }

    public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024)
    {
        var stream = _inner.OpenSource(path, priority, bufferSize);
        return _bandwidth is null ? stream : new ThrottledReadStream(stream, _bandwidth);
    }
}
