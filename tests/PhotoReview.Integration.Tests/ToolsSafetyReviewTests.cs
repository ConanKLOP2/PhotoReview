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

    [Fact(DisplayName = "fullscreen-capture cleanup: only the run-* subfolder PNGs are deleted (also from a failure path); foreign files in -Out survive; a non-run folder is refused")]
    public void FullscreenCapture_CleanupIsScopedToPerRunFolder()
    {
        var outDir = _root.Dir("fs-out");
        var foreignTop = Path.Combine(outDir, "mine.png");
        File.WriteAllText(foreignTop, "user png");
        var foreignDir = _root.Dir("fs-out/other");
        var foreignNested = Path.Combine(foreignDir, "keep.png");
        File.WriteAllText(foreignNested, "user png 2");
        var script = _root.File("fs-cleanup.ps1", Encoding.UTF8.GetBytes("""
            param($Lib, $Out, $Fail)
            $ErrorActionPreference = 'Stop'
            . $Lib
            $run = New-CaptureRunDir $Out
            if (-not (Test-Path -LiteralPath $run -PathType Container)) { throw 'run dir not created' }
            [IO.File]::WriteAllText((Join-Path $run 'sheet_a.png'), 'frame')
            [IO.File]::WriteAllText((Join-Path $run 'sheet_b.png'), 'frame')
            [IO.File]::WriteAllText((Join-Path $run 'keepme.txt'), 'backup')
            try {
                try { if ($Fail -eq '1') { throw 'simulated no monitor X' } }
                finally { Remove-CaptureFrames $run $Out }
            }
            catch { "CAUGHT=$($_.Exception.Message)" }
            "RUN=$run"
            $refused = $false
            try { Remove-CaptureFrames $Out $Out } catch { $refused = $true }
            "REFUSED_PARENT=$refused"
            """));

        foreach (var fail in new[] { "0", "1" })
        {
            var (code, output) = PowerShellRunner.Run("-File", script, "-Lib", Diag("Fullscreen-Capture-Cleanup.ps1"), "-Out", outDir, "-Fail", fail);
            Assert.True(code == 0, output);
            Assert.Equal(fail == "1", output.Contains("CAUGHT=simulated no monitor X", StringComparison.Ordinal));
            Assert.Contains("REFUSED_PARENT=True", output, StringComparison.Ordinal);
            var run = output.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("RUN=", StringComparison.Ordinal))[4..];
            Assert.True(Directory.Exists(run), "run folder keeps the non-PNG backup file");
            Assert.Empty(Directory.GetFiles(run, "*.png"));
            Assert.True(File.Exists(Path.Combine(run, "keepme.txt")));
            Assert.True(File.Exists(foreignTop), "a *.png the user already had in -Out must survive");
            Assert.True(File.Exists(foreignNested), "a *.png in another subfolder of -Out must survive");
        }
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
            $keys = @(); $bad = 0; $parts = @()
            foreach ($s in $splits) {
                $a = 0; $b = 0; $key = ''
                for ($i = 0; $i -lt 6; $i++) { if ($s[$i]) { $a++; $key += 'A' } else { $b++; $key += 'B' } }
                if ($a -ne 3 -or $b -ne 3) { $bad++ }
                $keys += $key
                # canonical unordered partition: the side containing run 0, as a string
                $side = ''; for ($i = 0; $i -lt 6; $i++) { if ($s[$i] -eq $s[0]) { $side += "$i" } }
                $parts += $side
            }
            "BAD=$bad DISTINCT=$(@($keys | Sort-Object -Unique).Count) PARTITIONS=$(@($parts | Sort-Object -Unique).Count)"
            """));
        var (code, output) = PowerShellRunner.Run("-File", script, "-Lib", Diag("Tune-Splits.ps1"));
        Assert.True(code == 0, output);
        Assert.Contains("COUNT=10 LEN=6", output, StringComparison.Ordinal);
        Assert.Contains("BAD=0 DISTINCT=10 PARTITIONS=10", output, StringComparison.Ordinal);
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
