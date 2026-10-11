using System.Runtime.CompilerServices;

// Internal test seams (e.g. WicDirectDecoder.CopyPixelsGuarded).
[assembly: InternalsVisibleTo("PhotoReview.Imaging.Tests")]
// SourceBytesCache.LastReadManagedThreadId: lets FileHashServiceTests prove an oversized file is streamed, not read into the cache.
[assembly: InternalsVisibleTo("PhotoReview.App.Tests")]
// DiskCacheStore.BeforeClearPruneScheduledForTests: BenchmarkImageExecutorTeardownTests holds a prune pass in flight to prove teardown does not wait for it.
[assembly: InternalsVisibleTo("PhotoReview.Integration.Tests")]
// WP-06: the WPF bridge reuses Imaging's internal WIC/IO helpers (WicDirectDecoder.AsInvalidData/EnsureOutputFits, MemoryHeadroom,
// ReadOnlyMemoryStreamFactory, the pure ExifOrientation queries) and TurboJPEG's fine-scale step is WicPixelScaler.
[assembly: InternalsVisibleTo("PhotoReview.Imaging.Wpf")]
[assembly: InternalsVisibleTo("PhotoReview.Imaging.TurboJpeg")]
