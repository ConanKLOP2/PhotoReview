using System.Globalization;
using System.Text;

namespace PhotoReview.Benchmark.Cli.ShellHost;

/// <summary>Một dòng của CSV <c>PHOTOREVIEW_PERF_TRACE</c> (schema của <c>PerfCsvListener</c>).</summary>
internal sealed record TraceRow(long UtcTicks, string Event, string Nav, string A, string Text);

/// <summary>Các mốc rút ra từ một lần chạy, mọi giá trị ms từ lúc tạo tiến trình (mốc Startup) trừ khi ghi khác.</summary>
internal sealed record TraceMetrics(
    double FirstFrameMs,
    double WindowShownMs,
    double WindowRevealedMs,
    IReadOnlyList<double> KeyToFrameMs,
    IReadOnlyList<double> KeyToFrameCachedMs,
    int KeysWithoutFrame,
    IReadOnlyDictionary<string, double> Marks);

internal static class ProbeTrace
{
    /// <summary>Phím điều hướng được tính key-&gt;frame (mỗi lần bấm một ảnh mới).</summary>
    internal static readonly string[] NavigationKeys = ["Right", "Left", "Next", "Prior", "PageDown", "PageUp", "Space", "Back"];

    public static IReadOnlyList<TraceRow> Parse(IEnumerable<string> lines)
    {
        var rows = new List<TraceRow>();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("utcTicks,", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = SplitCsv(line);
            if (fields.Count < 11 || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utc))
            {
                continue;
            }

            rows.Add(new TraceRow(utc, fields[3], fields[4], fields[6], fields[10]));
        }

        return rows;
    }

    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Rút các mốc; null khi thiếu mốc <c>appStartup</c> hoặc chưa có khung đầu.</summary>
    public static TraceMetrics? Extract(IReadOnlyList<TraceRow> rows)
    {
        var ordered = rows.Select((r, i) => (r, i)).OrderBy(x => x.r.UtcTicks).ThenBy(x => x.i).Select(x => x.r).ToArray();
        var marks = new Dictionary<string, double>(StringComparer.Ordinal);
        TraceRow? anchor = null;
        foreach (var row in ordered)
        {
            if (row.Event == "Startup" && TryDouble(row.A, out var ms) && row.Text.Length > 0 && !marks.ContainsKey(row.Text))
            {
                marks[row.Text] = ms;
                if (row.Text == "appStartup")
                {
                    anchor = row;
                }
            }
        }

        if (anchor is null)
        {
            return null;
        }

        var anchorMs = marks["appStartup"];
        double ProcessMs(TraceRow row) => anchorMs + ((row.UtcTicks - anchor.UtcTicks) / (double)TimeSpan.TicksPerMillisecond);

        TraceRow? firstFrame = ordered.FirstOrDefault(r => r.Event == "RenderedFrame");
        if (firstFrame is null)
        {
            return null;
        }

        var keyToFrame = new List<double>();
        var keyToFrameCached = new List<double>();
        var ramHitNavs = ordered.Where(r => r.Event == "Lookup" && r.Text == "ramHit").Select(r => r.Nav).ToHashSet(StringComparer.Ordinal);
        var withoutFrame = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var key = ordered[i];
            if (key.Event != "KeyInput" || !NavigationKeys.Contains(key.Text, StringComparer.Ordinal))
            {
                continue;
            }

            TraceRow? frame = null;
            for (var j = i + 1; j < ordered.Length && ordered[j].Event != "KeyInput"; j++)
            {
                if (ordered[j].Event == "RenderedFrame")
                {
                    frame = ordered[j];
                    break;
                }
            }

            if (frame is null)
            {
                withoutFrame++;
                continue;
            }

            _ = TryDouble(key.A, out var inputDelay);
            var latency = Math.Max(0, inputDelay) + ((frame.UtcTicks - key.UtcTicks) / (double)TimeSpan.TicksPerMillisecond);
            keyToFrame.Add(latency);
            if (frame.Nav.Length > 0 && ramHitNavs.Contains(frame.Nav))
            {
                keyToFrameCached.Add(latency);
            }
        }

        return new TraceMetrics(
            ProcessMs(firstFrame),
            marks.GetValueOrDefault("windowShown", double.NaN),
            marks.GetValueOrDefault("windowRevealed", double.NaN),
            keyToFrame, keyToFrameCached, withoutFrame, marks);
    }

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
