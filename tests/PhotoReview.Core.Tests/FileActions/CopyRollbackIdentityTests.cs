using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// R01a/R01b (review 2026-10-04): the proof of creation says THIS operation created the destination, not that the file at the path is
/// still that one when the rollback runs. The cleanup must delete only a destination whose size and write time still match what was
/// observed right after the copy; a foreign replacement planted in between (inside a stat hook, deterministic, no timing) survives.
/// In-memory file system and fakes only; the real Recycle Bin is never touched.
/// </summary>
public sealed class CopyRollbackIdentityTests
{
    private static readonly DateTime ForeignStamp = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string DestJpeg = @"C:\photos\sel\a.jpg";
    private const string DestRaw = @"C:\photos\sel\a.cr2";

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class Paths : IAppPaths
    {
        public string ConfigFile => @"C:\fake\config.json";
        public string JournalFile => @"C:\fake\operations.jsonl";
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

    private static (InMemoryFileSystem Fs, FaultableFileSystem Faulty, FileActionService Service) World()
    {
        var disk = new InMemoryFileSystem();
        var faulty = new FaultableFileSystem(disk);
        var journal = new OperationJournal(new Paths(), faulty, new Clock());
        return (disk, faulty, new FileActionService(journal, faulty, new Clock(), new NeverBin()));
    }

    /// <summary>
    /// Returns an "arm" action to call from the copy hook. From then on <paramref name="plant"/> runs exactly once, right before the
    /// <paramref name="nth"/> time the service looks at <paramref name="path"/> (a stat or an existence check): the 1st look is the
    /// observation right after the copy, the 2nd is the rollback's own check, i.e. a replacement landing in between.
    /// </summary>
    private static Action PlantAtNthLookAfterArm(FaultableFileSystem faulty, string path, int nth, Action plant)
    {
        var armed = false;
        var looks = 0;
        void Look(string p)
        {
            if (armed && string.Equals(p, path, StringComparison.OrdinalIgnoreCase) && ++looks == nth) plant();
        }
        faulty.StatThrowHook = Look;
        faulty.FileExistsHook = Look;
        return () => armed = true;
    }

    // ---- R01b, single Copy ----

    [Fact(DisplayName = "R01b: a single Copy whose verification fails deletes its own incomplete destination")]
    public async Task SingleCopy_VerificationFails_OwnIncompleteDestinationIsRemoved()
    {
        var (fs, _, service) = World();
        fs.AddFile(Jpeg, "long source photo");
        fs.CopyHook = (s, _) => { fs.AddFile(s, "short"); return null; }; // the source shrinks while it is copied

        var result = await service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(DestJpeg));
    }

    [Fact(DisplayName = "R01b: a single Copy keeps a shorter foreign file that replaced the destination after the copy")]
    public async Task SingleCopy_ForeignShorterFileReplacesDestinationBeforeCleanup_ForeignFileSurvives()
    {
        var (fs, faulty, service) = World();
        fs.AddFile(Jpeg, "long source photo");
        var arm = PlantAtNthLookAfterArm(faulty, DestJpeg, 2, () => fs.AddFile(DestJpeg, "foreign", ForeignStamp));
        fs.CopyHook = (s, _) => { arm(); fs.AddFile(s, "short"); return null; };

        var result = await service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.Equal("foreign", fs.ReadAllText(DestJpeg));
    }

    // ---- R01b, group Copy ----

    [Fact(DisplayName = "R01b: a group Copy rollback keeps a same-size foreign file that replaced an already copied member")]
    public async Task GroupCopy_SameSizeForeignFileReplacesCompletedCopy_ForeignFileSurvives()
    {
        var (fs, _, service) = World();
        fs.AddFile(Jpeg, "jpeg");
        fs.AddFile(Raw, "raw");
        fs.CopyHook = (s, _) =>
        {
            if (s != Raw) return null;
            fs.AddFile(DestJpeg, "JPEG", ForeignStamp); // same size as the copy this operation made, different file
            return new IOException("simulated");
        };

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(Jpeg, Raw), FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.Equal("JPEG", fs.ReadAllText(DestJpeg));
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error); // reported as not cleaned up, never deleted
    }

    [Fact(DisplayName = "R01b: a group Copy rollback still removes the copies it made when nothing was replaced")]
    public async Task GroupCopy_NothingReplaced_RollbackRemovesItsOwnCopies()
    {
        var (fs, _, service) = World();
        fs.AddFile(Jpeg, "jpeg");
        fs.AddFile(Raw, "raw");
        fs.CopyHook = (s, _) => s == Raw ? new IOException("simulated") : null;

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(Jpeg, Raw), FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.Equal("simulated", result.Error);
        Assert.False(fs.FileExists(DestJpeg));
    }

    [Fact(DisplayName = "R01b: a group Copy rollback keeps a foreign file that replaced the member whose verification failed")]
    public async Task GroupCopy_ForeignFileReplacesInFlightMemberBeforeRollback_ForeignFileSurvives()
    {
        var (fs, faulty, service) = World();
        fs.AddFile(Jpeg, "jpeg");
        fs.AddFile(Raw, "raw");
        var arm = PlantAtNthLookAfterArm(faulty, DestRaw, 2, () => fs.AddFile(DestRaw, "ff", ForeignStamp));
        fs.CopyHook = (s, _) =>
        {
            if (s != Raw) return null;
            arm();
            fs.AddFile(s, "r"); // the RAW shrinks while it is copied: verification fails
            return null;
        };

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(Jpeg, Raw), FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.Equal("ff", fs.ReadAllText(DestRaw));
        Assert.False(fs.FileExists(DestJpeg)); // the JPEG copy this operation made is still compensated
    }

    // ---- R01a, Recovery retry Copy ----

    [Fact(DisplayName = "R01a: a Recovery Copy retry that throws after raising the proof keeps a shorter foreign replacement")]
    public async Task RecoveryCopy_ThrowsAfterProofAndForeignShorterFileReplacesDestination_ForeignFileSurvives()
    {
        var disk = new InMemoryFileSystem();
        var faulty = new FaultableFileSystem(disk);
        var journal = new OperationJournal(new Paths(), faulty, new Clock());
        const string destination = @"D:\sel\a.jpg";
        disk.AddFile(Jpeg, "long source photo");
        var stat = disk.GetFileStat(Jpeg)!;
        var failed = new JournalEntry("r01a", FileOperationType.Copy, JournalState.Failed, Jpeg, destination, stat.Length, stat.LastWriteUtc, new Clock().UtcNow);
        journal.Append(failed);
        // The copy completed (proof raised), then the call fails and someone else's shorter file is at the destination.
        faulty.AfterCopy = (_, to) =>
        {
            disk.AddFile(to, "foreign", ForeignStamp);
            throw new IOException("simulated");
        };

        var result = await new RecoveryRetryService(journal, faulty, new Clock()).RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("foreign", disk.ReadAllText(destination));
    }
}
