using System.Text;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>ADR 0007 / IO03: Fast (default) vs PowerLossSafe journal modes keep identical invariants.</summary>
[Trait("Category", "HotPath")]
public sealed class JournalDurabilityTests
{
    private sealed class NoopRecycleBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public void SendToRecycleBin(string path) => fs.Delete(path); // the shell removes the file (B-06 post-check)
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => true;
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");
    private JournalDurability _mode = JournalDurability.Fast;
    private readonly OperationJournal _journal;
    private readonly FileActionService _service;

    public JournalDurabilityTests()
    {
        _journal = new OperationJournal(_paths, _fs, new SystemClock(), () => _mode);
        _service = new FileActionService(_journal, _fs, new SystemClock(), new NoopRecycleBin(_fs));
    }

    private string JournalText => _fs.ReadAllText(_paths.JournalFile);

    [Fact]
    public void DefaultsToFast_WhenNoProviderGiven()
    {
        var journal = new OperationJournal(_paths, _fs, new SystemClock());
        Assert.Equal(JournalDurability.Fast, journal.Durability);
        Assert.Equal(JournalDurability.Fast, new AppSettings().JournalDurability);
    }

    [Theory]
    [InlineData(JournalDurability.Fast, false)]
    [InlineData(JournalDurability.PowerLossSafe, true)]
    public async Task Move_OpensStreamPerMode_AndOrdersPreparedMutationCommitted(JournalDurability mode, bool expectedDurable)
    {
        _mode = mode;
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "hello photo");
        string? journalAtMutation = null;
        _fs.MoveHook = (_, _) => { journalAtMutation = JournalText; return null; };

        var result = await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, @"C:\photos\sel"));

        Assert.True(result.Succeeded);
        Assert.True(result.JournalPersisted);
        // Prepared is on disk (one complete line) before the mutation; Committed is not there yet.
        Assert.NotNull(journalAtMutation);
        Assert.Contains("\"State\":\"Prepared\"", journalAtMutation, StringComparison.Ordinal);
        Assert.DoesNotContain("Committed", journalAtMutation, StringComparison.Ordinal);
        Assert.EndsWith("\n", journalAtMutation, StringComparison.Ordinal);
        var kinds = _fs.Events.Where(e => e.Kind is "append" or "move").ToList();
        Assert.Equal(["append", "move", "append"], kinds.Select(e => e.Kind));
        Assert.All(kinds.Where(e => e.Kind == "append"), e => Assert.Equal(expectedDurable, e.Durable));
        var lines = JournalText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Prepared", lines[0], StringComparison.Ordinal);
        Assert.Contains("Committed", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recycle_FastMode_WritesNonDurableRecords()
    {
        _fs.WriteAllTextAtomic(@"C:\photos\b.jpg", "x");
        var result = await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\b.jpg", FileOperationType.Recycle, null));
        Assert.True(result.Succeeded);
        Assert.All(_fs.Events.Where(e => e.Kind == "append"), e => Assert.False(e.Durable));
        Assert.Equal(2, _fs.Events.Count(e => e.Kind == "append"));
    }

    [Fact]
    public void SafeMode_PreparedAndMutationRunOffCallerThread_FastModeKeepsPreparedInline()
    {
        // The caller is a dedicated thread that BLOCKS on the result (like a UI thread that awaits): nothing else can run on it, so
        // "off the caller" is exact. A thread-pool test thread cannot serve: it is handed back to the pool at the first await and may
        // then run the queued mutation itself (same ManagedThreadId, a false "ran on the caller"), and a dead thread's id is recycled.
        // Threads are compared by object, never by id.
        Thread? callerThread = null;
        List<InMemoryFileSystem.FsEvent>? safe = null;
        List<InMemoryFileSystem.FsEvent>? fast = null;
        var thread = new Thread(() =>
        {
            callerThread = Thread.CurrentThread;

            _mode = JournalDurability.PowerLossSafe;
            _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "hello");
            _service.ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, @"C:\photos\sel")).GetAwaiter().GetResult();
            safe = _fs.Events.Where(e => e.Kind is "append" or "move").ToList();

            _mode = JournalDurability.Fast;
            _fs.WriteAllTextAtomic(@"C:\photos\c.jpg", "hello");
            _service.ExecuteAsync(new FileActionRequest(@"C:\photos\c.jpg", FileOperationType.Move, @"C:\photos\sel")).GetAwaiter().GetResult();
            fast = _fs.Events.Where(e => e.Kind is "append" or "move").Skip(3).ToList();
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "caller thread hung");

        Assert.Equal(3, safe!.Count);
        Assert.All(safe, e => Assert.NotSame(callerThread, e.Thread));
        Assert.Same(callerThread, fast![0].Thread); // Prepared inline (~0.4 ms cache write)
        Assert.NotSame(callerThread, fast[1].Thread); // mutation always off-thread
    }

    [Fact]
    public async Task ModeSwitch_AppliesToNextWrite_WithoutRecreatingJournal()
    {
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "1");
        _fs.WriteAllTextAtomic(@"C:\photos\b.jpg", "2");
        await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, @"C:\photos\sel"));
        _mode = JournalDurability.PowerLossSafe;
        await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\b.jpg", FileOperationType.Move, @"C:\photos\sel"));

        Assert.Equal([false, false, true, true], _fs.Events.Where(e => e.Kind == "append").Select(e => e.Durable));
    }

    [Theory]
    [InlineData(JournalDurability.Fast)]
    [InlineData(JournalDurability.PowerLossSafe)]
    public void ReadSkipsCorruptTrailingLine(JournalDurability mode)
    {
        _mode = mode;
        var entry = new JournalEntry("ok", FileOperationType.Move, JournalState.Committed, @"C:\a.jpg", @"C:\b\a.jpg", 1, DateTime.UtcNow, DateTime.UtcNow);
        _journal.Append(entry);
        using (var stream = _fs.OpenAppend(_paths.JournalFile, durable: mode == JournalDurability.PowerLossSafe))
        {
            var torn = Encoding.UTF8.GetBytes("{\"Id\":\"torn\",\"Type\":\"Mo"); // power loss mid-line, no newline
            stream.Write(torn, 0, torn.Length);
            stream.Flush();
        }

        Assert.Equal("ok", Assert.Single(_journal.ReadCommittedMoves()).Id);
        Assert.Empty(_journal.ReadPendingOperations());
    }

    [Fact]
    public void OldConfigWithoutJournalDurability_LoadsAsFast()
    {
        var store = new SettingsStore(_paths, _fs, NullLog.Instance);
        _fs.WriteAllTextAtomic(_paths.ConfigFile, """{ "ConfigVersion": 2, "LoadingMode": "Fast" }""");
        var loaded = store.Load();
        Assert.Equal(JournalDurability.Fast, loaded.JournalDurability);
        Assert.Equal(JournalDurability.Fast, store.Current.JournalDurability);
    }

    [Theory]
    [InlineData("PowerLossSafe", JournalDurability.PowerLossSafe)]
    [InlineData("powerlosssafe", JournalDurability.PowerLossSafe)]
    [InlineData("garbage", JournalDurability.Fast)]
    public void JournalDurability_ParsesLeniently(string text, JournalDurability expected)
    {
        var store = new SettingsStore(_paths, _fs, NullLog.Instance);
        _fs.WriteAllTextAtomic(_paths.ConfigFile, "{ \"JournalDurability\": \"" + text + "\" }");
        Assert.Equal(expected, store.Load().JournalDurability);
    }

    [Fact]
    public void Settings_RoundTrip_PreservesPowerLossSafe_AndProviderReadsLive()
    {
        var store = new SettingsStore(_paths, _fs, NullLog.Instance);
        var journal = new OperationJournal(_paths, _fs, new SystemClock(), () => store.Current.JournalDurability);
        Assert.Equal(JournalDurability.Fast, journal.Durability);

        var settings = new AppSettings { JournalDurability = JournalDurability.PowerLossSafe };
        store.Save(settings);
        Assert.Contains("PowerLossSafe", _fs.ReadAllText(_paths.ConfigFile), StringComparison.Ordinal);

        var reloaded = new SettingsStore(_paths, _fs, NullLog.Instance);
        Assert.Equal(JournalDurability.PowerLossSafe, reloaded.Load().JournalDurability);
        Assert.Equal(JournalDurability.PowerLossSafe, journal.Durability); // live, no restart
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalFileSystem_OpenAppend_AppendsInBothModes(bool durable)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-io03-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "j.jsonl");
            var fs = new PhysicalFileSystem();
            for (var i = 0; i < 2; i++)
            {
                using var stream = fs.OpenAppend(path, durable);
                var bytes = Encoding.UTF8.GetBytes("line" + i + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            Assert.Equal("line0\nline1\n", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadPending_SkipsLinesWithoutIdOrSource_AndKeepsValidOnes()
    {
        const string noId = """{"Type":"Move","State":"Prepared","Source":"C:\p\b.jpg","Size":1}""";
        const string noSource = """{"Id":"noSource","Type":"Move","State":"Prepared","Size":1}""";
        using (var stream = _fs.OpenAppend(_paths.JournalFile, durable: false))
        {
            var bytes = Encoding.UTF8.GetBytes(noId + "\n" + noSource + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        _journal.Append(new JournalEntry("ok", FileOperationType.Move, JournalState.Prepared, @"C:\a.jpg", @"C:\b\a.jpg", 1, DateTime.UtcNow, DateTime.UtcNow));

        var pending = _journal.ReadPendingOperations();

        Assert.Equal("ok", Assert.Single(pending).Id);
    }
}
