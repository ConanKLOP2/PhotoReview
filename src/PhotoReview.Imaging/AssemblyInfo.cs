using System.Runtime.CompilerServices;

// Internal test seams (e.g. WicDirectDecoder.CopyPixelsGuarded).
[assembly: InternalsVisibleTo("PhotoReview.Imaging.Tests")]
// SourceBytesCache.LastReadManagedThreadId: lets FileHashServiceTests prove an oversized file is streamed, not read into the cache.
[assembly: InternalsVisibleTo("PhotoReview.App.Tests")]
// DiskCacheStore.BeforeClearPruneScheduledForTests: BenchmarkImageExecutorTeardownTests holds a prune pass in flight to prove teardown does not wait for it.
[assembly: InternalsVisibleTo("PhotoReview.Integration.Tests")]
