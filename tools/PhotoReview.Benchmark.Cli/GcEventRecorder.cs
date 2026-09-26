using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// Q-R26: records every GC of this process (start/end time, generation, reason) and every execution-engine
/// suspension (the pause the UI thread actually sees) from the runtime's own event source, so a perf-session run can
/// tell which GCs fell inside a navigation. Timestamps are UTC ticks, the same clock as the perf CSV's utcTicks column.
/// </summary>
internal sealed class GcEventRecorder : EventListener
{
    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    // Runtime event ids (ClrEtwAll.man): GCStart_V2, GCEnd_V1, GCRestartEEEnd_V1, GCSuspendEEBegin_V1.
    private const int GcStart = 1;
    private const int GcEnd = 2;
    private const int RestartEeEnd = 3;
    private const int SuspendEeBegin = 9;

    private static readonly string[] Reasons =
    [
        "AllocSmall", "Induced", "LowMemory", "Empty", "AllocLarge", "OutOfSpaceSOH", "OutOfSpaceLOH", "InducedNotForced",
        "Internal", "InducedLowMemory", "InducedCompacting", "LowMemoryHost", "PMFullGC", "LowMemoryHostBlocking",
    ];

    private readonly object _gate = new();
    private readonly List<GcRecord> _gcs = [];
    private readonly List<(long Start, long End)> _pauses = [];
    private long _suspendStart;
    private bool _enabled;

    private sealed class GcRecord
    {
        public long Start;
        public long End;
        public int Number;
        public int Depth;
        public string Reason = "";
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == RuntimeSource)
            EnableEvents(eventSource, EventLevel.Informational, GcKeyword);
    }

    /// <summary>Clears what was recorded so far and starts recording (one iteration).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _gcs.Clear();
            _pauses.Clear();
            _suspendStart = 0;
            _enabled = true;
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        var ticks = eventData.TimeStamp.ToUniversalTime().Ticks;
        var payload = eventData.Payload;
        lock (_gate)
        {
            if (!_enabled) return;
            switch (eventData.EventId)
            {
                case GcStart when payload is { Count: >= 3 }:
                    var reason = Convert.ToInt32(payload[2], CultureInfo.InvariantCulture);
                    _gcs.Add(new GcRecord
                    {
                        Start = ticks,
                        Number = Convert.ToInt32(payload[0], CultureInfo.InvariantCulture),
                        Depth = Convert.ToInt32(payload[1], CultureInfo.InvariantCulture),
                        Reason = reason >= 0 && reason < Reasons.Length ? Reasons[reason] : reason.ToString(CultureInfo.InvariantCulture),
                    });
                    break;
                case GcEnd when payload is { Count: >= 1 }:
                    var number = Convert.ToInt32(payload[0], CultureInfo.InvariantCulture);
                    for (var i = _gcs.Count - 1; i >= 0; i--)
                    {
                        if (_gcs[i].Number != number) continue;
                        _gcs[i].End = ticks;
                        break;
                    }
                    break;
                case SuspendEeBegin:
                    _suspendStart = ticks;
                    break;
                case RestartEeEnd:
                    if (_suspendStart != 0) _pauses.Add((_suspendStart, ticks));
                    _suspendStart = 0;
                    break;
            }
        }
    }

    /// <summary>The GCs recorded so far (generation and reason), for tests.</summary>
    internal IReadOnlyList<(int Gen, string Reason)> Snapshot()
    {
        lock (_gate) return [.. _gcs.Select(g => (g.Depth, g.Reason))];
    }

    /// <summary>Stops recording and writes one CSV row per GC, with the EE suspensions that overlapped it.</summary>
    public void WriteCsv(string path)
    {
        List<GcRecord> gcs;
        List<(long Start, long End)> pauses;
        lock (_gate)
        {
            _enabled = false;
            gcs = [.. _gcs];
            pauses = [.. _pauses];
        }

        var text = new StringBuilder("startUtcTicks,endUtcTicks,gc,gen,reason,gcMs,pauseMs").AppendLine();
        foreach (var gc in gcs)
        {
            var end = gc.End == 0 ? gc.Start : gc.End;
            // A blocking GC runs inside one suspension; a background gen2 overlaps a few short ones.
            var pauseTicks = pauses.Where(p => p.End >= gc.Start && p.Start <= end).Sum(p => p.End - p.Start);
            text.Append(gc.Start.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(end.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(gc.Number.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(gc.Depth.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(gc.Reason).Append(',')
                .Append(((end - gc.Start) / 10_000.0).ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append((pauseTicks / 10_000.0).ToString("F3", CultureInfo.InvariantCulture))
                .AppendLine();
        }
        File.WriteAllText(path, text.ToString());
    }
}
