using System.Globalization;

namespace PhotoReview.Benchmark.Cli.ShellHost;

/// <summary>
/// <c>--startup-probe --shell &lt;PhotoReview.exe&gt; [--wpf &lt;PhotoReview.App.exe&gt;] --source &lt;thư mục ảnh&gt; [--count 10] [--rounds 8]
/// [--warmup 1] [--seed 1] [--keys 12] [--out dir]</c>: probe khởi động + key→frame xen kẽ shell/WPF trên bản publish (WP-29).
/// Thứ tự: app đầu (baseline) là WPF nếu có, shell là candidate.
/// </summary>
internal static class StartupProbeCommand
{
    internal sealed record Parsed(string? ShellExe, string? WpfExe, string Source, int Count, int Rounds, int Warmup, int Seed, int Keys, string Out);

    public static Parsed Parse(IReadOnlyList<string> args)
    {
        string? shell = null, wpf = null, source = null, outDir = null;
        int count = 10, rounds = 8, warmup = 1, seed = 1, keys = 12;
        for (var i = 1; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            int Int(string name, int min, int max)
            {
                var text = Value();
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
                {
                    throw new ArgumentException($"{name} must be an integer in [{min}, {max}]: {text}");
                }

                return n;
            }

            switch (args[i])
            {
                case "--shell": shell = Value(); break;
                case "--wpf": wpf = Value(); break;
                case "--source": source = Value(); break;
                case "--out": outDir = Value(); break;
                case "--count": count = Int("--count", 3, 200); break;
                case "--rounds": rounds = Int("--rounds", 1, 200); break;
                case "--warmup": warmup = Int("--warmup", 0, 10); break;
                case "--seed": seed = Int("--seed", 0, int.MaxValue); break;
                case "--keys": keys = Int("--keys", 0, 200); break;
                default: throw new ArgumentException("unknown argument: " + args[i]);
            }
        }

        if (shell is null && wpf is null)
        {
            throw new ArgumentException("at least one of --shell / --wpf is required");
        }

        return new Parsed(shell, wpf, source ?? throw new ArgumentException("--source is required"), count, rounds, warmup, seed, keys,
            outDir ?? Path.Combine(Path.GetTempPath(), "pr-startup-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
    }

    public static int Run(IReadOnlyList<string> args)
    {
        var p = Parse(args);
        foreach (var exe in new[] { p.ShellExe, p.WpfExe }.OfType<string>())
        {
            if (!File.Exists(exe))
            {
                throw new FileNotFoundException("Executable not found: " + exe);
            }
        }

        var photos = Path.Combine(p.Out, "photos");
        CopySubset(p.Source, photos, p.Count);
        var apps = new List<ProbeApp>();
        if (p.WpfExe is not null) apps.Add(new ProbeApp("wpf", Path.GetFullPath(p.WpfExe)));
        if (p.ShellExe is not null) apps.Add(new ProbeApp("shell", Path.GetFullPath(p.ShellExe)));

        var options = new ProbeOptions(apps, p.Rounds, p.Warmup, p.Seed, p.Out);
        var session = new LoggingSession(new ProcessProbeSession(photos, Path.Combine(p.Out, "runs"), p.Keys));
        var outcome = StartupProbeRunner.Run(options, session);
        ProbeReport.Write(p.Out, outcome);
        Console.WriteLine(ProbeReport.ToMarkdown(outcome));
        Console.WriteLine("REPORT: " + Path.Combine(p.Out, "summary.md"));
        return outcome.Runs.Any(r => !r.IsWarmup && r.Ok) ? 0 : 1;
    }

    private static void CopySubset(string source, string target, int count)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        Directory.CreateDirectory(target);
        var jpegs = Directory.GetFiles(source, "*.jpg").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (jpegs.Length < count)
        {
            throw new InvalidOperationException($"{source} has {jpegs.Length} .jpg files, need {count}");
        }

        var start = (jpegs.Length - count) / 2;
        for (var i = 0; i < count; i++)
        {
            var destination = Path.Combine(target, $"{i:D2}-" + Path.GetFileName(jpegs[start + i]));
            if (!File.Exists(destination))
            {
                File.Copy(jpegs[start + i], destination);
            }
        }
    }

    private sealed class LoggingSession(IProbeSession inner) : IProbeSession
    {
        public ProbeRunResult Run(ProbeApp app, ProbeSlot slot)
        {
            var result = inner.Run(app, slot);
            Console.WriteLine(ProcessProbeSession.Describe(result));
            return result;
        }
    }
}
