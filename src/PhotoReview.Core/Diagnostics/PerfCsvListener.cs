using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PhotoReview.Core.Diagnostics;

/// <summary>
/// Opt-in CSV listener for the <see cref="PhotoReviewPerf"/> EventSource (D03). Only active when the
/// <c>PHOTOREVIEW_PERF_TRACE</c> environment variable names a directory that can be created/written;
/// otherwise <see cref="TryStartFromEnvironment"/> returns null and nothing is created on disk, and
/// app behavior/schema (config.json) is unchanged. Never blocks or throws from event delivery: events
/// are pushed onto a bounded channel that drops (and counts) writes instead of blocking when full, and
/// a background writer drains it to a CSV file.
/// </summary>
public sealed class PerfCsvListener : EventListener, IDisposable
{
    private const string ProviderName = "PhotoReview-Perf";
    private const int ChannelCapacity = 100_000;

    private readonly record struct Row(
        long UtcTicks, long QpcTicks, int ThreadId, string EventName,
        string Nav, string PathId, string A, string B, string C, string D, string Text);

    private Channel<Row>? _channel;
    private StreamWriter? _writer;
    private Task? _writerTask;
    private long _dropped;
    private int _disposed;

    // Only field access needed from OnEventSourceCreated is this constant name check, so it is safe
    // even if this callback fires synchronously from inside the base EventListener constructor
    // (before any instance field initializer of this class has run) — the classic EventListener
    // construction-order gotcha where already-existing EventSources are announced re-entrantly.
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == ProviderName)
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        var channel = _channel;
        if (channel is null) return; // construction not finished yet (see OnEventSourceCreated note); drop silently

        try
        {
            var row = BuildRow(eventData);
            if (!channel.Writer.TryWrite(row))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch
        {
            // Never let a malformed payload take down event delivery.
            Interlocked.Increment(ref _dropped);
        }
    }

    private static Row BuildRow(EventWrittenEventArgs eventData)
    {
        var utcTicks = DateTime.UtcNow.Ticks;
        var qpcTicks = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var eventName = eventData.EventName ?? eventData.EventId.ToString(CultureInfo.InvariantCulture);

        string nav = "";
        string pathId = "";
        var numeric = new string?[4];
        var numericCount = 0;
        var text = new List<string>();

        var names = eventData.PayloadNames;
        var values = eventData.Payload;
        if (names is not null && values is not null)
        {
            for (var i = 0; i < names.Count && i < values.Count; i++)
            {
                var name = names[i];
                var value = values[i];
                if ((name == "nav" || name == "gen") && nav.Length == 0)
                {
                    nav = FormatValue(value);
                }
                else if (name == "pathId" && pathId.Length == 0)
                {
                    pathId = FormatValue(value);
                }
                else if (IsNumeric(value))
                {
                    if (numericCount < numeric.Length) numeric[numericCount] = FormatValue(value);
                    numericCount++;
                }
                else if (value is not null)
                {
                    text.Add(FormatValue(value));
                }
            }
        }

        return new Row(
            utcTicks, qpcTicks, threadId, eventName,
            nav, pathId,
            numeric[0] ?? "", numeric[1] ?? "", numeric[2] ?? "", numeric[3] ?? "",
            string.Join(";", text));
    }

    private static bool IsNumeric(object? value) => value is long or int or double or bool;

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        double d => d.ToString("G17", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Starts a listener writing to <c>PHOTOREVIEW_PERF_TRACE</c>/perf-&lt;pid&gt;-&lt;timestamp&gt;.csv
    /// when that environment variable is set to a directory that can be created and written to.
    /// Returns null (and leaves nothing on disk) when the variable is unset/blank or the directory
    /// cannot be prepared.
    /// </summary>
    public static PerfCsvListener? TryStartFromEnvironment()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE");
        if (string.IsNullOrWhiteSpace(dir)) return null;

        string path;
        FileStream stream;
        try
        {
            Directory.CreateDirectory(dir);
            var pid = Environment.ProcessId;
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            path = Path.Combine(dir, $"perf-{pid}-{ts}.csv");
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
        }
        catch
        {
            return null;
        }

        var listener = new PerfCsvListener();
        try
        {
            listener.Start(stream);
            return listener;
        }
        catch
        {
            listener.Dispose();
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            return null;
        }
    }

    internal long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Test seam: a listener writing to <paramref name="stream"/> with a custom channel capacity.</summary>
    internal static PerfCsvListener StartForTest(Stream stream, int capacity)
    {
        var listener = new PerfCsvListener();
        listener.Start(stream, capacity);
        return listener;
    }

    private void Start(Stream stream, int capacity = ChannelCapacity)
    {
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        // CORE-03: with DropWrite a full channel makes TryWrite return true and silently discard the new item, so the
        // drop must be counted via the itemDropped callback (a false TryWrite only happens after completion).
        _channel = Channel.CreateBounded<Row>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        }, _ => Interlocked.Increment(ref _dropped));

        WriteHeader(_writer);
        _writerTask = Task.Run(WriterLoopAsync);
    }

    private static readonly string[] DiagFlagNames =
        [DiagOptions.PreReadVar, DiagOptions.PreloadWorkersVar, DiagOptions.DisableDiskCacheVar];

    private static void WriteHeader(StreamWriter writer)
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var diagFlags = string.Join(";", DiagFlagNames
            .Select(name => (name, value: Environment.GetEnvironmentVariable(name)))
            .Where(p => !string.IsNullOrEmpty(p.value))
            .Select(p => $"{p.name}={p.value}"));

        writer.WriteLine(FormattableString.Invariant(
            $"# commit={version} diag={diagFlags} qpcFrequency={Stopwatch.Frequency}"));
        writer.WriteLine("utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text");
    }

    private async Task WriterLoopAsync()
    {
        var reader = _channel!.Reader;
        var lastFlush = Stopwatch.GetTimestamp();
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var row))
                {
                    WriteRow(row);
                    if (PhotoReviewPerf.Ms(lastFlush) >= 1000)
                    {
                        FlushWriter();
                        lastFlush = Stopwatch.GetTimestamp();
                    }
                }
            }
        }
        catch
        {
            // Writer must never crash the process; diagnostics are best-effort.
        }
        finally
        {
            FlushWriter();
        }
    }

    private void WriteRow(Row row)
    {
        var writer = _writer;
        if (writer is null) return;
        writer.Write(row.UtcTicks);
        writer.Write(',');
        writer.Write(row.QpcTicks);
        writer.Write(',');
        writer.Write(row.ThreadId);
        writer.Write(',');
        writer.Write(CsvEscape(row.EventName));
        writer.Write(',');
        writer.Write(CsvEscape(row.Nav));
        writer.Write(',');
        writer.Write(CsvEscape(row.PathId));
        writer.Write(',');
        writer.Write(CsvEscape(row.A));
        writer.Write(',');
        writer.Write(CsvEscape(row.B));
        writer.Write(',');
        writer.Write(CsvEscape(row.C));
        writer.Write(',');
        writer.Write(CsvEscape(row.D));
        writer.Write(',');
        writer.Write(CsvEscape(row.Text));
        writer.Write('\n');
    }

    private void FlushWriter()
    {
        try { _writer?.Flush(); } catch { /* best effort */ }
    }

    // R2-F-32: the analyzer reads the file line by line, so a quoted embedded newline would split one row into two
    // malformed ones. Line breaks are therefore flattened to a space (quotes/commas are still RFC-4180 escaped).
    internal static string CsvEscape(string value)
    {
        if (value.Length == 0) return value;
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0) value = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _channel?.Writer.TryComplete();
        try { _writerTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }

        try
        {
            if (_writer is not null)
            {
                _writer.WriteLine(FormattableString.Invariant($"# dropped={Interlocked.Read(ref _dropped)}"));
                _writer.Flush();
                _writer.Dispose();
            }
        }
        catch { /* best effort */ }

        base.Dispose();
    }
}
