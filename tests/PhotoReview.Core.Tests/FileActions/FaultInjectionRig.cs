using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Monotonic fake clock: every read is one second later, so journal lines of one run are totally ordered.</summary>
internal sealed class StepClock : IClock
{
    private long _ticks;
    public DateTime UtcNow => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Interlocked.Increment(ref _ticks));
}

/// <summary>
/// Fake Recycle Bin (never the real one). SEND and RESTORE are numbered mutations of the fault injector, so a crash can hit
/// "deleted from the folder but not yet in the bin"-style windows exactly as the injector decides; the bin's content survives a
/// "process restart" because it is OS state, not process state.
/// </summary>
internal sealed class FaultBin(FaultInjectionFileSystem fs) : IRecycleBin
{
    public sealed record Item(string Path, string Text, FileStat Stat);

    public List<Item> Items { get; } = [];

    public void SendToRecycleBin(string path)
    {
        var stat = fs.GetFileStat(path) ?? throw new FileNotFoundException("gone", path);
        var text = fs.ReadAllText(path);
        fs.Mutate("recycle", () =>
        {
            fs.Disk.Delete(path);
            Items.Add(new Item(path, text, stat));
        });
    }

    public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
    {
        var item = Items.FirstOrDefault(i => string.Equals(i.Path, originalPath, StringComparison.OrdinalIgnoreCase)
            && i.Stat.Length == expectedSize && i.Stat.LastWriteUtc == expectedLastWriteUtc);
        if (item is null || fs.FileExists(originalPath)) return false;
        try
        {
            fs.Mutate("restore", () =>
            {
                fs.Disk.AddFile(originalPath, item.Text, item.Stat.LastWriteUtc);
                Items.Remove(item);
            });
        }
        catch (IOException) when (!fs.Dead)
        {
            return false; // like WindowsRecycleBin.TryRestore, which turns every shell failure into "false" and never throws
        }

        return true;
    }
}

/// <summary>
/// One simulated machine for a crash-consistency scenario: an in-memory disk behind a <see cref="FaultInjectionFileSystem"/>, a fake
/// Recycle Bin and the services under test. Every photo has a unique file name and a unique content derived from it, which is what
/// lets the invariants tell "lost", "duplicated" and "overwritten" apart. <see cref="Restart"/> abandons every in-memory object (the
/// process died) and builds new ones over what survived on disk.
/// </summary>
internal sealed class FaultRig
{
    public const string Photos = @"C:\photos";
    public const string Sel = @"C:\photos\sel";
    public const string Other = @"C:\other";
    public static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime BaseStamp = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, string> _contentByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _seedPaths = [];

    public InMemoryFileSystem Disk { get; } = new();
    public FaultInjectionFileSystem Fs { get; }
    public FaultBin Bin { get; }
    public StepClock Clock { get; } = new();
    public OperationJournal Journal { get; private set; }
    public FileActionService Service { get; private set; }
    public UndoService Undo { get; private set; }

    /// <summary>Every result an action of the scenario returned (checked against the disk right after the scenario).</summary>
    public List<object> Outcomes { get; } = [];

    /// <summary>True in the single-Delete undo scenario: Ctrl+Z restores the file without any journal line, so its Committed Delete line legitimately stays.</summary>
    public bool SingleRecycleMayBeRestored { get; set; }

    public FaultRig()
    {
        Fs = new FaultInjectionFileSystem(Disk);
        Bin = new FaultBin(Fs);
        Journal = NewJournal();
        Service = new FileActionService(Journal, Fs, Clock, Bin);
        Undo = new UndoService(Journal, Fs, Bin, Service, clock: Clock);
    }

    public IReadOnlyList<string> SeedPaths => _seedPaths;

    public IEnumerable<string> Names => _contentByName.Keys;

    public string ContentOf(string pathOrName) => _contentByName[Path.GetFileName(pathOrName)];

    public bool KnownName(string path) => _contentByName.ContainsKey(Path.GetFileName(path));

    public static IEnumerable<string> Folders => [Photos, Sel, Other];

    public FaultRig Seed(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            var content = name + ":" + new string('#', 12 + _contentByName.Count);
            _contentByName[name] = content;
            _seedPaths.Add(path);
            Disk.AddFile(path, content, BaseStamp.AddDays(_contentByName.Count));
        }

        return this;
    }

    public OperationJournal NewJournal() => new(Paths, Fs, Clock, appendRetryDelay: _ => { });

    /// <summary>The process died (or is about to be replaced): healthy file system, brand-new in-memory objects on the surviving disk.</summary>
    public OperationJournal Restart()
    {
        Fs.Arm(null);
        Journal = NewJournal();
        Service = new FileActionService(Journal, Fs, Clock, Bin);
        Undo = new UndoService(Journal, Fs, Bin, Service, clock: Clock);
        return Journal;
    }

    public static CaptureGroup Capture(string folder = Photos, string stem = "a", bool xmp = true) =>
        new(Path.Combine(folder, stem + ".jpg"), Path.Combine(folder, stem + ".cr2"), xmp ? Path.Combine(folder, stem + ".xmp") : null);

    /// <summary>Every journal line in file order (torn lines skipped).</summary>
    public List<JournalEntry> JournalLines()
    {
        if (!Disk.FileExists(Paths.JournalFile)) return [];
        return Disk.ReadAllText(Paths.JournalFile).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JournalLineParser.TryParse(line.TrimEnd('\r')))
            .OfType<JournalEntry>().ToList();
    }

    public string JournalText() => Disk.FileExists(Paths.JournalFile) ? Disk.ReadAllText(Paths.JournalFile) : string.Empty;

    /// <summary>Latest line of every Id (the same "last line wins" rule the journal uses).</summary>
    public Dictionary<string, JournalEntry> Latest()
    {
        var latest = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        foreach (var line in JournalLines()) latest[line.Id] = line;
        return latest;
    }

    public static IReadOnlyList<JournalGroupMember> MembersOf(JournalEntry entry) =>
        entry.GroupMembers is { Count: > 0 } members
            ? members
            : [new JournalGroupMember(entry.Source, entry.Destination, entry.Size, entry.LastWriteUtc)];

    /// <summary>Path of the file when it sits there complete (exact original content), else null.</summary>
    public bool HasFull(string path) => Disk.FileExists(path) && Disk.ReadAllText(path) == ContentOf(path);

    public int BinCount(string path) =>
        Bin.Items.Count(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) && item.Text == ContentOf(path));
}
