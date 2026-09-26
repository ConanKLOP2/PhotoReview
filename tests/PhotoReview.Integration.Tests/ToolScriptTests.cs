using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoReview.Integration.Tests;

/// <summary>Shared plumbing for the tests that run tools/*.ps1 in Windows PowerShell (temp directories only).</summary>
internal static class PowerShellRunner
{
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PhotoReview.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("PhotoReview.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The test run may itself be started from PowerShell 7 (CI does). Windows PowerShell 5.1 children then inherit the
    /// PS7 <c>PSModulePath</c>, cannot load the PS7 build of Microsoft.PowerShell.Utility and lose cmdlets such as
    /// Get-FileHash. Dropping the variable makes the child compute its own default module path.
    /// </summary>
    internal static void ForWindowsPowerShell(ProcessStartInfo psi) => psi.Environment.Remove("PSModulePath");

    private static readonly string[] BaseArgs = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"];

    /// <summary>Runs <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass</c> with the arguments; output is decoded as UTF-8.</summary>
    public static (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BaseArgs.Concat(args)) psi.ArgumentList.Add(a);
        ForWindowsPowerShell(psi);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("powershell.exe did not finish within 3 minutes: " + string.Join(' ', args));
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Copies one tools script into <c>&lt;repo&gt;\tools</c> of a fake repository so its $PSScriptRoot-relative paths stay inside the temp directory.</summary>
    public static string CopyScript(string fakeRepo, params string[] relativeScripts)
    {
        var tools = Directory.CreateDirectory(Path.Combine(fakeRepo, "tools")).FullName;
        foreach (var script in relativeScripts) File.Copy(Path.Combine(RepoRoot(), "tools", script), Path.Combine(tools, script), overwrite: true);
        return tools;
    }
}

/// <summary>
/// tools/i18n-check.ps1 edge cases (strict UTF-8, surrogates, nesting, placeholders, size, BOM). One script run over a
/// folder of crafted catalogs (the script spends seconds compiling its C# validator), asserted per file from the -Json output.
/// </summary>
[Trait("Category", "Integration")]
public sealed class I18nCheckScriptTests : IClassFixture<I18nCheckScriptTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        private readonly TempRoot _root = new("i18n");
        public IReadOnlyDictionary<string, string[]> ErrorsByFile { get; }
        public int ExitCode { get; }
        public string Output { get; }
        public (int ExitCode, string Output) BracketFolderRun { get; }
        public (int ExitCode, string Output) MissingFolderRun { get; }

        public Fixture()
        {
            var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(PowerShellRunner.RepoRoot(), "src", "PhotoReview.Core", "Localization", "Languages", "en.json"), Encoding.UTF8));
            var plain = en.RootElement.EnumerateObject().First(p => !p.Name.StartsWith('_') && p.Value.GetString()!.IndexOf('{') < 0).Name;
            var withPlaceholder = en.RootElement.EnumerateObject().First(p => !p.Name.StartsWith('_') && p.Value.GetString()!.Contains("{name}", StringComparison.Ordinal)).Name;

            var dir = _root.Dir("catalogs");
            void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(dir, name + ".json"), bytes);
            void WriteText(string name, string text) => Write(name, new UTF8Encoding(false).GetBytes(text));
            string Meta(string code) => "\"_meta\":{\"code\":\"" + code + "\"}";
            string Doc(string code, string body) => "{" + Meta(code) + (body.Length > 0 ? "," + body : "") + "}";

            WriteText("good", Doc("good", $"\"{plain}\":\"Xin chao \\u00e9\"")); // \u00e9 escape, fine
            WriteText("astral", Doc("astral", $"\"{plain}\":\"smile \\ud83d\\ude00\"")); // valid surrogate pair
            Write("bom", [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(Doc("bom", $"\"{plain}\":\"ok\""))]);
            Write("doublebom", [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(Doc("doublebom", $"\"{plain}\":\"ok\""))]);
            Write("badutf8", [.. new UTF8Encoding(false).GetBytes("{" + Meta("badutf8") + $",\"{plain}\":\"a"), 0xC3, 0x28, .. new UTF8Encoding(false).GetBytes("b\"}")]);
            WriteText("lonesurrogate", Doc("lonesurrogate", $"\"{plain}\":\"a\\ud800b\""));
            WriteText("dupkey", "{" + Meta("dupkey") + $",\"{plain}\":\"a\",\"{plain}\":\"b\"}}");
            WriteText("dupescaped", "{" + Meta("dupescaped") + $",\"{plain}\":\"a\",\"\\u0061ction.dummy\":\"b\",\"action.dummy\":\"c\"}}");
            WriteText("deep", Doc("deep", "\"x\":" + new string('[', 200_000) + new string(']', 200_000)));
            WriteText("unknownkey", Doc("unknownkey", "\"no.such.key\":\"a\""));
            WriteText("unknownph", Doc("unknownph", $"\"{plain}\":\"{{zzz}}\""));
            WriteText("newlineph", Doc("newlineph", $"\"{withPlaceholder}\":\"{{name\\n}}\""));
            WriteText("nonstring", Doc("nonstring", $"\"{plain}\":5"));
            WriteText("nullvalue", Doc("nullvalue", $"\"{plain}\":null"));
            WriteText("nometa", "{\"" + plain + "\":\"a\"}");
            WriteText("badcode", Doc("Bad Code!", $"\"{plain}\":\"a\""));
            WriteText("rootarray", "[]");
            WriteText("empty", "");
            WriteText("trailingcomma", "{" + Meta("trailingcomma") + $",\"{plain}\":\"a\",}}");
            WriteText("comment", "{" + Meta("comment") + $",\"{plain}\":\"a\"}} // note");
            Write("huge", new UTF8Encoding(false).GetBytes(Doc("huge", $"\"{plain}\":\"a\"").TrimEnd('}') + new string(' ', 1_100_000) + "}"));

            var run = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "i18n-check.ps1"), "-Path", dir, "-Json");
            ExitCode = run.ExitCode;
            Output = run.Output;
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var start = run.Output.IndexOf('{', StringComparison.Ordinal);
            if (start >= 0)
            {
                using var result = JsonDocument.Parse(run.Output[start..]);
                foreach (var catalog in result.RootElement.GetProperty("catalogs").EnumerateArray())
                {
                    var file = catalog.GetProperty("File").GetString()!;
                    var list = catalog.GetProperty("Errors");
                    errors[file] = list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Select(e => e.GetString()!)] : [list.GetString()!];
                }
            }
            ErrorsByFile = errors;

            var bracket = _root.Dir("[brackets] and spaces");
            File.WriteAllText(Path.Combine(bracket, "fine.json"), Doc("fine", $"\"{plain}\":\"ok\""), new UTF8Encoding(false));
            BracketFolderRun = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "i18n-check.ps1"), "-Path", bracket);
            MissingFolderRun = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "i18n-check.ps1"), "-Path", _root.Combine("does-not-exist"));
        }

        public void Dispose() => _root.Dispose();
    }

    private readonly Fixture _fixture;

    public I18nCheckScriptTests(Fixture fixture) => _fixture = fixture;

    [Theory(DisplayName = "i18n-check accepts well-formed catalogs (escapes, surrogate pair, one BOM)")]
    [InlineData("good.json")]
    [InlineData("astral.json")]
    [InlineData("bom.json")]
    public void ValidCatalogsHaveNoErrors(string file)
    {
        Assert.True(_fixture.ErrorsByFile.ContainsKey(file), _fixture.Output);
        Assert.Empty(_fixture.ErrorsByFile[file]);
    }

    [Theory(DisplayName = "i18n-check rejects malformed catalogs with the expected reason")]
    [InlineData("badutf8.json", "Invalid UTF-8")]
    [InlineData("lonesurrogate.json", "Unpaired UTF-16 surrogate")]
    [InlineData("doublebom.json", "Invalid JSON")]
    [InlineData("dupkey.json", "Duplicate key")]
    [InlineData("dupescaped.json", "Duplicate key")]
    [InlineData("deep.json", "Nesting is deeper than 64")]
    [InlineData("unknownkey.json", "unknown key")]
    [InlineData("unknownph.json", "unknown placeholder {zzz}")]
    [InlineData("newlineph.json", "unbalanced braces or an invalid placeholder")]
    [InlineData("nonstring.json", "is not a string")]
    [InlineData("nullvalue.json", "is not a string")]
    [InlineData("nometa.json", "_meta.code is missing or invalid")]
    [InlineData("badcode.json", "_meta.code is missing or invalid")]
    [InlineData("rootarray.json", "root must be a JSON object")]
    [InlineData("empty.json", "Invalid JSON")]
    [InlineData("trailingcomma.json", "Trailing comma")]
    [InlineData("comment.json", "Invalid JSON")]
    [InlineData("huge.json", "larger than")]
    public void MalformedCatalogsAreRejected(string file, string reason)
    {
        Assert.True(_fixture.ErrorsByFile.TryGetValue(file, out var errors), $"{file} missing from the -Json result. Output: {_fixture.Output}");
        Assert.Contains(errors, e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "i18n-check exits 1 when any catalog is bad, and still reports every file")]
    public void BadCatalogsFailTheRun() => Assert.Equal(1, _fixture.ExitCode);

    [Fact(DisplayName = "i18n-check -Path accepts a folder whose name contains wildcard characters and spaces")]
    public void FolderWithBracketsIsChecked()
    {
        Assert.Equal(0, _fixture.BracketFolderRun.ExitCode);
        Assert.Contains("PASS", _fixture.BracketFolderRun.Output, StringComparison.Ordinal);
        Assert.Contains("fine.json", _fixture.BracketFolderRun.Output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "i18n-check -Path on a missing folder fails")]
    public void MissingFolderFails()
    {
        Assert.NotEqual(0, _fixture.MissingFolderRun.ExitCode);
        Assert.Contains("Folder not found", _fixture.MissingFolderRun.Output, StringComparison.Ordinal);
    }
}

/// <summary>tools/fetch-native.ps1 offline paths (present file, malformed pin) inside a temp fake repository whose folder name has wildcard characters.</summary>
[Trait("Category", "Integration")]
public sealed class FetchNativeScriptTests : IDisposable
{
    private readonly TempRoot _root = new("fetch-native");

    public void Dispose() => _root.Dispose();

    private string FakeRepo(string hashFileContent, byte[]? dll)
    {
        var repo = _root.Dir("repo [1]");
        PowerShellRunner.CopyScript(repo, "fetch-native.ps1");
        Directory.CreateDirectory(Path.Combine(repo, "native"));
        File.WriteAllText(Path.Combine(repo, "native", "turbojpeg.sha256"), hashFileContent);
        if (dll is not null)
        {
            Directory.CreateDirectory(Path.Combine(repo, "native", "x64"));
            File.WriteAllBytes(Path.Combine(repo, "native", "x64", "turbojpeg.dll"), dll);
        }
        return repo;
    }

    private static (int ExitCode, string Output) Fetch(string repo) =>
        PowerShellRunner.Run("-File", Path.Combine(repo, "tools", "fetch-native.ps1"));

    private static readonly byte[] Dll = [1, 2, 3, 4, 5];
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Theory(DisplayName = "fetch-native: a present DLL whose SHA-256 matches the pin is accepted without any download (upper or lower case pin, wildcard characters in the repo path)")]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingDllIsAccepted(bool lowerCasePin)
    {
        var pin = lowerCasePin ? Hex(Dll).ToLowerInvariant() : Hex(Dll);
        var repo = FakeRepo(pin + "\r\n", Dll);

        var (code, output) = Fetch(repo);

        Assert.True(code == 0, "fetch-native exit " + code + ": " + output);
        Assert.Contains("is present and SHA-256 matches", output, StringComparison.Ordinal);
        Assert.Equal(Dll, File.ReadAllBytes(Path.Combine(repo, "native", "x64", "turbojpeg.dll")));
    }

    [Theory(DisplayName = "fetch-native: an empty, short, over-long or non-hex pin fails before touching the network or the DLL")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("E9BDEC69FA2008EAF557CB68BD483E62A350B740A15884970000000000000000FF")]
    [InlineData("ZZBDEC69FA2008EAF557CB68BD483E62A350B740A158849700000000000000000")]
    public void MalformedPinIsRejected(string pin)
    {
        var repo = FakeRepo(pin, Dll);

        var (code, output) = Fetch(repo);

        Assert.NotEqual(0, code);
        Assert.Contains("64-character hex SHA-256", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading", output, StringComparison.Ordinal);
        Assert.Equal(Dll, File.ReadAllBytes(Path.Combine(repo, "native", "x64", "turbojpeg.dll")));
    }

    [Fact(DisplayName = "fetch-native: the pin shipped in the repository is a 64-character hex SHA-256")]
    public void ShippedPinIsWellFormed()
    {
        var pin = File.ReadLines(Path.Combine(PowerShellRunner.RepoRoot(), "native", "turbojpeg.sha256")).First().Trim();
        Assert.Matches("^[0-9A-Fa-f]{64}$", pin);
    }
}

/// <summary>tools/clean-work.ps1: dry run, retention, protected folder, links and wildcard characters, inside a temp fake repository.</summary>
[Trait("Category", "Integration")]
public sealed class CleanWorkScriptTests : IDisposable
{
    private readonly TempRoot _root = new("clean-work");

    public void Dispose() => _root.Dispose();

    private string FakeRepo(string name = "repo [x]")
    {
        var repo = _root.Dir(name);
        PowerShellRunner.CopyScript(repo, "clean-work.ps1");
        return repo;
    }

    private static string Old(string path, int daysOld = 100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-daysOld));
        return path;
    }

    /// <summary>A directory junction (needs no elevation, unlike a symbolic link).</summary>
    private static void MakeJunction(string link, string target)
    {
        var (code, output) = PowerShellRunner.Run("-Command", $"New-Item -ItemType Junction -Path '{link}' -Target '{target}' | Out-Null");
        Assert.True(code == 0 && Directory.Exists(link), "creating the junction failed: " + output);
    }

    private static (int ExitCode, string Output) Clean(string repo, params string[] args) =>
        PowerShellRunner.Run(["-File", Path.Combine(repo, "tools", "clean-work.ps1"), .. args]);

    [Fact(DisplayName = "clean-work: the default run is a dry run that deletes nothing")]
    public void DryRunDeletesNothing()
    {
        var repo = FakeRepo();
        var oldFile = Old(Path.Combine(repo, "work", "logs", "old.log"));

        var (code, output) = Clean(repo);

        Assert.Equal(0, code);
        Assert.True(File.Exists(oldFile));
        Assert.Contains("Would delete", output, StringComparison.Ordinal);
        Assert.Contains("Dry run: 1 file(s)", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "clean-work -Delete removes only old files, prunes emptied folders, and never touches dotnet-home or files outside work (junction not followed)")]
    public void DeleteRespectsRetentionProtectedFolderAndLinks()
    {
        var repo = FakeRepo("repo plain"); // New-Item -Path (used for the junction) would treat [x] as a wildcard
        var work = Path.Combine(repo, "work");
        var oldFile = Old(Path.Combine(work, "logs", "sub", "old.log"));
        var newFile = Old(Path.Combine(work, "logs", "new.log"), daysOld: 1);
        var kept = Old(Path.Combine(work, "dotnet-home", "cache", "old.bin"));
        var outside = Old(Path.Combine(repo, "outside", "precious.txt"));
        var junction = Path.Combine(work, "link");
        MakeJunction(junction, Path.Combine(repo, "outside"));

        var (code, output) = Clean(repo, "-Delete", "-OlderThanDays", "14");

        Assert.Equal(0, code);
        Assert.False(File.Exists(oldFile), output);
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldFile)!), "emptied folder should be pruned");
        Assert.True(File.Exists(newFile));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(outside));
    }

    [Fact(DisplayName = "clean-work rejects a negative retention and a missing work folder is a no-op")]
    public void NegativeRetentionRejected_MissingWorkIsNoOp()
    {
        var repo = FakeRepo();
        var (noWorkCode, noWorkOutput) = Clean(repo, "-Delete");
        Assert.Equal(0, noWorkCode);
        Assert.Contains("Nothing to do", noWorkOutput, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(repo, "work"));
        var (code, output) = Clean(repo, "-OlderThanDays", "-1");
        Assert.NotEqual(0, code);
        Assert.Contains("OlderThanDays must be >= 0", output, StringComparison.Ordinal);
    }
}

/// <summary>tools/*.ps1 encoding and misc script guards.</summary>
[Trait("Category", "Integration")]
public sealed class ToolScriptEncodingTests : IDisposable
{
    private readonly TempRoot _root = new("tool-scripts");

    public void Dispose() => _root.Dispose();

    public static TheoryData<string> AllScripts()
    {
        var data = new TheoryData<string>();
        var tools = Path.Combine(PowerShellRunner.RepoRoot(), "tools");
        foreach (var file in Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(tools, file));
        return data;
    }

    [Theory(DisplayName = "Windows PowerShell 5.1 reads a BOM-less script as ANSI: every tools script with a non-ASCII byte must start with a UTF-8 BOM")]
    [MemberData(nameof(AllScripts))]
    public void NonAsciiScriptsCarryABom(string relative)
    {
        var bytes = File.ReadAllBytes(Path.Combine(PowerShellRunner.RepoRoot(), "tools", relative));
        var hasNonAscii = bytes.Any(b => b >= 0x80);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.True(!hasNonAscii || hasBom, $"tools/{relative} contains non-ASCII text but no UTF-8 BOM; Windows PowerShell 5.1 would decode it as ANSI (mojibake).");
    }

    [Fact(DisplayName = "benchmark-folder prints its Vietnamese error text intact under Windows PowerShell 5.1")]
    public void BenchmarkFolder_VietnameseMessageIsNotMojibake()
    {
        var empty = _root.Dir("empty");
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "benchmark-folder.ps1");

        var (code, output) = PowerShellRunner.Run("-Command",
            $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; try {{ & '{script}' -Folder '{empty}' }} catch {{ $_.Exception.Message }}");

        Assert.Equal(0, code);
        Assert.Contains("Không tìm thấy ảnh được hỗ trợ", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "parse-applog reads a log and writes its CSV under a folder whose name contains wildcard characters")]
    public void ParseAppLog_BracketFolder()
    {
        var folder = _root.Dir("logs [2026] (a)");
        var log = Path.Combine(folder, "app.log");
        File.WriteAllLines(log,
        [
            "2026-09-26 10:00:00.100 [INF] [T1] ShowImage start token=1 path=a.jpg",
            "2026-09-26 10:00:00.150 [INF] [T1] ShowImage preview-presented token=1 path=a.jpg",
        ], new UTF8Encoding(false));
        var csv = Path.Combine(folder, "out [1].csv");

        var (code, output) = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", "parse-applog.ps1"), "-Log", log, "-Out", csv);

        Assert.True(code == 0, output);
        Assert.True(File.Exists(csv), output);
    }

    [Fact(DisplayName = "procmon-summary reads and writes CSV under a folder whose name contains wildcard characters")]
    public void ProcmonSummary_BracketFolder()
    {
        var folder = _root.Dir("procmon [2026]");
        var csv = Path.Combine(folder, "procmon [1].csv");
        File.WriteAllLines(csv,
        [
            "\"Time of Day\",\"Process Name\",\"PID\",\"Operation\",\"Path\",\"Result\",\"Detail\"",
            "\"1\",\"PhotoReview.App.exe\",\"1\",\"ReadFile\",\"C:\\Users\\u\\AppData\\Local\\PhotoReview\\cache\\abc.pv4\",\"SUCCESS\",\"Length: 4,096\"",
        ]);
        var outCsv = Path.Combine(folder, "out [1].csv");

        var (code, output) = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", "procmon-summary.ps1"), "-Csv", csv, "-Out", outCsv);

        Assert.True(code == 0, output);
        Assert.True(File.Exists(outCsv), output);
    }

    [Fact(DisplayName = "benchmark-folder handles a folder whose name contains wildcard characters")]
    public void BenchmarkFolder_BracketFolder()
    {
        var folder = _root.Dir("photos [2026] (a)");
        File.WriteAllBytes(Path.Combine(folder, "a.jpg"), [1, 2, 3]);
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "benchmark-folder.ps1");

        var (code, output) = PowerShellRunner.Run("-File", script, "-Folder", folder, "-Runs", "1");

        Assert.Equal(0, code);
        Assert.Contains("files=1;", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "check-doc-links skips '…' example paths even when the markdown is UTF-8 without a BOM")]
    public void CheckDocLinks_EllipsisExampleIsSkipped()
    {
        var repo = _root.Dir("docs-repo");
        PowerShellRunner.CopyScript(repo, "check-doc-links.ps1");
        File.WriteAllText(Path.Combine(repo, "README.md"), "See [more](docs/…/something.md) and [gone](docs/missing.md).\n", new UTF8Encoding(false));

        var (code, output) = PowerShellRunner.Run("-File", Path.Combine(repo, "tools", "check-doc-links.ps1"));

        Assert.NotEqual(0, code);
        Assert.Contains("docs/missing.md", output, StringComparison.Ordinal);
        Assert.DoesNotContain("something.md", output, StringComparison.Ordinal);
    }

    private static readonly string[] ReleaseFiles =
    [
        "PhotoReview.App.exe", "PhotoReview.App.dll", "PhotoReview.App.deps.json", "PhotoReview.App.runtimeconfig.json",
        "PhotoReview.Core.dll", "PhotoReview.Imaging.dll", "PhotoReview.Platform.Windows.dll", "PhotoReview.Benchmarking.dll",
        "PhotoReview.PerfAnalysis.dll", "PhotoReview.Imaging.TurboJpeg.dll", "turbojpeg.dll", "Languages\\en.json", "Languages\\vi.json",
    ];

    [Fact(DisplayName = "verify-release: an empty folder fails and names a missing file")]
    public void VerifyRelease_MissingFiles()
    {
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "verify-release.ps1");

        var (code, output) = PowerShellRunner.Run("-File", script, "-ReleaseDirectory", _root.Dir("empty-release"));

        Assert.NotEqual(0, code);
        Assert.Contains("Missing release file", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release: a complete folder whose turbojpeg.dll does not match the pinned SHA-256 fails before anything else")]
    public void VerifyRelease_NativeHashMismatch()
    {
        var release = _root.Dir("release [x]");
        foreach (var name in ReleaseFiles) _root.File(Path.Combine("release [x]", name), 1, 2, 3);
        var script = Path.Combine(PowerShellRunner.RepoRoot(), "tools", "verify-release.ps1");

        var (code, output) = PowerShellRunner.Run("-File", script, "-ReleaseDirectory", release);

        Assert.NotEqual(0, code);
        Assert.True(output.Contains("turbojpeg.dll SHA-256 mismatch", StringComparison.Ordinal), "verify-release output: " + output);
    }
}
