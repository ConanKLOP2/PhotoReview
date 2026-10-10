using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PhotoReview.App;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Integration.Tests.Infrastructure;

/// <summary>
/// Audit D-03: a failure inside a deliberately fire-and-forget task (<c>TaskLogging.FireAndLog</c>, the presenter, the
/// preload hand-off, ...) is only <c>AppLog.Error</c> -- nothing else surfaces it, so a UI test stayed green while the app
/// logged a real error. This guard makes every <c>StaTestHost.RunAsync</c> body red when the app logged an Error through
/// <see cref="AppLog"/> and the test did not announce it with <c>StaTestHost.ExpectLoggedError</c>.
/// <para>
/// <see cref="FileLog"/> is sealed and drops every entry while disabled (INV-10), so the guard swaps
/// <see cref="AppLog.Instance"/> for its own enabled <see cref="FileLog"/> on a private temp file and reads back what was
/// appended since the pump started. Only <c>AppLog</c> callers are seen; code that holds <c>FileLog.Default</c> / an injected
/// <c>ILog</c> directly is not (a known limit, documented in docs/TESTING.md).
/// </para>
/// </summary>
internal sealed partial class AppLogErrorGuard
{
    private readonly FileLog _log;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Expectation> _expected = [];
    private long _mark;

    private sealed class Expectation(string substring, bool mustOccur)
    {
        public string Substring { get; } = substring;
        public bool MustOccur { get; } = mustOccur;
        public bool Seen { get; set; }
    }

    private AppLogErrorGuard(FileLog log, string path)
    {
        _log = log;
        _path = path;
    }

    /// <summary>Redirects <see cref="AppLog"/> to a private, enabled log for the rest of the process.</summary>
    public static AppLogErrorGuard InstallForProcess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"photoreview-uitest-applog-{Environment.ProcessId}.log");
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var log = new FileLog(path);
        AppLog.Instance = log;
        log.Enabled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { log.Shutdown(); File.Delete(path); } catch (Exception) { /* best effort temp cleanup */ }
        };
        return new AppLogErrorGuard(log, path);
    }

    /// <summary>Starts a pump: drops earlier expectations and remembers the end of the log.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (!_log.Enabled) _log.Enabled = true; // something (a benchmark window, a settings save) switched logging off
            _log.Flush();
            _mark = LengthOrZero();
            _expected.Clear();
        }
    }

    /// <summary>Allows an Error whose text (message or exception) contains <paramref name="substring"/> in the current pump.</summary>
    public void Expect(string substring, string reason, bool mustOccur)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate) _expected.Add(new Expectation(substring, mustOccur));
    }

    /// <summary>The failure to report for this pump (null when every logged Error was announced and every required one occurred).</summary>
    public InvalidOperationException? End(Exception? bodyFailure)
    {
        lock (_gate)
        {
            _log.Flush();
            var errors = ReadErrorsSinceMark();
            var unexpected = new List<string>();
            foreach (var error in errors)
            {
                var match = _expected.FirstOrDefault(e => error.Contains(e.Substring, StringComparison.Ordinal));
                if (match is null) unexpected.Add(error);
                else match.Seen = true;
            }

            var missing = bodyFailure is null
                ? _expected.Where(e => e.MustOccur && !e.Seen).Select(e => e.Substring).ToList()
                : [];
            if (unexpected.Count == 0 && missing.Count == 0) return null;

            var text = new StringBuilder();
            if (unexpected.Count > 0)
            {
                text.Append("The app logged ").Append(unexpected.Count).AppendLine(" unexpected AppLog Error(s) during this UI test body "
                    + "(fire-and-forget failures are otherwise invisible). Fix the cause, or announce a legitimate one with "
                    + "StaTestHost.ExpectLoggedError(substring, reason):");
                foreach (var error in unexpected) text.AppendLine("  - " + Truncate(error));
            }
            if (missing.Count > 0)
                text.Append("Expected AppLog Error(s) that never occurred: ").AppendLine(string.Join("; ", missing.Select(m => "\"" + m + "\"")));
            if (bodyFailure is not null) text.Append("The test body itself also failed: ").Append(bodyFailure.GetType().Name).Append(": ").Append(bodyFailure.Message);
            return new InvalidOperationException(text.ToString().TrimEnd(), bodyFailure);
        }
    }

    private static string Truncate(string text) => text.Length <= 1200 ? text : text[..1200] + " ...";

    private long LengthOrZero()
    {
        try { return File.Exists(_path) ? new FileInfo(_path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private List<string> ReadErrorsSinceMark()
    {
        var result = new List<string>();
        string text;
        try
        {
            if (!File.Exists(_path)) return result;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The 10 MB rotation moved the file: everything in the new one is newer than the mark.
            if (stream.Length < _mark) _mark = 0;
            stream.Seek(_mark, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
            _mark = stream.Length;
        }
        catch (IOException)
        {
            return result;
        }

        // An entry is its header line plus the exception text that follows it.
        StringBuilder? current = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var header = EntryHeader().Match(trimmed);
            if (header.Success)
            {
                if (current is not null) result.Add(current.ToString());
                current = header.Groups["level"].Value == "ERROR" ? new StringBuilder(trimmed) : null;
            }
            else current?.Append('\n').Append(trimmed);
        }
        if (current is not null) result.Add(current.ToString());
        return result;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(?<level>[A-Z]+)\] \[T\d+\] ")]
    private static partial Regex EntryHeader();
}
