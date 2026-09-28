using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Pure selection step of journal compaction (<see cref="OperationJournal.TryCompact"/>): which lines of a journal snapshot
/// can be dropped without changing what ANY reader of the journal observes. No I/O.
/// <para><b>Rule.</b> A line is dropped only when it is (a) blank, or (b) a recognized entry (<see cref="JournalLineParser"/>)
/// of an Id whose effective latest entry L (latest-entry resolution with the FA-01 rule, as in
/// <c>OperationJournal.ComputeLatestEntries</c>) is Committed or Dismissed, that lies strictly BEFORE L, and that is not one of
/// the last <see cref="OperationJournal.StartupCommittedMoveLimit"/> committed-Move lines the tail reader of
/// <see cref="OperationJournal.ReadCommittedMoves"/> returns. Everything else is kept byte-for-byte in its original order:
/// every entry of an Id that is still Prepared or Failed, L itself and every line of its Id after L, and every line this
/// build cannot parse (malformed, or an enum value from a newer build). Each entry this plan recognizes is also an entry of
/// <c>OperationJournal.ReadEntries</c> (a line with an inner '\r' is never recognized here); the readers may see MORE entries
/// (an invalid-UTF-8 line they decode leniently), which the argument below does not depend on.</para>
/// <para><b>Why readers cannot tell.</b> Only entries before a Committed/Dismissed L of the same Id disappear. (1) Latest-entry
/// resolution (Recovery list, Dismiss, reconcile): L is always assigned when read (the FA-01 rule only ever skips a Failed),
/// so the state after L and everything later is identical; Prepared/Failed Ids are untouched, so the Recovery list keeps its
/// order. (2) <c>AppendIfStillPending</c> reads an Id's last line: unchanged. (3) <c>AppendIfUnchangedSince(anchor, ownSince)</c>
/// with ownSince holding only Prepared entries (its only callers, <see cref="JournalTransaction"/>): when the anchor's last
/// occurrence is at or after L, the suffix after it is unchanged; otherwise the suffix contains L in both files, and L
/// (Committed/Dismissed) can never equal a Prepared entry, so both answer false. (4) <c>ReadCommittedMoves</c>: its window of
/// the last 200 committed-Move lines is kept, so a compacted file still of at least 1 MiB returns the same list; a smaller
/// one is read in full and returns those same moves plus older ones (a superset, only ever used as Undo's fingerprint fallback).</para>
/// </summary>
internal static class JournalCompactionPlan
{
    internal readonly record struct Result(byte[] Kept, int DroppedLines, int KeptLines);

    /// <param name="snapshot">Journal bytes ending right after a '\n' (the caller cuts a partial last line off).</param>
    public static Result Build(ReadOnlySpan<byte> snapshot)
    {
        // Pass 1: line boundaries and recognized entries.
        var starts = new List<int>();
        var lengths = new List<int>(); // including the terminating '\n'
        var entries = new List<JournalEntry?>();
        var blank = new List<bool>();
        var position = 0;
        while (position < snapshot.Length)
        {
            var newline = snapshot[position..].IndexOf((byte)'\n');
            var length = newline < 0 ? snapshot.Length - position : newline + 1;
            var content = Content(snapshot.Slice(position, length));
            starts.Add(position);
            lengths.Add(length);
            var isBlank = content.Trim(" \t\r"u8).IsEmpty;
            blank.Add(isBlank);
            // OperationJournal.ReadEntries splits lines with StreamReader.ReadLine, which also breaks at a lone '\r'. A line
            // holding one (JSON allows it as whitespace; the writer never emits it) may read differently there, so it is
            // never treated as a recognized entry here: every entry recognized by this plan is one ReadEntries sees too.
            entries.Add(isBlank || content.Contains((byte)'\r') ? null : JournalLineParser.TryParse(content));
            position += length;
        }

        // Effective latest line per Id, with the same FA-01 rule as OperationJournal.ComputeLatestEntries.
        var latest = new Dictionary<string, (int Index, JournalEntry Entry)>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not { } entry) continue;
            if (entry.State == JournalState.Failed && OperationJournal.IsReconcileCode(entry.ErrorCode)
                && latest.TryGetValue(entry.Id, out var previous) && previous.Entry.State == JournalState.Committed)
                continue;
            latest[entry.Id] = (i, entry);
        }

        // The committed-Move lines ReadCommittedMoves' tail reader returns (same line predicate, newest first).
        var committedMoveWindow = new HashSet<int>();
        for (var i = entries.Count - 1; i >= 0 && committedMoveWindow.Count < OperationJournal.StartupCommittedMoveLimit; i--)
        {
            if (OperationJournal.TryParseTailCommittedMove(Content(snapshot.Slice(starts[i], lengths[i]))) is not null)
                committedMoveWindow.Add(i);
        }

        var kept = new MemoryStream(snapshot.Length);
        var dropped = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (IsDroppable(i))
            {
                dropped++;
                continue;
            }
            kept.Write(snapshot.Slice(starts[i], lengths[i]));
        }
        return new Result(kept.ToArray(), dropped, entries.Count - dropped);

        bool IsDroppable(int i)
        {
            if (blank[i]) return true;
            if (entries[i] is not { } entry) return false; // unparseable here: kept verbatim
            var (latestIndex, latestEntry) = latest[entry.Id];
            return latestEntry.State is JournalState.Committed or JournalState.Dismissed
                && i < latestIndex
                && !committedMoveWindow.Contains(i);
        }
    }

    // A line without its "\n" / "\r\n" terminator, as both journal readers see it.
    private static ReadOnlySpan<byte> Content(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == '\n') line = line[..^1];
        if (!line.IsEmpty && line[^1] == '\r') line = line[..^1];
        return line;
    }
}
