using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.Fakes;

/// <summary>
/// The real <see cref="FileActionService"/> and <see cref="OperationJournal"/> on the physical file system, with the
/// journal under <c>dataDir</c> and a recycle bin that only deletes inside the caller-owned temp root (so a test can
/// never reach the user's Recycle Bin). <see cref="Recycled"/> lists the paths sent to it.
/// </summary>
internal sealed class PhysicalActionHarness
{
    public List<string> Recycled { get; } = [];
    public OperationJournal Journal { get; }
    public FileActionService Service { get; }

    public PhysicalActionHarness(string dataDir)
    {
        var clock = new SystemClock();
        var fileSystem = new PhysicalFileSystem();
        Journal = new OperationJournal(new TempPaths(dataDir), fileSystem, clock);
        Service = new FileActionService(Journal, fileSystem, clock, new TempRecycleBin(Recycled));
    }

    private sealed class TempPaths(string dir) : IAppPaths
    {
        public string ConfigFile => Path.Combine(dir, "config.json");
        public string JournalFile => Path.Combine(dir, "operations.jsonl");
        public string SessionsDir => Path.Combine(dir, "Sessions");
        public string LogFile => Path.Combine(dir, "app.log");
        public string PreviewCacheDir => Path.Combine(dir, "cache");
        public string ThumbnailCacheDir => Path.Combine(dir, "thumbnails");
        public string WindowPlacementFile => Path.Combine(dir, "window-placement.json");
    }

    private sealed class TempRecycleBin(List<string> recycled) : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            recycled.Add(path);
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
