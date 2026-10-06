using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Journal compaction (the one step that REWRITES the append-only journal at startup) interrupted at every one of its file operations:
/// a crash or failure before, during or after each step must leave a journal whose latest verdict of every Id is exactly what it was,
/// that parses completely, that a later compaction can still handle, and whose pending/failed items are still there for Recovery.
/// In-memory only.
/// </summary>
public sealed class JournalCompactionFaultInjectionTests
{
    private static readonly DateTime Stamp = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The compaction primitives over the fault injector: staging file writes, the fsync and the atomic rename are numbered mutations.</summary>
    private sealed class FaultCompactionFiles(FaultInjectionFileSystem fs) : IJournalCompactionFiles
    {
        public Stream OpenReadDenyWriters(string path) => fs.OpenReadShared(path);

        public IStagedReplacement CreateStagedReplacement(string tempPath)
        {
            fs.Mutate("stage-create", () => fs.Disk.AddFile(tempPath, string.Empty, Stamp));
            return new Staged(fs, tempPath);
        }

        private sealed class Staged(FaultInjectionFileSystem fs, string tempPath) : IStagedReplacement
        {
            private readonly MemoryStream _bytes = new();
            private bool _replaced;

            public void Write(ReadOnlySpan<byte> bytes) => _bytes.Write(bytes);

            public void FlushToDisk() =>
                fs.Mutate("stage-flush", () => fs.Disk.AddFile(tempPath, _bytes.ToArray(), Stamp));

            public void ReplaceAtomically(string destination) =>
                fs.Mutate("replace", () =>
                {
                    fs.Disk.WriteAllTextAtomic(destination, Encoding.UTF8.GetString(_bytes.ToArray()));
                    fs.Disk.Delete(tempPath);
                    _replaced = true;
                });

            public void Dispose()
            {
                if (_replaced || fs.Dead) return; // a dead process cleans nothing up
                try { fs.Disk.Delete(tempPath); } catch (IOException) { }
            }
        }
    }

    private static string Line(string id, JournalState state, string source, string? destination = null) =>
        JsonSerializer.Serialize(new JournalEntry(id, FileOperationType.Move, state, source, destination, 1234, Stamp, Stamp.AddSeconds(1),
            state == JournalState.Failed ? "boom" : null)) + "\n";

    /// <summary>A journal over the 1 MB compaction threshold: thousands of Prepared+Committed pairs (the Prepared lines are droppable),
    /// then one Prepared and one Failed line that must survive untouched.</summary>
    private static (InMemoryFileSystem Disk, FaultInjectionFileSystem Fs) NewJournalFile()
    {
        var disk = new InMemoryFileSystem();
        var text = new StringBuilder();
        for (var i = 0; i < 4500; i++)
        {
            var id = "op" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            var source = @"C:\photos\2026\summer-holiday\IMG_" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".jpg";
            var destination = @"C:\photos\2026\summer-holiday\selected\IMG_" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".jpg";
            text.Append(Line(id, JournalState.Prepared, source, destination));
            text.Append(Line(id, JournalState.Committed, source, destination));
        }

        text.Append(Line("pending", JournalState.Prepared, @"C:\photos\p.jpg", @"C:\photos\sel\p.jpg"));
        text.Append(Line("failed", JournalState.Failed, @"C:\photos\f.jpg", @"C:\photos\sel\f.jpg"));
        disk.AddFile(FaultRig.Paths.JournalFile, text.ToString(), Stamp);
        return (disk, new FaultInjectionFileSystem(disk));
    }

    private static Dictionary<string, string> Latest(InMemoryFileSystem disk, out int unparsable)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        unparsable = 0;
        foreach (var raw in disk.ReadAllText(FaultRig.Paths.JournalFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (JournalLineParser.TryParse(raw.TrimEnd('\r')) is { } entry) latest[entry.Id] = $"{entry.State}|{entry.Source}|{entry.Destination}";
            else unparsable++;
        }

        return latest;
    }

    private static OperationJournal Journal(FaultInjectionFileSystem fs) =>
        new(FaultRig.Paths, fs, new StepClock(), appendRetryDelay: _ => { }, compactionFiles: new FaultCompactionFiles(fs));

    [Fact(DisplayName = "Compaction interrupted at every file operation: every verdict survives, the journal still parses and can be compacted again")]
    public void Compaction_FaultAtEveryStep_KeepsEveryVerdict()
    {
        var (probeDisk, probeFs) = NewJournalFile();
        var before = Latest(probeDisk, out _);
        probeFs.Arm(null);
        var probe = Journal(probeFs).TryCompact();
        Assert.Equal(JournalCompactionOutcome.Compacted, probe.Outcome);
        var calls = probeFs.Kinds.ToArray();
        Assert.True(calls.Length >= 3, "compaction must stage, flush and rename");
        Assert.Equal(before, Latest(probeDisk, out var probeBad)); // the healthy run is lossless
        Assert.Equal(0, probeBad);
        Assert.True(probe.BytesAfter < probe.BytesBefore, "the fixture must really shrink the journal");

        var violations = new List<string>();
        var runs = 0;
        for (var point = 1; point <= calls.Length; point++)
        {
            foreach (var kind in FaultInjectionMatrix.Kinds)
            {
                if (!FaultInjectionFileSystem.Applies(kind, calls[point - 1])) continue;
                runs++;
                var ctx = $"compaction [{kind} at call #{point}/{calls.Length} = {calls[point - 1]}]";
                var (disk, fs) = NewJournalFile();
                fs.Arm(new FaultPlan(point, kind));
                var result = Journal(fs).TryCompact(); // never throws for I/O
                if (kind is FaultKind.CrashBefore or FaultKind.FailOnce && result.Outcome == JournalCompactionOutcome.Compacted)
                    violations.Add($"{ctx}: reported Compacted although its own step failed");

                // Restart: healthy file system, new journal object.
                fs.Arm(null);
                var after = Latest(disk, out var bad);
                if (bad != 0) violations.Add($"{ctx}: {bad} journal line(s) no longer parse");
                foreach (var (id, verdict) in before)
                {
                    if (!after.TryGetValue(id, out var now)) violations.Add($"{ctx}: the verdict of {id} VANISHED");
                    else if (now != verdict) violations.Add($"{ctx}: the verdict of {id} changed from {verdict} to {now}");
                }

                var again = Journal(fs).TryCompact();
                if (again.Outcome == JournalCompactionOutcome.Failed)
                    violations.Add($"{ctx}: a later compaction on the surviving files FAILED: {again.Error}");
                var final = Latest(disk, out var finalBad);
                if (finalBad != 0 || before.Any(pair => !final.TryGetValue(pair.Key, out var v) || v != pair.Value))
                    violations.Add($"{ctx}: after the second compaction a verdict is lost or a line is unparsable");
                var pending = Journal(fs).ReadPendingAndFailedOperations().Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                if (!pending.SequenceEqual(["failed", "pending"]))
                    violations.Add($"{ctx}: the Recovery items are {string.Join(",", pending)} instead of failed,pending");
            }
        }

        Assert.True(runs >= calls.Length, $"only {runs} compaction fault runs");
        Assert.True(violations.Count == 0, $"{violations.Count} violation(s) over {runs} compaction fault runs:{Environment.NewLine}" + string.Join(Environment.NewLine, violations.Take(25)));
    }
}
