using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// R01 (review 2026-10-04): a Copy that fails BEFORE it created anything (the source vanished while another process created the
/// destination) must never delete that destination. Ownership comes only from the copy's own proof of creation; everything runs on
/// the in-memory file system and never touches a real photo or the Recycle Bin.
/// </summary>
public sealed class CopyForeignDestinationTests
{
    private const string Foreign = "foreign";

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class Paths(string journalFile) : IAppPaths
    {
        public string ConfigFile => @"C:\fake\config.json";
        public string JournalFile => journalFile;
        public string SessionsDir => @"C:\fake\Sessions";
        public string LogFile => @"C:\fake\app.log";
        public string PreviewCacheDir => @"C:\fake\cache";
        public string ThumbnailCacheDir => @"C:\fake\thumbs";
        public string WindowPlacementFile => @"C:\fake\window.json";
    }

    private sealed class NeverBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new InvalidOperationException("The Recycle Bin must not be used.");
        public bool TryRestore(string path, long length, DateTime deletedUtc) => throw new InvalidOperationException("The Recycle Bin must not be used.");
    }

    private static (InMemoryFileSystem Fs, OperationJournal Journal, FileActionService Service) World()
    {
        var fs = new InMemoryFileSystem();
        var clock = new Clock();
        var journal = new OperationJournal(new Paths(@"C:\fake\operations.jsonl"), fs, clock);
        return (fs, journal, new FileActionService(journal, fs, clock, new NeverBin()));
    }

    [Fact(DisplayName = "R01: single Copy whose source vanishes while a foreign destination appears keeps the foreign file")]
    public async Task SingleCopy_SourceVanishesAndForeignDestinationArrives_ForeignFileSurvives()
    {
        var (fs, _, service) = World();
        const string source = @"C:\photos\a.jpg";
        const string destination = @"C:\photos\sel\a.jpg";
        fs.AddFile(source, "long source photo");
        fs.CopyHook = (s, d) => { fs.AddFile(d, Foreign); fs.Delete(s); return null; };

        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(destination));
        Assert.Equal(Foreign, fs.ReadAllText(destination));
    }

    [Fact(DisplayName = "R01: group Copy whose member source vanishes while a longer foreign destination appears keeps the foreign file")]
    public async Task GroupCopy_SourceVanishesAndForeignDestinationArrives_ForeignFileSurvives()
    {
        var (fs, _, service) = World();
        const string jpeg = @"C:\photos\a.jpg";
        const string raw = @"C:\photos\a.cr2";
        const string destination = @"C:\photos\sel\a.cr2";
        fs.AddFile(jpeg, "jpeg");
        fs.AddFile(raw, "raw");
        const string foreignLonger = "foreign longer than source";
        fs.CopyHook = (s, d) =>
        {
            if (s == raw) { fs.AddFile(d, foreignLonger); fs.Delete(s); }
            return null;
        };

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(jpeg, raw), FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(destination));
        Assert.Equal(foreignLonger, fs.ReadAllText(destination));
        Assert.False(fs.FileExists(@"C:\photos\sel\a.jpg")); // the copy this operation did make is still compensated
    }

    [Fact(DisplayName = "R01: recovery retry Copy whose source vanishes while a foreign destination appears keeps the foreign file")]
    public async Task RecoveryCopy_SourceVanishesAndForeignDestinationArrives_ForeignFileSurvives()
    {
        var (fs, journal, _) = World();
        const string source = @"C:\photos\a.jpg";
        const string destination = @"D:\sel\a.jpg";
        fs.AddFile(source, "long source photo");
        var stat = fs.GetFileStat(source)!;
        var failed = new JournalEntry("r01", FileOperationType.Copy, JournalState.Failed, source, destination, stat.Length, stat.LastWriteUtc, new Clock().UtcNow);
        journal.Append(failed);
        fs.CopyHook = (s, d) => { fs.AddFile(d, Foreign); fs.Delete(s); return null; };

        var result = await new RecoveryRetryService(journal, fs, new Clock()).RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(destination));
        Assert.Equal(Foreign, fs.ReadAllText(destination));
    }

    [Fact(DisplayName = "R01 control: a Copy that creates its destination and then fails still removes its own partial file")]
    public async Task SingleCopy_FailsAfterCreatingDestination_OwnPartialFileIsRemoved()
    {
        var (fs, _, service) = World();
        const string source = @"C:\photos\a.jpg";
        fs.AddFile(source, "long source photo");
        fs.CopyFailsAfterBytes = 3;

        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(@"C:\photos\sel\a.jpg"));
    }
}
