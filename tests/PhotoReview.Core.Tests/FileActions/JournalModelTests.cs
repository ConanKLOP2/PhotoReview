using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Randomized state-machine test: a seeded random sequence of appends (all states, retries under one Id), dismissals,
/// crashes that leave a torn last line, restarts (fresh journal instance) and reconciles is applied to the real
/// <see cref="OperationJournal"/> and to a tiny reference model (latest record per Id wins; reconcile verdict from the
/// file-existence rules). Every observable read must agree after every step. Padded seeds push the file over the
/// 1 MB threshold so the reverse (tail) reader is compared against the same model.
/// </summary>
public sealed class JournalModelTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int FullScanThresholdBytes = 1024 * 1024;

    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; set; } = T0.AddDays(30);
    }

    public static TheoryData<int, int> Seeds()
    {
        var data = new TheoryData<int, int>();
        for (var seed = 1; seed <= 25; seed++) data.Add(seed, 0);
        for (var seed = 100; seed < 101; seed++) data.Add(seed, 3500); // journal grows past 1 MB: reverse tail reader
        return data;
    }

    [Theory(DisplayName = "Journal agrees with a reference model across random appends, dismissals, torn tails, restarts and reconciles")]
    [MemberData(nameof(Seeds))]
    public void RandomHistory_MatchesReferenceModel(int seed, int padBytes)
    {
        var rng = new Random(seed);
        var disk = new InMemoryFileSystem();
        var clock = new Clock();
        var journal = new OperationJournal(Paths, disk, clock);
        var latest = new Dictionary<string, JournalEntry>();
        var committedMoves = new List<JournalEntry>();
        var order = new List<string>();
        var nextId = 0;
        if (padBytes > 0)
        {
            // A history already past the 1 MB threshold (mixed Move/Copy so the tail reader has to skip lines).
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < 400; i++)
            {
                var type = i % 3 == 0 ? FileOperationType.Copy : FileOperationType.Move;
                var entry = new JournalEntry("pre" + i, type, JournalState.Committed, $@"C:\old\{i}.jpg", $@"C:\old\sel\{i}.jpg", 10, T0, T0, new string('p', padBytes));
                text.Append(System.Text.Json.JsonSerializer.Serialize(entry)).AppendLine();
                latest[entry.Id] = entry;
                order.Add(entry.Id);
                if (type == FileOperationType.Move) committedMoves.Add(entry);
            }
            disk.AddFile(Paths.JournalFile, text.ToString());
        }
        var reverseReaderUsed = false;
        var steps = 120;

        void Record(JournalEntry entry)
        {
            journal.Append(entry);
            latest[entry.Id] = entry;
            if (!order.Contains(entry.Id)) order.Add(entry.Id);
            if (entry is { Type: FileOperationType.Move, State: JournalState.Committed }) committedMoves.Add(entry);
        }

        JournalEntry Fresh()
        {
            var id = "op" + nextId++;
            var type = (FileOperationType)rng.Next(3);
            var source = $@"C:\photos\{id}.jpg";
            var destination = type == FileOperationType.Recycle ? null : $@"C:\photos\sel\{id}.jpg";
            const long size = 10;
            if (rng.Next(2) == 0) disk.AddFile(source, new string('x', (int)size), T0);
            if (destination is not null && rng.Next(2) == 0) disk.AddFile(destination, new string('x', (int)(rng.Next(4) == 0 ? size + 1 : size)), T0);
            var error = padBytes > 0 ? new string('p', padBytes) : null;
            return new JournalEntry(id, type, JournalState.Prepared, source, destination, size, T0, clock.UtcNow, error);
        }

        for (var step = 0; step < steps; step++)
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            var action = rng.Next(10);
            if (action <= 2 || order.Count == 0)
            {
                Record(Fresh());
            }
            else if (action <= 4)
            {
                // A later state (or a retry) under an existing Id.
                var previous = latest[order[rng.Next(order.Count)]];
                var state = (JournalState)rng.Next(4);
                Record(previous with { State = state, TimestampUtc = clock.UtcNow, Error = state == JournalState.Failed ? "boom" : null, ErrorCode = null });
            }
            else if (action == 5)
            {
                // Dismiss from a possibly stale copy of the entry.
                var previous = latest[order[rng.Next(order.Count)]];
                journal.Dismiss([previous]);
                latest[previous.Id] = previous with { State = JournalState.Dismissed };
            }
            else if (action == 6)
            {
                // Crash mid-append: a torn last line, then a restarted process.
                var text = disk.FileExists(Paths.JournalFile) ? disk.ReadAllText(Paths.JournalFile) : string.Empty;
                disk.AddFile(Paths.JournalFile, text + "{\"Id\":\"torn\",\"Ty");
                journal = new OperationJournal(Paths, disk, clock);
            }
            else if (action == 7)
            {
                journal = new OperationJournal(Paths, disk, clock); // restart without a crash
            }
            else
            {
                // Reconcile: verdicts from the documented file rules, applied to the model.
                var expected = new Dictionary<string, JournalState>();
                foreach (var pending in latest.Values.Where(e => e.State == JournalState.Prepared))
                {
                    var sourceExists = disk.FileExists(pending.Source);
                    var destinationOk = pending.Destination is not null && disk.GetFileStat(pending.Destination) is { } stat && stat.Length == pending.Size;
                    expected[pending.Id] = pending.Type switch
                    {
                        FileOperationType.Recycle => sourceExists ? JournalState.Failed : JournalState.Committed,
                        FileOperationType.Move => !sourceExists && destinationOk ? JournalState.Committed : JournalState.Failed,
                        _ => destinationOk ? JournalState.Committed : JournalState.Failed,
                    };
                }

                var reconciled = journal.ReconcilePendingOperations();

                Assert.Equal(expected.Count, reconciled.Count);
                foreach (var entry in reconciled)
                {
                    Assert.Equal(expected[entry.Id], entry.State);
                    latest[entry.Id] = entry;
                    if (entry is { Type: FileOperationType.Move, State: JournalState.Committed }) committedMoves.Add(entry);
                }
                Assert.Empty(journal.ReadPendingOperations());
            }

            var view = journal.ReadPendingAndFailedOperations();
            var modelView = latest.Values.Where(e => e.State is JournalState.Prepared or JournalState.Failed)
                .OrderBy(e => e.State).ThenBy(e => order.IndexOf(e.Id)).Select(e => (e.Id, e.State)).ToList();
            Assert.Equal(modelView, view.Select(e => (e.Id, e.State)).ToList());

            var reverseReader = disk.FileExists(Paths.JournalFile) && disk.ReadAllText(Paths.JournalFile).Length >= FullScanThresholdBytes;
            reverseReaderUsed |= reverseReader;
            var modelMoves = reverseReader ? committedMoves.TakeLast(200) : committedMoves;
            Assert.True(
                journal.ReadCommittedMoves().Select(m => m.Id).SequenceEqual(modelMoves.Select(m => m.Id)),
                $"seed {seed} step {step}: committed moves differ from the model (reverse reader: {reverseReader}); got {journal.ReadCommittedMoves().Count} [{string.Join(",", journal.ReadCommittedMoves().Select(m => m.Id).Skip(60).Take(30))}] want {modelMoves.Count()} [{string.Join(",", modelMoves.Take(3).Select(m => m.Id))}]");
        }

        Assert.Equal(padBytes > 0, reverseReaderUsed); // the padded seeds must really cover the >1 MB tail reader
    }
}
