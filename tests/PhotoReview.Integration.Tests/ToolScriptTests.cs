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
        "PhotoReview.Core.dll", "PhotoReview.Imaging.dll", "PhotoReview.Imaging.Wpf.dll", "PhotoReview.App.Shared.dll", "PhotoReview.App.WpfWindows.dll", "PhotoReview.Platform.Windows.dll", "PhotoReview.Benchmarking.dll",
        "PhotoReview.PerfAnalysis.dll", "PhotoReview.Imaging.TurboJpeg.dll", "PhotoReview.Imaging.LibRaw.dll", "turbojpeg.dll",
        // WP-30: the Win32 shell executable and the libraries only it needs.
        "PhotoReview.exe", "PhotoReview.dll", "PhotoReview.deps.json", "PhotoReview.runtimeconfig.json",
        "PhotoReview.Shell.Interop.dll", "PhotoReview.Shell.Rendering.dll", "PhotoReview.Shell.WpfBridge.dll",
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

        // Every pin now matches: the default mode is a no-op that never touches the network.
        var again = PowerShellRunner.Run("-File", script, "-DownloadUrl", UnreachableUrl);
        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Contains("PASS: LibRaw DLL", again.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading", again.Output, StringComparison.Ordinal);
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
        "PhotoReview.Core.dll", "PhotoReview.Imaging.dll", "PhotoReview.Imaging.Wpf.dll", "PhotoReview.App.Shared.dll", "PhotoReview.App.WpfWindows.dll", "PhotoReview.Platform.Windows.dll", "PhotoReview.Benchmarking.dll",
        "PhotoReview.PerfAnalysis.dll", "PhotoReview.Imaging.TurboJpeg.dll", "PhotoReview.Imaging.LibRaw.dll",
        "PhotoReview.exe", "PhotoReview.dll", "PhotoReview.runtimeconfig.json", "PhotoReview.Shell.Interop.dll",
        "PhotoReview.Shell.Rendering.dll", "PhotoReview.Shell.WpfBridge.dll",
        "Languages\\en.json", "Languages\\vi.json",
    ];

    /// <summary>A minimal valid dependency manifest; <paramref name="extraAssembly"/> adds an assembly the folder may not contain.</summary>
    private static string Deps(string? extraAssembly = null) =>
        "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0\"},\"targets\":{\".NETCoreApp,Version=v10.0\":{" +
        "\"PhotoReview.Core/1.0.0\":{\"runtime\":{\"PhotoReview.Core.dll\":{}}}" +
        (extraAssembly is null ? "" : ",\"Extra/1.0.0\":{\"runtime\":{\"lib/net10.0/" + extraAssembly + "\":{}}}") + "}}}";

    /// <param name="thirdParty">Content of THIRD-PARTY-NOTICES.md in the release folder; null omits the file.</param>
    /// <param name="fullPass">
    /// Also fakes the version step so a fully valid folder can reach the final PASS line: the stub project reports exactly the version
    /// resource of a real, git-versioned assembly of this build, which is copied in as PhotoReview.App.dll.
    /// </param>
    private (int ExitCode, string Output) Run(string lgpl, string cddl, string notice, string? thirdParty = GoodThirdParty, bool fullPass = false,
        string? skipFile = null, string? depsExtra = null, string? emptyFile = null, bool smoke = false)
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
        foreach (var file in ReleaseFileNames)
        {
            if (file == skipFile) continue;
            _root.File(Path.Combine(name, "release", file), file == emptyFile ? [] : [1]);
        }
        File.WriteAllText(Path.Combine(release, "PhotoReview.App.deps.json"), Deps(depsExtra));
        File.WriteAllText(Path.Combine(release, "PhotoReview.deps.json"), Deps());
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
            if (skipFile != "PhotoReview.dll") File.Copy(assembly, Path.Combine(release, "PhotoReview.dll"), overwrite: true);
            var version = FileVersionInfo.GetVersionInfo(assembly);
            var projectDir = Path.Combine(repo, "src", "PhotoReview.App");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "PhotoReview.App.csproj"),
                "<Project><PropertyGroup><FileVersion>" + version.FileVersion + "</FileVersion><InformationalVersion>" + version.ProductVersion +
                "</InformationalVersion></PropertyGroup><Target Name=\"PhotoReviewComputeVersion\" /></Project>");
        }

        return smoke
            ? PowerShellRunner.Run("-File", Path.Combine(repo, "tools", "verify-release.ps1"), "-ReleaseDirectory", release, "-Smoke")
            : PowerShellRunner.Run("-File", Path.Combine(repo, "tools", "verify-release.ps1"), "-ReleaseDirectory", release);
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

    [Theory(DisplayName = "verify-release (WP-30): a folder missing a shell file fails and names it")]
    [InlineData("PhotoReview.exe")]
    [InlineData("PhotoReview.dll")]
    [InlineData("PhotoReview.runtimeconfig.json")]
    [InlineData("PhotoReview.Shell.Interop.dll")]
    [InlineData("PhotoReview.Shell.Rendering.dll")]
    [InlineData("PhotoReview.Shell.WpfBridge.dll")]
    public void VerifyRelease_MissingShellFile_Fails(string file)
    {
        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, fullPass: true, skipFile: file);

        Assert.NotEqual(0, code);
        Assert.Contains("Missing release file(s): " + file, output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release (WP-30): an empty PhotoReview.exe fails")]
    public void VerifyRelease_EmptyShellExe_Fails()
    {
        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, fullPass: true, emptyFile: "PhotoReview.exe");

        Assert.NotEqual(0, code);
        Assert.Contains("Shell executable PhotoReview.exe is empty", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release (WP-30): an assembly named by a deps.json but absent from the folder fails and names it")]
    public void VerifyRelease_DepsNamesMissingAssembly_Fails()
    {
        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, fullPass: true, depsExtra: "PhotoReview.Gone.dll");

        Assert.NotEqual(0, code);
        // Windows PowerShell wraps long Write-Error lines at the console width: compare on collapsed whitespace.
        var flat = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.Contains("PhotoReview.App.deps.json names assemblies that are not in the release folder: PhotoReview.Gone.dll", flat, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "verify-release (WP-30): -Smoke fails when PhotoReview.exe cannot be started or does not exit 0 (control: same folder passes without -Smoke)")]
    public void VerifyRelease_SmokeWithFakeShellExe_Fails()
    {
        var (okCode, okOutput) = Run(GoodLgpl, GoodCddl, GoodNotice, fullPass: true);
        Assert.True(okCode == 0, okOutput);
        Assert.Contains("SHELL EXE SHA256:", okOutput, StringComparison.Ordinal);

        var (code, output) = Run(GoodLgpl, GoodCddl, GoodNotice, fullPass: true, smoke: true);

        Assert.NotEqual(0, code);
        Assert.DoesNotContain("PASS: release files present", output, StringComparison.Ordinal);
    }
}

/// <summary>
/// deploy/install-photo-review-association.ps1 and uninstall: the Win32 shell registers under its OWN ProgId, never touches the
/// WPF registration or any file type's default, and is opt-in. Every run targets a private HKCU key (-ClassesPath), never the
/// real Software\Classes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShellAssociationScriptTests : IDisposable
{
    private readonly PhotoReview.TestSupport.TempRoot _root = new("shell-association");
    private readonly string _keyName = @"Software\PhotoReviewTests\assoc-" + Guid.NewGuid().ToString("N");
    private string ClassesPath => @"HKCU:\" + _keyName;

    public void Dispose()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_keyName, throwOnMissingSubKey: false);
        // Leave no empty parent behind (another test class instance may still use it: only an empty one is removed).
        try
        {
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PhotoReviewTests");
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\PhotoReviewTests", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* best effort */ }
        _root.Dispose();
    }

    private string Exe(string name = "PhotoReview.exe") => _root.File(name, 1);

    private static string Script(string name) => Path.Combine(PowerShellRunner.RepoRoot(), "deploy", name);

    private (int ExitCode, string Output) Install(string exe, params string[] extra) =>
        PowerShellRunner.Run(["-File", Script("install-photo-review-association.ps1"), "-ExePath", exe, "-ClassesPath", ClassesPath, .. extra]);

    private (int ExitCode, string Output) Uninstall(params string[] extra) =>
        PowerShellRunner.Run(["-File", Script("uninstall-photo-review-association.ps1"), "-ClassesPath", ClassesPath, .. extra]);

    private Microsoft.Win32.RegistryKey? Open(string relative) => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyName + "\\" + relative);

    private string? Default(string relative)
    {
        using var key = Open(relative);
        return key?.GetValue(null) as string;
    }

    private void SeedUserJpgDefault()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_keyName + @"\.jpg");
        key.SetValue(null, "jpegfile");
        using var progs = key.CreateSubKey("OpenWithProgids");
        progs.SetValue("SomeoneElse.Viewer", "");
    }

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    [Fact(DisplayName = "association (WP-30): -Variant Shell without -Force in a non-interactive host writes nothing")]
    public void Shell_WithoutForce_NonInteractive_WritesNothing()
    {
        var (code, _) = Install(Exe(), "-Variant", "Shell");

        Assert.NotEqual(0, code);
        Assert.Null(Open("PhotoReview.Shell"));
        Assert.Null(Open(@"Directory\shell\PhotoReview.Shell"));
    }

    [Fact(DisplayName = "association (WP-30): -Variant Shell -WhatIf writes nothing")]
    public void Shell_WhatIf_WritesNothing()
    {
        var (code, output) = Install(Exe(), "-Variant", "Shell", "-WhatIf");

        Assert.True(code == 0, output);
        Assert.Null(Open("PhotoReview.Shell"));
        Assert.Null(Open(".jpg"));
    }

    [Fact(DisplayName = "association (WP-30): -Variant Shell -Force registers ProgId PhotoReview.Shell with the exe and %1, and its own folder verbs")]
    public void Shell_Force_RegistersOwnProgIdAndVerbs()
    {
        var exe = Exe();

        var (code, output) = Install(exe, "-Variant", "Shell", "-Force");

        Assert.True(code == 0, output);
        Assert.Equal("Photo Review (Win32)", Default("PhotoReview.Shell"));
        Assert.Equal($"\"{exe}\" \"%1\"", Default(@"PhotoReview.Shell\shell\open\command"));
        foreach (var ext in Extensions)
        {
            using var progs = Open(ext + @"\OpenWithProgids");
            Assert.NotNull(progs);
            Assert.Contains("PhotoReview.Shell", progs!.GetValueNames());
            Assert.DoesNotContain("PhotoReview.App", progs.GetValueNames());
        }
        Assert.Equal($"\"{exe}\" \"%1\"", Default(@"Directory\shell\PhotoReview.Shell\command"));
        Assert.Equal($"\"{exe}\" \"%V\"", Default(@"Directory\Background\shell\PhotoReview.Shell\command"));
    }

    [Fact(DisplayName = "association (WP-30): registering the shell never touches the WPF registration or a file type's default")]
    public void Shell_Force_LeavesWpfAndUserDefaultsAlone()
    {
        SeedUserJpgDefault();
        var wpfExe = Exe("PhotoReview.App.exe");
        Assert.Equal(0, Install(wpfExe).ExitCode);

        var (code, output) = Install(Exe(), "-Variant", "Shell", "-Force");

        Assert.True(code == 0, output);
        Assert.Equal("jpegfile", Default(".jpg"));
        using (var progs = Open(@".jpg\OpenWithProgids"))
            Assert.Contains("SomeoneElse.Viewer", progs!.GetValueNames());
        Assert.Equal($"\"{wpfExe}\" \"%1\"", Default(@"PhotoReview.App\shell\open\command"));
        Assert.Equal($"\"{wpfExe}\" \"%1\"", Default(@"Directory\shell\PhotoReview\command"));
        Assert.Equal("Browse with PhotoReview", Default(@"Directory\shell\PhotoReview"));
    }

    [Fact(DisplayName = "association (WP-30): -NoFolderMenu skips the folder verbs for the shell variant")]
    public void Shell_NoFolderMenu_SkipsVerbs()
    {
        var (code, output) = Install(Exe(), "-Variant", "Shell", "-Force", "-NoFolderMenu");

        Assert.True(code == 0, output);
        Assert.NotNull(Open("PhotoReview.Shell"));
        Assert.Null(Open(@"Directory\shell\PhotoReview.Shell"));
        Assert.Null(Open(@"Directory\Background\shell\PhotoReview.Shell"));
    }

    [Fact(DisplayName = "association (WP-30): the default variant keeps the WPF ProgId and verb key and needs no confirmation")]
    public void Wpf_DefaultVariant_Unchanged()
    {
        var exe = Exe("PhotoReview.App.exe");

        var (code, output) = Install(exe);

        Assert.True(code == 0, output);
        Assert.Equal("Photo Review", Default("PhotoReview.App"));
        Assert.Equal($"\"{exe}\" \"%1\"", Default(@"PhotoReview.App\shell\open\command"));
        Assert.Equal("Browse with PhotoReview", Default(@"Directory\shell\PhotoReview"));
        Assert.Null(Open("PhotoReview.Shell"));
        Assert.Null(Open(@"Directory\shell\PhotoReview.Shell"));
    }

    [Fact(DisplayName = "association (WP-30): uninstalling the shell variant removes only the shell keys and values")]
    public void Shell_Uninstall_RemovesOnlyShellRegistration()
    {
        SeedUserJpgDefault();
        var wpfExe = Exe("PhotoReview.App.exe");
        Install(wpfExe);
        Install(Exe(), "-Variant", "Shell", "-Force");

        var (code, output) = Uninstall("-Variant", "Shell");

        Assert.True(code == 0, output);
        Assert.Null(Open("PhotoReview.Shell"));
        Assert.Null(Open(@"Directory\shell\PhotoReview.Shell"));
        Assert.Null(Open(@"Directory\Background\shell\PhotoReview.Shell"));
        using (var progs = Open(@".jpg\OpenWithProgids"))
        {
            Assert.DoesNotContain("PhotoReview.Shell", progs!.GetValueNames());
            Assert.Contains("PhotoReview.App", progs.GetValueNames());
            Assert.Contains("SomeoneElse.Viewer", progs.GetValueNames());
        }
        Assert.Equal($"\"{wpfExe}\" \"%1\"", Default(@"PhotoReview.App\shell\open\command"));
        Assert.NotNull(Open(@"Directory\shell\PhotoReview"));
        Assert.Equal("jpegfile", Default(".jpg"));
    }

    [Fact(DisplayName = "association (WP-30): an executable that does not exist fails before any key is written")]
    public void Shell_MissingExe_Fails()
    {
        var (code, _) = Install(Path.Combine(_root.Dir("nowhere"), "PhotoReview.exe"), "-Variant", "Shell", "-Force");

        Assert.NotEqual(0, code);
        Assert.Null(Open("PhotoReview.Shell"));
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
