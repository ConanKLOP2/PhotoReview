using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PhotoReview.Tests.PerfAnalysis;

/// <summary>
/// One parsed data row from a perf-*.csv file written by
/// PhotoReview.App.Diagnostics.PerfCsvListener (D03). Column order is fixed:
/// utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text. See PerfCsvListener.BuildRow for how
/// each EventSource event's payload is mapped into nav/pathId/a..d/text (nav or gen -> Nav,
/// pathId -> PathId, numeric payload args in declaration order -> a..d, bool as 0/1, remaining
/// string args joined with ';' -> Text).
/// </summary>
public sealed record PerfRow(
    long UtcTicks, long QpcTicks, int Thread, string Event,
    string Nav, string PathId, string A, string B, string C, string D, string Text)
{
    public double? ANum => ParseDouble(A);
    public double? BNum => ParseDouble(B);
    public double? CNum => ParseDouble(C);
    public double? DNum => ParseDouble(D);

    public long? NavId => long.TryParse(Nav, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string s) =>
        s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>One parsed perf-*.csv file: header metadata plus every data row, in file order.</summary>
public sealed class PerfCsvFile
{
    public required string Path { get; init; }
    public required string CommitVersion { get; init; }
    public required IReadOnlyDictionary<string, string> DiagFlags { get; init; }
    public required long QpcFrequency { get; init; }
    public required long DroppedRows { get; init; }
    public required IReadOnlyList<PerfRow> Rows { get; init; }

    /// <summary>Converts a delta of two <see cref="Stopwatch.GetTimestamp"/> readings to milliseconds
    /// using this file's own recorded QPC frequency (falls back to the local machine's frequency
    /// only when the header did not carry one).</summary>
    public double QpcToMs(long deltaTicks) => deltaTicks * 1000.0 / QpcFrequency;
}

public static class PerfCsvReader
{
    private const string HeaderPrefix = "utcTicks,";

    public static PerfCsvFile Read(string path)
    {
        var commit = "unknown";
        var diagFlags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long qpcFrequency = 0;
        long dropped = 0;
        var rows = new List<PerfRow>();

        foreach (var rawLine in File.ReadLines(path))
        {
            if (rawLine.Length == 0) continue;
            if (rawLine[0] == '#')
            {
                ParseCommentLine(rawLine, ref commit, diagFlags, ref qpcFrequency, ref dropped);
                continue;
            }
            if (rawLine.StartsWith(HeaderPrefix, StringComparison.Ordinal)) continue;

            var fields = SplitCsvLine(rawLine);
            if (fields.Count < 11) continue; // malformed/truncated line (e.g. torn write): skip defensively
            if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utcTicks)) continue;
            if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var qpcTicks)) continue;
            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var thread)) continue;

            rows.Add(new PerfRow(utcTicks, qpcTicks, thread, fields[3], fields[4], fields[5],
                fields[6], fields[7], fields[8], fields[9], fields[10]));
        }

        return new PerfCsvFile
        {
            Path = path,
            CommitVersion = commit,
            DiagFlags = diagFlags,
            QpcFrequency = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency,
            DroppedRows = dropped,
            Rows = rows,
        };
    }

    /// <summary>
    /// Parses the two comment-line shapes PerfCsvListener writes:
    ///   # commit=&lt;v&gt; diag=&lt;a=1;b=2&gt; qpcFrequency=&lt;n&gt;   (header, WriteHeader)
    ///   # dropped=&lt;n&gt;                                             (trailer, Dispose)
    /// Unknown "# ..." lines (e.g. hand-authored comments in a sample fixture) are ignored.
    /// </summary>
    private static void ParseCommentLine(string line, ref string commit, Dictionary<string, string> diagFlags,
        ref long qpcFrequency, ref long dropped)
    {
        var content = line.TrimStart('#', ' ');
        foreach (var token in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            switch (key)
            {
                case "commit":
                    commit = value;
                    break;
                case "qpcFrequency":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)) qpcFrequency = f;
                    break;
                case "dropped":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) dropped = d;
                    break;
                case "diag":
                    foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var i = pair.IndexOf('=');
                        if (i > 0) diagFlags[pair[..i]] = pair[(i + 1)..];
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Splits one CSV data line matching PerfCsvListener.CsvEscape's scheme: a field is wrapped in
    /// double quotes only when it contains a comma, quote, or newline, and embedded quotes are
    /// doubled. Quotes therefore only ever appear at field boundaries, so a simple state machine
    /// (not a general RFC 4180 parser) is sufficient.
    /// </summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
