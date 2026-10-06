[CmdletBinding()]
param(
    # Output directory, absolute or relative to the current directory (git-ignored: /work/ is in .gitignore). Raw .coverage files, the merged cobertura
    # XML, summary.json and coverage-report.md land here. Never commit it.
    [string]$OutDir = '',

    # Skip the Release build of the solution (use when it was just built).
    [switch]$SkipBuild,

    # Only measure these test projects ('Core', 'Core.Tests' or 'PhotoReview.Core.Tests'); default: all five.
    [string[]]$Project = @(),

    # Re-render the report from an existing merged cobertura file without running any test.
    [switch]$ReportOnly,

    # "Non-trivial" threshold for the per-file table: files with fewer coverable lines are listed
    # separately so tiny files do not dominate the lowest-first ranking.
    [int]$MinLines = 30,

    # How many files the per-file table lists (lowest line coverage first, files >= MinLines).
    [int]$Top = 40
)

<#
.SYNOPSIS
    Line/branch coverage baseline for the repo: runs the standard test filter per test project with
    coverage collection, merges, and writes a per-assembly and per-file markdown table.

.DESCRIPTION
    Measurement path (no package is added to any project): the VSTest "Code Coverage" data collector that
    ships inside Microsoft.NET.Test.Sdk (already referenced by every test project), plus the
    `dotnet-coverage` local tool (dotnet-tools.json) only to MERGE the per-project .coverage files into
    one cobertura file. Each project runs through tools/run-tests-hidden.ps1, so Category=UI real-WPF
    tests run (never skipped) on the private hidden desktop, with the same filter and hang guard
    (blame-hang 120s + the 20 min TestSessionTimeout) as the CI gate.

    Branch coverage: this collector's cobertura output has no branch data, so the report gives BLOCK
    coverage (from the native xml of the same merge) as the branch-level metric: a block is a
    straight-line IL run between branch points, so an untaken branch leaves its block uncovered.

    Excluded from the numbers: test/support assemblies, generated code ([GeneratedCode],
    [CompilerGenerated], [ExcludeFromCodeCoverage]), *.g.cs / *.g.i.cs / obj\ sources (XAML-generated
    and source-generator output), and the benchmark/perf-analysis tooling assemblies.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/coverage.ps1
    Full run (~5-10 min); report in work\coverage\coverage-report.md.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/coverage.ps1 -Project Core -OutDir work\cov-core
    One project, relative -OutDir (resolved against the current directory). Exits non-zero with a message
    when the merged cobertura has no packages or lines.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $root 'work\coverage' }
# Absolute from the start: a relative -OutDir would otherwise resolve against whichever working directory a
# child process (test host, dotnet-coverage) happens to have, and produce an empty merge.
if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path (Get-Location).ProviderPath $OutDir }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$merged = Join-Path $OutDir 'merged.cobertura.xml'
$mergedXml = Join-Path $OutDir 'merged.coverage.xml'
$rawDir = Join-Path $OutDir 'raw'

$allProjects = @('Architecture.Tests', 'Core.Tests', 'Imaging.Tests', 'Integration.Tests', 'App.Tests') |
    ForEach-Object { "PhotoReview.$_" }
if ($Project.Count -gt 0) {
    # Accept 'Core', 'Core.Tests' or 'PhotoReview.Core.Tests'.
    $wanted = $Project | ForEach-Object { $n = $_ -replace '^PhotoReview\.', ''; if ($n -notmatch '\.Tests$') { $n += '.Tests' }; "PhotoReview.$n" }
    $allProjects = @($allProjects | Where-Object { $wanted -contains $_ })
    if ($allProjects.Count -eq 0) { throw "-Project '$($Project -join ', ')' matches none of the test projects" }
}

if (-not $ReportOnly) {
    Push-Location $root
    try {
        dotnet tool restore | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

        if (Test-Path -LiteralPath $rawDir) { Remove-Item -Recurse -Force -LiteralPath $rawDir }
        New-Item -ItemType Directory -Force -Path $rawDir | Out-Null

        # Run settings: the repo hang guard (same values as tests/test.runsettings) + the Code Coverage collector.
        $settings = Join-Path $OutDir 'coverage.runsettings'
        @'
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <TestSessionTimeout>1200000</TestSessionTimeout>
  </RunConfiguration>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="blame" enabled="True">
        <Configuration>
          <CollectDumpOnTestSessionHang TestTimeout="120s" HangDumpType="None" />
        </Configuration>
      </DataCollector>
      <DataCollector friendlyName="Code Coverage" enabled="True">
        <Configuration>
          <CodeCoverage>
            <ModulePaths>
              <Include>
                <ModulePath>.*PhotoReview\..*\.dll$</ModulePath>
              </Include>
              <Exclude>
                <ModulePath>.*Tests?\.dll$</ModulePath>
                <ModulePath>.*TestSupport.*\.dll$</ModulePath>
                <ModulePath>.*PhotoReview\.Benchmark.*\.dll$</ModulePath>
                <ModulePath>.*PhotoReview\.PerfAnalysis.*\.dll$</ModulePath>
                <ModulePath>.*PhotoReview\.Localization\.Generator\.dll$</ModulePath>
              </Exclude>
            </ModulePaths>
            <Attributes>
              <Exclude>
                <Attribute>^System\.CodeDom\.Compiler\.GeneratedCodeAttribute$</Attribute>
                <Attribute>^System\.Runtime\.CompilerServices\.CompilerGeneratedAttribute$</Attribute>
                <Attribute>^System\.Diagnostics\.CodeAnalysis\.ExcludeFromCodeCoverageAttribute$</Attribute>
              </Exclude>
            </Attributes>
            <Sources>
              <Exclude>
                <Source>.*\.g\.cs$</Source>
                <Source>.*\.g\.i\.cs$</Source>
                <Source>.*\\obj\\.*</Source>
              </Exclude>
            </Sources>
            <UseVerifiableInstrumentation>True</UseVerifiableInstrumentation>
            <EnableStaticNativeInstrumentation>False</EnableStaticNativeInstrumentation>
            <EnableDynamicNativeInstrumentation>False</EnableDynamicNativeInstrumentation>
          </CodeCoverage>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
'@ | Set-Content -LiteralPath $settings -Encoding utf8

        if (-not $SkipBuild) {
            dotnet build (Join-Path $root 'PhotoReview.slnx') -c Release --nologo -v q
            if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
        }

        $failed = @()
        foreach ($name in $allProjects) {
            $csproj = Join-Path $root "tests\$name\$name.csproj"
            $res = Join-Path $rawDir $name
            $log = Join-Path $OutDir "$name.log"
            Write-Host "=== Coverage run: $name ===" -ForegroundColor Cyan
            # Same filter + hang flags as tools/run-tests-hidden.ps1 defaults; UI tests are NOT excluded.
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-tests-hidden.ps1') `
                -LogPath $log -DesktopName "PhotoReviewCov$($name -replace '\W','')" `
                $csproj -c Release --no-build `
                --filter 'Category!=Manual&Category!=Native&Category!=Slow' `
                --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none `
                --settings $settings --collect 'Code Coverage' --results-directory $res | Out-Host
            if ($LASTEXITCODE -ne 0) { $failed += $name }
        }
        if ($failed.Count -gt 0) { Write-Warning "Test failures in: $($failed -join ', ') (coverage still merged, but numbers are for a red run)" }

        # @(...) is load-bearing: with exactly one .coverage file (a single -Project) the pipeline yields a bare string,
        # and splatting `@files` of a string passes NO file to dotnet-coverage, which then writes an EMPTY merge.
        $files = @(Get-ChildItem -LiteralPath $rawDir -Recurse -Filter *.coverage | ForEach-Object FullName)
        if (-not $files) { throw "No .coverage files produced under $rawDir" }
        foreach ($m in @($merged, $mergedXml)) { if (Test-Path -LiteralPath $m) { Remove-Item -Force -LiteralPath $m } }
        dotnet tool run dotnet-coverage merge -f cobertura -o $merged @files
        if ($LASTEXITCODE -ne 0) { throw 'dotnet-coverage merge failed' }
        # Fail loudly on an empty merge (no packages / no lines) instead of writing a report of zeros.
        if (-not (Test-Path -LiteralPath $merged)) { throw "dotnet-coverage merge wrote no file at $merged" }
        $probe = Get-Content -LiteralPath $merged -Raw
        $pkgCount = ([regex]::Matches($probe, '<package\s')).Count
        $lineCount = ([regex]::Matches($probe, '<line\s')).Count
        if ($pkgCount -eq 0 -or $lineCount -eq 0) {
            throw "Merged cobertura $merged is empty ($pkgCount packages, $lineCount lines) from $($files.Count) .coverage file(s) under $rawDir; the instrumented test run produced no coverage data (see the per-project logs in $OutDir)."
        }
        # Cobertura from this collector carries NO branch data (every line is branch="False"), so the
        # same merge is also written in the native xml format, which has per-function block counts.
        dotnet tool run dotnet-coverage merge -f xml -o $mergedXml @files
        if ($LASTEXITCODE -ne 0) { throw 'dotnet-coverage merge (xml) failed' }
    }
    finally { Pop-Location }
}

if (-not (Test-Path -LiteralPath $merged)) { throw "Missing $merged (run without -ReportOnly first)" }

# ---- Parse cobertura: per file, de-duplicate lines (lambda/closure classes repeat their parent's lines) ----
[xml]$xml = Get-Content -LiteralPath $merged -Raw
$fileMap = @{}   # key "asm|path" -> @{ Asm; Path; Lines = @{ n -> @{Hit; BrCov; BrTot} } }
foreach ($pkg in $xml.coverage.packages.package) {
    $asm = $pkg.name
    foreach ($cls in $pkg.classes.class) {
        $key = "$asm|$($cls.filename)"
        if (-not $fileMap.ContainsKey($key)) { $fileMap[$key] = @{ Asm = $asm; Path = $cls.filename; Lines = @{} } }
        $lines = $fileMap[$key].Lines
        foreach ($ln in $cls.lines.line) {
            $n = [int]$ln.number; $hit = [int64]$ln.hits -gt 0
            if (-not $lines.ContainsKey($n)) { $lines[$n] = @{ Hit = $hit } }
            else { $lines[$n].Hit = $lines[$n].Hit -or $hit }
        }
    }
}

# ---- Block coverage per file from the native xml: the branch-level proxy (a block is a straight-line
# run of IL between branch points, so an untaken branch leaves its block uncovered). ----
$blockMap = @{}  # lower-cased full path -> @{ Cov; Tot }
[xml]$bx = Get-Content -LiteralPath $mergedXml -Raw
foreach ($mod in $bx.results.modules.module) {
    $src = @{}
    foreach ($sf in $mod.source_files.source_file) { $src[$sf.id] = $sf.path.ToLowerInvariant() }
    foreach ($fn in $mod.functions.function) {
        $first = $fn.ranges.range | Select-Object -First 1
        if (-not $first -or -not $src.ContainsKey($first.source_id)) { continue }
        $k = $src[$first.source_id]
        if (-not $blockMap.ContainsKey($k)) { $blockMap[$k] = @{ Cov = 0; Tot = 0 } }
        $c = [int]$fn.blocks_covered; $u = [int]$fn.blocks_not_covered
        $blockMap[$k].Cov += $c; $blockMap[$k].Tot += $c + $u
    }
}

$rows = foreach ($f in $fileMap.Values) {
    $lt = $f.Lines.Count
    if ($lt -eq 0) { continue }
    $lc = @($f.Lines.Values | Where-Object { $_.Hit }).Count
    $bk = $blockMap[$f.Path.ToLowerInvariant()]
    $bt = if ($bk) { $bk.Tot } else { 0 }; $bc = if ($bk) { $bk.Cov } else { 0 }
    [pscustomobject]@{ Assembly = $f.Asm; File = $f.Path; Lines = $lt; Covered = $lc; Branches = $bt; BranchCovered = $bc }
}

# Make paths repo-relative.
$rows | ForEach-Object {
    $p = $_.File
    $i = $p.IndexOf('\src\', [StringComparison]::OrdinalIgnoreCase)
    if ($i -ge 0) { $p = $p.Substring($i + 1) }
    $_.File = $p -replace '\\', '/'
}

function Pct([double]$c, [double]$t) { if ($t -le 0) { 'n/a' } else { '{0:N1}' -f (100.0 * $c / $t) } }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Coverage report ($(Get-Date -Format 'yyyy-MM-dd'))")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## Per assembly')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Assembly | Lines | Line % | Blocks | Block % |')
[void]$sb.AppendLine('|---|---:|---:|---:|---:|')
$asmRows = $rows | Group-Object Assembly | ForEach-Object {
    [pscustomobject]@{
        Assembly = $_.Name
        Lines = ($_.Group | Measure-Object Lines -Sum).Sum; Covered = ($_.Group | Measure-Object Covered -Sum).Sum
        Branches = ($_.Group | Measure-Object Branches -Sum).Sum; BranchCovered = ($_.Group | Measure-Object BranchCovered -Sum).Sum
    }
} | Sort-Object { $_.Covered / [Math]::Max(1, $_.Lines) }
foreach ($a in $asmRows) {
    [void]$sb.AppendLine("| $($a.Assembly) | $($a.Lines) | $(Pct $a.Covered $a.Lines) | $($a.Branches) | $(Pct $a.BranchCovered $a.Branches) |")
}
$tl = ($rows | Measure-Object Lines -Sum).Sum; $tc = ($rows | Measure-Object Covered -Sum).Sum
$tb = ($rows | Measure-Object Branches -Sum).Sum; $tbc = ($rows | Measure-Object BranchCovered -Sum).Sum
[void]$sb.AppendLine("| **Total** | $tl | $(Pct $tc $tl) | $tb | $(Pct $tbc $tb) |")
[void]$sb.AppendLine('')
[void]$sb.AppendLine("## Lowest-covered files (>= $MinLines coverable lines, lowest line % first, top $Top)")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| File | Lines | Line % | Block % |')
[void]$sb.AppendLine('|---|---:|---:|---:|')
$rows | Where-Object { $_.Lines -ge $MinLines } |
    Sort-Object @{ Expression = { $_.Covered / $_.Lines } }, @{ Expression = { -$_.Lines } } | Select-Object -First $Top |
    ForEach-Object { [void]$sb.AppendLine("| $($_.File) | $($_.Lines) | $(Pct $_.Covered $_.Lines) | $(Pct $_.BranchCovered $_.Branches) |") }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## All files (lowest line % first)')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Assembly | File | Lines | Line % | Block % |')
[void]$sb.AppendLine('|---|---|---:|---:|---:|')
$rows | Sort-Object @{ Expression = { $_.Covered / $_.Lines } }, @{ Expression = { -$_.Lines } } |
    ForEach-Object { [void]$sb.AppendLine("| $($_.Assembly) | $($_.File) | $($_.Lines) | $(Pct $_.Covered $_.Lines) | $(Pct $_.BranchCovered $_.Branches) |") }

$reportPath = Join-Path $OutDir 'coverage-report.md'
$sb.ToString() | Set-Content -LiteralPath $reportPath -Encoding utf8
[pscustomobject]@{ Assemblies = $asmRows; Files = $rows } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'summary.json') -Encoding utf8

Write-Host "Report: $reportPath" -ForegroundColor Green
Write-Host "Merged cobertura: $merged" -ForegroundColor Green
$asmRows | Format-Table Assembly, Lines, Covered, Branches, BranchCovered -AutoSize | Out-Host
