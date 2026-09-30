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
        "PhotoReview.PerfAnalysis.dll", "PhotoReview.Imaging.TurboJpeg.dll", "PhotoReview.Imaging.LibRaw.dll", "turbojpeg.dll",
        "libraw.dll", "LibRaw-LICENSE.LGPL", "LibRaw-LICENSE.CDDL", "LibRaw-SOURCE.zip", "LibRaw-NOTICE.txt", "THIRD-PARTY-NOTICES.md",
        "Languages\\en.json", "Languages\\vi.json",
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

/// <summary>
/// tools/fetch-libraw.ps1 in a fake repository (temp directory, no network): the package SHA-256 is verified BEFORE extraction,
/// entries cannot escape the temp directory, -Verify is read-only, and a good package installs and is then idempotent.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchLibRawScriptTests : IDisposable
{
    private readonly TempRoot _root = new("fetch-libraw");
    public void Dispose() => _root.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] BuildPackage(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        return ms.ToArray();
    }

    /// <summary>Fake repo with the script and pins; returns (repo, scriptPath).</summary>
    private (string Repo, string Script) FakeRepo(string name, byte[] dll, byte[] package)
    {
        var repo = _root.Dir(name);
        PowerShellRunner.CopyScript(repo, "fetch-libraw.ps1");
        Directory.CreateDirectory(Path.Combine(repo, "native"));
        File.WriteAllText(Path.Combine(repo, "native", "libraw.sha256"), Sha(dll) + "\n");
        File.WriteAllText(Path.Combine(repo, "native", "libraw.package.sha256"), Sha(package) + "\n");
        return (repo, Path.Combine(repo, "tools", "fetch-libraw.ps1"));
    }

    private static readonly byte[] Dll = [1, 2, 3, 4];

    [Fact(DisplayName = "fetch-libraw: a package whose SHA-256 differs from the pin is rejected before anything is extracted or installed")]
    public void FetchLibRaw_TamperedPackage_FailsBeforeExtraction()
    {
        var good = BuildPackage(("LibRaw-0.22.2/bin/libraw.dll", Dll));
        var (repo, script) = FakeRepo("tampered", Dll, good);
        var tampered = _root.File("tampered.zip", 1, 2, 3); // not even a zip: extraction would fail with a different error

        var (code, output) = PowerShellRunner.Run("-File", script, "-PackagePath", tampered);

        Assert.NotEqual(0, code);
        Assert.Contains("source package SHA-256 mismatch", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo, "native", "x64", "libraw.dll")));
    }

    // A closed local port: any attempt to download fails immediately, so a test that expects "no network" cannot pass by accident
    // and a fallback attempt is observable ("Downloading ..." in the output) without touching the internet.
    private const string UnreachableUrl = "https://127.0.0.1:1/LibRaw-0.22.2-Win64.zip";

    private static readonly string[] CommittedPackageEntries = ["LibRaw-0.22.2/bin/libraw.dll", "LibRaw-0.22.2/LICENSE.LGPL", "LibRaw-0.22.2/LICENSE.CDDL"];

    private (string Repo, string Script, string CommittedZip, byte[] Package) FakeRepoWithCommittedPackage(string name)
    {
        var package = BuildPackage((CommittedPackageEntries[0], Dll), (CommittedPackageEntries[1], [1]), (CommittedPackageEntries[2], [2]));
        var (repo, script) = FakeRepo(name, Dll, package);
        var licenseDir = Path.Combine(repo, "native", "libraw");
        Directory.CreateDirectory(licenseDir);
        File.WriteAllText(Path.Combine(licenseDir, "NOTICE.txt"), "notice");
        var zip = Path.Combine(licenseDir, "LibRaw-0.22.2-Win64.zip");
        File.WriteAllBytes(zip, package);
        return (repo, script, zip, package);
    }

    [Fact(DisplayName = "fetch-libraw: a fresh checkout installs from the committed package without any download")]
    public void FetchLibRaw_FreshCheckout_UsesCommittedPackageWithoutNetwork()
    {
        var (repo, script, _, _) = FakeRepoWithCommittedPackage("committed");

        var (code, output) = PowerShellRunner.Run("-File", script, "-DownloadUrl", UnreachableUrl);

        Assert.True(code == 0, output);
        Assert.Equal(Dll, File.ReadAllBytes(Path.Combine(repo, "native", "x64", "libraw.dll")));
        Assert.Contains("committed", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Downloading", output, StringComparison.Ordinal);
        Assert.Equal(0, PowerShellRunner.Run("-File", script, "-Verify").ExitCode);
    }

    [Fact(DisplayName = "fetch-libraw: a committed package that fails its pin is not trusted; the script falls back to the download and fails closed")]
    public void FetchLibRaw_TamperedCommittedPackage_FallsBackToDownloadAndFailsClosed()
    {
        var (repo, script, zip, _) = FakeRepoWithCommittedPackage("committed-tampered");
        File.WriteAllBytes(zip, [1, 2, 3]); // not the pinned package (and not even a zip)

        var (code, output) = PowerShellRunner.Run("-File", script, "-DownloadUrl", UnreachableUrl);

        Assert.NotEqual(0, code);
        Assert.Contains("does not match native/libraw.package.sha256", output, StringComparison.Ordinal);
        Assert.Contains("Downloading", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo, "native", "x64", "libraw.dll")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(zip)); // the bad file is left alone, never installed over
    }

    [Fact(DisplayName = "fetch-libraw: a pinned package with an entry escaping the extraction root is refused and writes nothing outside")]
    public void FetchLibRaw_ZipSlipEntry_IsRefused()
    {
        var evilName = "zipslip-" + Guid.NewGuid().ToString("N") + ".txt";
        var evil = BuildPackage(("LibRaw-0.22.2/bin/libraw.dll", Dll), ("../" + evilName, [9]));
        var (repo, script) = FakeRepo("zipslip", Dll, evil); // the pin matches: only the entry guard can stop it
        var package = _root.File("evil.zip", evil);

        var (code, output) = PowerShellRunner.Run("-File", script, "-PackagePath", package);

        Assert.NotEqual(0, code);
        Assert.Contains("outside the temp directory", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), evilName)));
        Assert.False(File.Exists(Path.Combine(repo, "native", "x64", "libraw.dll")));
    }

    [Fact(DisplayName = "fetch-libraw: -Verify fails without installing when the DLL is missing")]
    public void FetchLibRaw_VerifyMode_MissingDll_FailsAndInstallsNothing()
    {
        var good = BuildPackage(("LibRaw-0.22.2/bin/libraw.dll", Dll));
        var (repo, script) = FakeRepo("verify", Dll, good);

        var (code, output) = PowerShellRunner.Run("-File", script, "-Verify");

        Assert.NotEqual(0, code);
        Assert.Contains("missing/mismatched", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repo, "native", "x64")));
    }

    [Fact(DisplayName = "fetch-libraw: a pinned good package installs, a stale DLL is detected by -Verify, and re-running repairs it")]
    public void FetchLibRaw_GoodPackage_InstallsDetectsStaleAndRepairs()
    {
        var good = BuildPackage(
            ("LibRaw-0.22.2/bin/libraw.dll", Dll), ("LibRaw-0.22.2/LICENSE.LGPL", [1]), ("LibRaw-0.22.2/LICENSE.CDDL", [2]));
        var (repo, script) = FakeRepo("good", Dll, good);
        var package = _root.File("good.zip", good);
        var installedDll = Path.Combine(repo, "native", "x64", "libraw.dll");
        // NOTICE.txt is repository content (not part of the package) and must exist for the pins to count as satisfied.
        Directory.CreateDirectory(Path.Combine(repo, "native", "libraw"));
        File.WriteAllText(Path.Combine(repo, "native", "libraw", "NOTICE.txt"), "notice");

        var install = PowerShellRunner.Run("-File", script, "-PackagePath", package);
        Assert.True(install.ExitCode == 0, install.Output);
        Assert.Equal(Dll, File.ReadAllBytes(installedDll));

        Assert.Equal(0, PowerShellRunner.Run("-File", script, "-Verify").ExitCode);

        File.WriteAllBytes(installedDll, [7, 7, 7]); // stale/wrong native DLL
        var stale = PowerShellRunner.Run("-File", script, "-Verify");
        Assert.NotEqual(0, stale.ExitCode);

        var repair = PowerShellRunner.Run("-File", script, "-PackagePath", package);
        Assert.True(repair.ExitCode == 0, repair.Output);
        Assert.Equal(Dll, File.ReadAllBytes(installedDll));
    }
}

/// <summary>tools/verify-release.ps1 legal-file content checks in a fake repository (checked before the version/msbuild step).</summary>
[Trait("Category", "Integration")]
public sealed class VerifyReleaseLegalContentTests : IDisposable
{
    private readonly TempRoot _root = new("verify-release-legal");
    public void Dispose() => _root.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static readonly string[] ReleaseFileNames =
    [
        "PhotoReview.App.exe", "PhotoReview.App.dll", "PhotoReview.App.deps.json", "PhotoReview.App.runtimeconfig.json",
        "PhotoReview.Core.dll", "PhotoReview.Imaging.dll", "PhotoReview.Platform.Windows.dll", "PhotoReview.Benchmarking.dll",
        "PhotoReview.PerfAnalysis.dll", "PhotoReview.Imaging.TurboJpeg.dll", "PhotoReview.Imaging.LibRaw.dll",
        "Languages\\en.json", "Languages\\vi.json",
    ];

    /// <param name="thirdParty">Content of THIRD-PARTY-NOTICES.md in the release folder; null omits the file.</param>
    /// <param name="fullPass">
    /// Also fakes the version step so a fully valid folder can reach the final PASS line: the stub project reports exactly the version
    /// resource of a real, git-versioned assembly of this build, which is copied in as PhotoReview.App.dll.
    /// </param>
    private (int ExitCode, string Output) Run(string lgpl, string cddl, string notice, string? thirdParty = GoodThirdParty, bool fullPass = false)
    {
        var name = "repo-" + Guid.NewGuid().ToString("N");
        var repo = _root.Dir(name);
        PowerShellRunner.CopyScript(repo, "verify-release.ps1");
        byte[] turbo = [1], libraw = [2], package = [3];
        Directory.CreateDirectory(Path.Combine(repo, "native"));
        File.WriteAllText(Path.Combine(repo, "native", "turbojpeg.sha256"), Sha(turbo));
        File.WriteAllText(Path.Combine(repo, "native", "libraw.sha256"), Sha(libraw));
        File.WriteAllText(Path.Combine(repo, "native", "libraw.package.sha256"), Sha(package));
        var release = _root.Dir(Path.Combine(name, "release"));
        foreach (var file in ReleaseFileNames) _root.File(Path.Combine(name, "release", file), 1);
        File.WriteAllBytes(Path.Combine(release, "turbojpeg.dll"), turbo);
        File.WriteAllBytes(Path.Combine(release, "libraw.dll"), libraw);
        File.WriteAllBytes(Path.Combine(release, "LibRaw-SOURCE.zip"), package);
        File.WriteAllText(Path.Combine(release, "LibRaw-LICENSE.LGPL"), lgpl);
        File.WriteAllText(Path.Combine(release, "LibRaw-LICENSE.CDDL"), cddl);
        File.WriteAllText(Path.Combine(release, "LibRaw-NOTICE.txt"), notice);
        if (thirdParty is not null) File.WriteAllText(Path.Combine(release, "THIRD-PARTY-NOTICES.md"), thirdParty);
        if (fullPass)
        {
            var assembly = typeof(PhotoReview.Benchmark.Cli.RawSurvey).Assembly.Location;
            File.Copy(assembly, Path.Combine(release, "PhotoReview.App.dll"), overwrite: true);
            var version = FileVersionInfo.GetVersionInfo(assembly);
            var projectDir = Path.Combine(repo, "src", "PhotoReview.App");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "PhotoReview.App.csproj"),
                "<Project><PropertyGroup><FileVersion>" + version.FileVersion + "</FileVersion><InformationalVersion>" + version.ProductVersion +
                "</InformationalVersion></PropertyGroup><Target Name=\"PhotoReviewComputeVersion\" /></Project>");
        }

        return PowerShellRunner.Run("-File", Path.Combine(repo, "tools", "verify-release.ps1"), "-ReleaseDirectory", release);
    }

    private const string GoodThirdParty = "# Third-Party Software Notices\n## libjpeg-turbo\nBSD-3-Clause / IJG License / zlib License";

    private const string GoodLgpl = "GNU LESSER GENERAL PUBLIC LICENSE Version 2.1";
    private const string GoodCddl = "COMMON DEVELOPMENT AND DISTRIBUTION LICENSE (CDDL) Version 1.0";
    private const string GoodNotice = "PhotoReview uses the unmodified LibRaw 0.22.2 library";

    [Theory(DisplayName = "verify-release: an empty legal file fails and names that file")]
    [InlineData("LibRaw-LICENSE.LGPL")]
    [InlineData("LibRaw-LICENSE.CDDL")]
    [InlineData("LibRaw-NOTICE.txt")]
    public void VerifyRelease_EmptyLegalFile_Fails(string file)
    {
        var (code, output) = Run(
            file == "LibRaw-LICENSE.LGPL" ? "  " : GoodLgpl, file == "LibRaw-LICENSE.CDDL" ? "" : GoodCddl, file == "LibRaw-NOTICE.txt" ? "\n" : GoodNotice);

        Assert.NotEqual(0, code);
        Assert.Contains(file + " is empty", output, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "verify-release: a legal file without its marker text fails")]
    [InlineData("LibRaw-LICENSE.LGPL")]
    [InlineData("LibRaw-LICENSE.CDDL")]
    [InlineData("LibRaw-NOTICE.txt")]
    public void VerifyRelease_WrongLegalContent_Fails(string file)
    {
        var (code, output) = Run(
            file == "LibRaw-LICENSE.LGPL" ? "something else" : GoodLgpl, file == "LibRaw-LICENSE.CDDL" ? "something else" : GoodCddl,
            file == "LibRaw-NOTICE.txt" ? "uses LibRaw 0.21.0" : GoodNotice);

        Assert.NotEqual(0, code);
        Assert.Contains(file + " does not contain the expected text", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release: correct legal files pass the content check")]
    public void VerifyRelease_GoodLegalFiles_PassContentCheck()
    {
        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, fullPass: true);

        Assert.True(code == 0, output);
        Assert.Contains("PASS: release files present", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release: the same fully valid folder fails once one legal file is wrong (control for the passing case)")]
    public void VerifyRelease_SameFolderWithWrongLegalFile_Fails()
    {
        var (code, output) = Run(GoodLgpl, "something else", GoodNotice, fullPass: true);

        Assert.NotEqual(0, code);
        Assert.Contains("LibRaw-LICENSE.CDDL does not contain the expected text", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS: release files present", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release: a release folder without THIRD-PARTY-NOTICES.md fails and names it")]
    public void VerifyRelease_MissingThirdPartyNotices_Fails()
    {
        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, thirdParty: null, fullPass: true);

        Assert.NotEqual(0, code);
        Assert.Contains("Missing release file(s): THIRD-PARTY-NOTICES.md", output, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "verify-release: an empty THIRD-PARTY-NOTICES.md or one without the libjpeg-turbo notice fails")]
    [InlineData("  ", "is empty")]
    [InlineData("nothing relevant", "does not contain the expected text")]
    public void VerifyRelease_BadThirdPartyNotices_Fails(string content, string expected)
    {
        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, thirdParty: content, fullPass: true);

        Assert.NotEqual(0, code);
        Assert.Contains("THIRD-PARTY-NOTICES.md " + expected, output, StringComparison.Ordinal);
    }
}

/// <summary>Runs tools/fetch-raw-samples.ps1 -SelfTest under Windows PowerShell 5.1 (the CI shell) so its array-shape regressions are gated locally too.</summary>
[Trait("Category", "Integration")]
public sealed class FetchRawSamplesSelfTestTests
{
    [Fact(DisplayName = "fetch-raw-samples: -SelfTest passes on Windows PowerShell (a one-row manifest still runs the license check)")]
    public void FetchRawSamples_SelfTest_Passes()
    {
        var (code, output) = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "fetch-raw-samples.ps1"), "-SelfTest");

        Assert.True(code == 0, output);
        Assert.Contains("PASS: fetch-raw-samples self-test", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "fetch-raw-samples: a -FormatFilter that matches nothing fails before any network access")]
    public void FetchRawSamples_UnknownFormatFilter_FailsNonZeroWithoutNetwork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fetch-raw-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manifest = Path.Combine(dir, "samples.txt");
            File.WriteAllText(manifest, "CR2\tCamera\thttps://raw.pixls.us/getfile.php/129/nice/sample.CR2\t" + new string('A', 64) +
                "\thttps://creativecommons.org/publicdomain/zero/1.0/\tsample.CR2\n", new UTF8Encoding(false));

            // The selection check runs before the (network) licence check, so this never reaches raw.pixls.us.
            var (code, output) = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "fetch-raw-samples.ps1"),
                "-SamplesFile", manifest, "-TargetDir", Path.Combine(dir, "out"), "-FormatFilter", "CR4");

            Assert.NotEqual(0, code);
            Assert.Contains("FormatFilter 'CR4' matches no sample", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Done:", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
