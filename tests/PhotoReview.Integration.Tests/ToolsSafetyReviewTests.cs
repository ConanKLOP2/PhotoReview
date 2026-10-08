using System.IO;
using System.Text;
using System.Text.Json;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Dev-tooling safety fixes (static review): coverage.ps1 raw-folder ownership, fullscreen-capture.ps1 cleanup scope,
/// tune-rank A/A split representation, make-subset-fixture.ps1 alias registration. Temp directories only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ToolsSafetyReviewTests : IDisposable
{
    private readonly TempRoot _root = new("tools-safety");

    public void Dispose() => _root.Dispose();

    private static string Diag(string name) => Path.Combine(PowerShellRunner.RepoRoot(), "tools", "diag", name);

    [Fact(DisplayName = "coverage.ps1 refuses to delete a pre-existing <OutDir>\\raw it did not create (no ownership marker)")]
    public void Coverage_RefusesForeignRawFolder()
    {
        var outDir = _root.Dir("cov");
        var raw = _root.Dir("cov/raw");
        var userFile = Path.Combine(raw, "precious.txt");
        File.WriteAllText(userFile, "user data");

        var (code, output) = PowerShellRunner.Run("-File", Path.Combine(PowerShellRunner.RepoRoot(), "tools", "coverage.ps1"), "-OutDir", outDir, "-SkipBuild", "-Project", "Core");

        Assert.NotEqual(0, code);
        Assert.Contains("Refusing to delete", output, StringComparison.Ordinal);
        Assert.True(File.Exists(userFile), "the foreign raw folder content must survive");
    }

    [Fact(DisplayName = "fullscreen-capture.ps1 captures into a unique per-run subfolder and its cleanup never touches other files in -Out")]
    public void FullscreenCapture_CleanupIsScopedToPerRunFolder()
    {
        // The rig needs a real monitor and a launched app, so this is a source-level guard: -Out is re-rooted to a unique run-* subfolder
        // before anything is written, and the only PNG cleanup runs on that (re-rooted) folder.
        var text = File.ReadAllText(Diag("fullscreen-capture.ps1"));
        var reroot = text.IndexOf("$Out = Join-Path $Out ('run-'", StringComparison.Ordinal);
        var firstUse = text.IndexOf("$backup = Join-Path $Out", StringComparison.Ordinal);
        var cleanup = text.IndexOf("Remove-Item -Force", text.LastIndexOf("--- summary", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(reroot > 0, "-Out must be re-rooted into a per-run subfolder");
        Assert.True(reroot < firstUse, "re-root must precede the first write into $Out");
        Assert.True(cleanup > reroot, "cleanup must come after the re-root (so it only sees the per-run folder)");
        Assert.Equal(-1, text.IndexOf("Get-ChildItem $Out -Filter", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "tune-rank A/A splits: 40 runs give 100 balanced splits and runs >= 32 are not pinned to run i-32")]
    public void TuneSplits_AreCorrectBeyond32Runs()
    {
        const int n = 40;
        var script = _root.File("splits.ps1", Encoding.UTF8.GetBytes("""
            param($Lib, [int]$N)
            $ErrorActionPreference = 'Stop'
            . $Lib
            $splits = New-AaSplits $N (New-Object System.Random(42))
            $unbalanced = 0; $pinned = 0
            foreach ($s in $splits) {
                $c = 0; for ($i = 0; $i -lt $N; $i++) { if ($s[$i]) { $c++ } }
                if ($c -ne [math]::Floor($N / 2)) { $unbalanced++ }
            }
            # run i (i >= 32) must be able to differ from run i-32 in some split
            for ($i = 32; $i -lt $N; $i++) {
                $differs = $false
                foreach ($s in $splits) { if ($s[$i] -ne $s[$i - 32]) { $differs = $true; break } }
                if (-not $differs) { $pinned++ }
            }
            "COUNT=$($splits.Count) UNBALANCED=$unbalanced PINNED=$pinned"
            """));

        var (code, output) = PowerShellRunner.Run("-File", script, "-Lib", Diag("Tune-Splits.ps1"), "-N", n.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(code == 0, output);
        Assert.Contains("COUNT=100 UNBALANCED=0 PINNED=0", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "tune-rank A/A splits: n <= 10 stays exhaustive and balanced (C(6,3)/2 = 10)")]
    public void TuneSplits_SmallNIsExhaustive()
    {
        var script = _root.File("splits-small.ps1", Encoding.UTF8.GetBytes("""
            param($Lib)
            . $Lib
            $splits = New-AaSplits 6 (New-Object System.Random(1))
            "COUNT=$($splits.Count) LEN=$($splits[0].Length)"
            """));
        var (code, output) = PowerShellRunner.Run("-File", script, "-Lib", Diag("Tune-Splits.ps1"));
        Assert.True(code == 0, output);
        Assert.Contains("COUNT=10 LEN=6", output, StringComparison.Ordinal);
    }

    private string RegisterAlias(string fixtures, string alias, string newPath)
    {
        var inFile = _root.File("fixtures-in.json", new UTF8Encoding(false).GetBytes(fixtures));
        var outFile = _root.Combine("fixtures-out.json");
        var script = _root.File("alias.ps1", Encoding.UTF8.GetBytes("""
            param($Lib, $In, $Out, $Alias, $NewPath)
            $ErrorActionPreference = 'Stop'
            . $Lib
            $line = '  ' + (ConvertTo-JsonString $Alias) + ': { "path": ' + (ConvertTo-JsonString $NewPath) + ', "files": 7, "desc": "new" }'
            $raw = [System.IO.File]::ReadAllText($In)
            [System.IO.File]::WriteAllText($Out, (Set-FixtureAliasText $raw $Alias $line))
            """));
        var (code, output) = PowerShellRunner.Run("-File", script, "-Lib", Diag("Fixture-Alias.ps1"), "-In", inFile, "-Out", outFile, "-Alias", alias, "-NewPath", newPath);
        Assert.True(code == 0, output);
        return File.ReadAllText(outFile);
    }

    [Fact(DisplayName = "make-subset-fixture alias: a multi-line existing entry is replaced whole, other keys untouched, result is valid JSON")]
    public void Alias_MultiLineEntryReplacedWhole()
    {
        const string fixtures = "{\r\n  \"F4\": { \"path\": \"C:\\\\Xiuren\\\\[[WALLPAPER]\", \"files\": 1 },\r\n  \"F-small\": {\r\n    \"path\": \"C:\\\\old\\\\small\",\r\n    \"files\": 3,\r\n    \"desc\": \"old\"\r\n  },\r\n  \"Z\": { \"path\": \"D:\\\\z\" }\r\n}";

        var result = RegisterAlias(fixtures, "F-small", @"C:\Xiuren\_tune\small");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(["F4", "F-small", "Z"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(@"C:\Xiuren\_tune\small", doc.RootElement.GetProperty("F-small").GetProperty("path").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("F-small").GetProperty("files").GetInt32());
        Assert.Contains("\"F4\": { \"path\": \"C:\\\\Xiuren\\\\[[WALLPAPER]\", \"files\": 1 },", result, StringComparison.Ordinal);
        Assert.Contains("\"Z\": { \"path\": \"D:\\\\z\" }", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", result, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "make-subset-fixture alias: single-line entry (unescaped-backslash repo style) is replaced in place, a new alias is appended")]
    public void Alias_SingleLineAndAppend_KeepRepoStyle()
    {
        const string fixtures = "{\r\n  \"F4\": { \"path\": \"C:\\Xiuren\\[[WALLPAPER]\" },\r\n  \"F-small\": { \"path\": \"C:\\old\", \"files\": 3 },\r\n  \"Z\": { \"path\": \"D:\\z\" }\r\n}";

        var replaced = RegisterAlias(fixtures, "F-small", @"C:\new");
        Assert.Contains("\"F4\": { \"path\": \"C:\\Xiuren\\[[WALLPAPER]\" },", replaced, StringComparison.Ordinal);
        Assert.Contains("\"F-small\": { \"path\": \"C:\\\\new\", \"files\": 7, \"desc\": \"new\" },", replaced, StringComparison.Ordinal);
        Assert.Contains("\"Z\": { \"path\": \"D:\\z\" }", replaced, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\old", replaced, StringComparison.Ordinal);

        var appended = RegisterAlias(fixtures, "F-new", @"C:\x");
        Assert.Contains("\"Z\": { \"path\": \"D:\\z\" },\r\n  \"F-new\":", appended, StringComparison.Ordinal);
        Assert.StartsWith(fixtures.Substring(0, fixtures.LastIndexOf('}') - 1).TrimEnd(), appended.TrimEnd(), StringComparison.Ordinal);
    }
}
