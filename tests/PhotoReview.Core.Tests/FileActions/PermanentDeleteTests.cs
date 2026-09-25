using System.IO;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Q-R8: permanent delete on drives without a Recycle Bin (opt-in). Fakes only: no real Recycle Bin or removable drive is touched.</summary>
public sealed class PermanentDeleteTests
{
    private const string Journal = @"C:\data\operations.jsonl";
    private const string Photo = @"E:\usb\a.jpg";
    private static readonly DateTime Stamp = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow => Stamp;
        public long Timestamp => 0;
    }

    private sealed class FakePaths : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => Journal;
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private sealed class FakeBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public bool HasRecycleBin { get; set; }
        public List<string> Recycled { get; } = [];
        public List<string> Permanent { get; } = [];
        public int RestoreCalls { get; private set; }

        public bool CanRecycle(string path) => HasRecycleBin;

        public void SendToRecycleBin(string path)
        {
            if (!HasRecycleBin) throw new IOException("no recycle bin");
            Recycled.Add(path);
            fs.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            if (HasRecycleBin) throw new InvalidOperationException("fixed drive must never be deleted permanently");
            Permanent.Add(path);
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls++;
            return true;
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeBin _bin;
    private readonly OperationJournal _journal;
    private readonly FileActionService _service;
    private readonly UndoService _undo;

    public PermanentDeleteTests()
    {
        _bin = new FakeBin(_fs);
        _journal = new OperationJournal(new FakePaths(), _fs, new FakeClock());
        _service = new FileActionService(_journal, _fs, new FakeClock(), _bin);
        _undo = new UndoService(_journal, _fs, _bin, _service);
        _fs.AddFile(Photo, new byte[10], Stamp);
    }

    private List<JournalEntry> JournalEntries() =>
        _fs.FileExists(Journal)
            ? [.. _fs.ReadAllText(Journal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => JsonSerializer.Deserialize<JournalEntry>(l)!)]
            : [];

    [Fact(DisplayName = "No Recycle Bin and permanent delete not allowed: refused, file kept, nothing journaled")]
    public async Task Recycle_NoRecycleBinNotAllowed_RefusesAndKeepsFile()
    {
        var result = await _service.ExecuteAsync(new FileActionRequest(Photo, FileOperationType.Recycle));

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecycleUnsupportedDrive("a.jpg"), result.Error);
        Assert.True(_fs.FileExists(Photo));
        Assert.Empty(_bin.Permanent);
        Assert.Empty(_bin.Recycled);
        Assert.Empty(JournalEntries());
    }

    [Fact(DisplayName = "No Recycle Bin and allowed: deletes permanently; Prepared and Committed entries are marked Permanent")]
    public async Task Recycle_NoRecycleBinAllowed_DeletesPermanentlyAndJournalsTruthfully()
    {
        var result = await _service.ExecuteAsync(new FileActionRequest(Photo, FileOperationType.Recycle, AllowPermanentDelete: true));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.PermanentlyDeleted);
        Assert.False(_fs.FileExists(Photo));
        Assert.Equal(Photo, Assert.Single(_bin.Permanent));
        Assert.Empty(_bin.Recycled);
        var entries = JournalEntries();
        Assert.Equal([JournalState.Prepared, JournalState.Committed], entries.Select(e => e.State));
        Assert.All(entries, e => Assert.True(e.Permanent));
    }

    [Fact(DisplayName = "Fixed drive (has a Recycle Bin): unchanged, recycled, entry not marked Permanent, flag ignored")]
    public async Task Recycle_FixedDrive_UnchangedEvenWithFlag()
    {
        _bin.HasRecycleBin = true;

        var result = await _service.ExecuteAsync(new FileActionRequest(Photo, FileOperationType.Recycle, AllowPermanentDelete: true));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.PermanentlyDeleted);
        Assert.Equal(Photo, Assert.Single(_bin.Recycled));
        Assert.Empty(_bin.Permanent);
        Assert.All(JournalEntries(), e => Assert.Null(e.Permanent));
        Assert.DoesNotContain("Permanent", _fs.ReadAllText(Journal), StringComparison.Ordinal); // omitted when null: old shape kept
    }

    [Fact(DisplayName = "Undo after a permanent delete reports it cannot restore and never asks the Recycle Bin")]
    public async Task Undo_AfterPermanentDelete_SaysCannotRestore()
    {
        var result = await _service.ExecuteAsync(new FileActionRequest(Photo, FileOperationType.Recycle, AllowPermanentDelete: true));
        _undo.Register(result);

        var undo = await _undo.UndoLastAsync();

        Assert.False(undo.Succeeded);
        Assert.Equal(Tr.CoreUndoPermanentlyDeleted("a.jpg"), undo.ErrorMessage);
        Assert.Equal(0, _bin.RestoreCalls);
    }

    [Fact(DisplayName = "Undo after a normal recycle still restores through the Recycle Bin")]
    public async Task Undo_AfterNormalRecycle_StillRestores()
    {
        _bin.HasRecycleBin = true;
        var result = await _service.ExecuteAsync(new FileActionRequest(Photo, FileOperationType.Recycle));
        _undo.Register(result);

        var undo = await _undo.UndoLastAsync();

        Assert.True(undo.Succeeded);
        Assert.Equal(1, _bin.RestoreCalls);
    }

    [Theory(DisplayName = "Recovery verdict for a Recycle entry: permanent + source missing = PermanentlyDeleted")]
    [InlineData(true, false, RecoveryVerdict.PermanentlyDeleted)]
    [InlineData(null, false, RecoveryVerdict.RecycleUnverifiable)]
    [InlineData(true, true, RecoveryVerdict.NotRecycled)]
    public void Recovery_PermanentRecycle_VerdictReflectsPermanence(bool? permanent, bool sourcePresent, RecoveryVerdict expected)
    {
        var fs = new InMemoryFileSystem();
        if (sourcePresent) fs.AddFile(Photo, new byte[10], Stamp);
        var entry = new JournalEntry("id", FileOperationType.Recycle, JournalState.Committed, Photo, null, 10, Stamp, Stamp, Permanent: permanent);

        Assert.Equal(expected, new RecoveryFileCheck(fs).Check(entry).Verdict);
    }

    [Fact(DisplayName = "PermanentlyDeleted verdict has a stable code")]
    public void Code_PermanentlyDeleted_IsStable() =>
        Assert.Equal("PermanentlyDeleted", RecoveryFileCheck.Code(RecoveryVerdict.PermanentlyDeleted));

    [Fact(DisplayName = "Setting defaults to off, round-trips through SettingsStore and an absent key loads as off")]
    public void Setting_DefaultsOff_RoundTrips()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var store = new SettingsStore(paths, fs, PhotoReview.Core.Diagnostics.NullLog.Instance, (_, _) => { });
        Assert.False(new AppSettings().AllowPermanentDeleteWithoutRecycleBin);

        fs.WriteAllTextAtomic(paths.ConfigFile, "{ \"LoggingEnabled\": true }");
        Assert.False(store.Load().AllowPermanentDeleteWithoutRecycleBin);

        var on = new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true };
        store.Save(on);
        var reloaded = new SettingsStore(paths, fs, PhotoReview.Core.Diagnostics.NullLog.Instance, (_, _) => { }).Load();
        Assert.True(reloaded.AllowPermanentDeleteWithoutRecycleBin);
        Assert.Empty(SettingsNormalizer.Normalize(reloaded));
    }

    [Fact(DisplayName = "A malformed value for the setting never turns it on")]
    public void Setting_MalformedValue_LoadsAsOff()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        fs.WriteAllTextAtomic(paths.ConfigFile, "{ \"AllowPermanentDeleteWithoutRecycleBin\": \"yes please\" }");

        var loaded = new SettingsStore(paths, fs, PhotoReview.Core.Diagnostics.NullLog.Instance, (_, _) => { }).Load();

        Assert.False(loaded.AllowPermanentDeleteWithoutRecycleBin);
    }
}
