[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RequireSelfContained,
    [string]$ReleaseDirectory = '',
    [switch]$Native,
    [switch]$Slow,
    [switch]$All,
    [switch]$TestReport,
    [Parameter(DontShow)]
    [scriptblock]$TestCommandInvoker
)

# Usage examples:
# ./verify-all.ps1                    # Default: HotPath only (~2-3 min)
# ./verify-all.ps1 -Slow               # Include 10+ second tests
# ./verify-all.ps1 -Native             # Include real-OS tests (Recycle Bin, shell)
# ./verify-all.ps1 -All                # Everything (CI+local exhaustive)
# ./verify-all.ps1 -TestReport         # Print test timing report (requires running tests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'PhotoReview.slnx'
$appProject = Join-Path $root 'src\PhotoReview.App\PhotoReview.App.csproj'

# Build filter string dynamically
# NOTE: This filter is verified to match TEST_FILTER in .github/workflows/ci.yml and AGENTS.md > Tests.
# Category=Integration tests are not excluded (Q-R3); an extra pass below runs them even when their class is Slow.
# xUnit uses & (not AND) to join filter conditions
$filter = "Category!=Manual"  # Always exclude Manual
if (-not $All) {
    if (-not $Native) { $filter += "&Category!=Native" }
    if (-not $Slow) { $filter += "&Category!=Slow" }
}
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    # Matches the framework-dependent artifact path documented in README.md/AGENTS.md
    # (src/PhotoReview.App/bin/Release/net10.0-windows/publish for -Configuration Release),
    # so running this gate with no override actually populates the documented location.
    [xml]$appProjectXml = Get-Content -LiteralPath $appProject
    $targetFramework = $appProjectXml.SelectSingleNode('//TargetFramework').InnerText
    $ReleaseDirectory = Join-Path $root "src\PhotoReview.App\bin\$Configuration\$targetFramework\publish"
}

function Publish-ReleaseDirectory([string]$Directory, [bool]$SelfContained) {
    # Wiping first stops verify-release.ps1 from confirming a stale artifact left
    # over from an earlier publish (different code, coincidentally matching
    # FileVersion) as if it were this run's build.
    # Guard: only ever wipe a directory that is unmistakably a publish output (a stray -ReleaseDirectory such
    # as '.' or the repo root must never be deleted recursively).
    $leaf = Split-Path -Leaf ([System.IO.Path]::GetFullPath($Directory).TrimEnd([char]92, [char]47))
    if ($leaf -notin @('publish', 'PhotoReview-self-contained')) {
        throw "Refusing to wipe '$Directory': the release directory name must be 'publish' or 'PhotoReview-self-contained'."
    }
    if (Test-Path -LiteralPath $Directory) { Remove-Item -LiteralPath $Directory -Recurse -Force }
    $publishArgs = @($appProject, '-c', $Configuration, '-o', $Directory, '--nologo')
    if ($SelfContained) { $publishArgs += @('--self-contained', 'true', '-r', 'win-x64') }
    else { $publishArgs += @('--self-contained', 'false') }
    dotnet publish @publishArgs
}

function Find-TrxFiles {
    param([string[]]$TestProjects)

    $trxFiles = @()
    foreach ($testProject in $TestProjects) {
        # dotnet test is invoked with --results-directory (see the test loop below), so .trx files land here.
        $resultsPath = Join-Path $root "TestResults\$testProject"
        if (Test-Path -LiteralPath $resultsPath) {
            $trxFiles += @(Get-ChildItem -LiteralPath $resultsPath -Filter "*.trx" -Recurse -ErrorAction SilentlyContinue)
        }
    }
    return $trxFiles
}

function Parse-TrxFile {
    param([System.IO.FileInfo]$TrxFile)

    [xml]$trxContent = Get-Content -LiteralPath $TrxFile.FullName
    $ns = @{ trx = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }

    $unitTests = $trxContent.SelectNodes('//trx:UnitTestResult', $ns)
    $testDefinitions = $trxContent.SelectNodes('//trx:UnitTest', $ns)
    $results = @()

    foreach ($unitTest in $unitTests) {
        $testName = $unitTest.GetAttribute('testName')
        $durationStr = $unitTest.GetAttribute('duration')

        # Parse duration (format: HH:MM:SS.mmm)
        $duration = [timespan]::Zero
        if ($durationStr) {
            try {
                $duration = [timespan]::Parse($durationStr)
            }
            catch {
                $duration = [timespan]::Zero
            }
        }

        # Extract test full name and category from test definitions
        $testDef = $testDefinitions | Where-Object { $_.GetAttribute('name') -eq $testName } | Select-Object -First 1
        $category = 'Default'
        if ($testDef) {
            $categoryNode = $testDef.SelectSingleNode('.//trx:TestCategory/trx:TestCategoryItem', $ns)
            if ($categoryNode) {
                $category = $categoryNode.GetAttribute('TestCategory')
            }
        }

        $results += [PSCustomObject]@{
            TestName = $testName
            Duration = $duration
            DurationSeconds = $duration.TotalSeconds
            Category = $category
            Outcome = $unitTest.GetAttribute('outcome')
        }
    }

    return $results
}

function Generate-TestReport {
    param(
        [string[]]$TestProjects,
        [string]$RootPath
    )

    Write-Host "`n=== Test Report ===" -ForegroundColor Cyan

    $trxFiles = Find-TrxFiles -TestProjects $TestProjects

    if ($trxFiles.Count -eq 0) {
        Write-Host "No .trx files found" -ForegroundColor Yellow
        return
    }

    $allTests = @()
    $projectTimings = @{}

    # Parse all trx files
    foreach ($trxFile in $trxFiles) {
        $tests = Parse-TrxFile -TrxFile $trxFile
        $allTests += $tests

        $projectName = Split-Path -Parent $trxFile.FullName | ForEach-Object {
            if ($_ -match 'tests\\(.+?)\\obj') {
                $matches[1]
            }
        }

        if ($projectName) {
            $projectDuration = ($tests | Measure-Object -Property DurationSeconds -Sum).Sum
            if ($projectTimings.ContainsKey($projectName)) {
                $projectTimings[$projectName] += $projectDuration
            }
            else {
                $projectTimings[$projectName] = $projectDuration
            }
        }
    }

    # Per-project wall times
    Write-Host "`nPer-Project Wall Time (60s threshold):" -ForegroundColor Cyan
    $projectWarnings = @()
    foreach ($project in $projectTimings.Keys | Sort-Object) {
        $duration = $projectTimings[$project]
        $status = if ($duration -le 60) { "PASS" } else { "WARN" }
        $color = if ($status -eq "PASS") { "Green" } else { "Yellow" }

        Write-Host "  $project`: $("{0:F2}" -f $duration)s [$status]" -ForegroundColor $color

        if ($status -eq "WARN") {
            $projectWarnings += "$project exceeds 60s threshold ($("{0:F2}" -f $duration)s)"
        }
    }

    # 10 slowest tests
    Write-Host "`n10 Slowest Tests (5s threshold for non-Slow category):" -ForegroundColor Cyan
    $slowestTests = $allTests | Sort-Object -Property DurationSeconds -Descending | Select-Object -First 10

    $testWarnings = @()
    foreach ($test in $slowestTests) {
        $warn = $test.DurationSeconds -gt 5 -and $test.Category -ne 'Slow'
        $color = if ($warn) { "Yellow" } else { "White" }
        $warnMarker = if ($warn) { " [!]" } else { "" }

        Write-Host "  $($test.TestName): $($test.DurationSeconds)s (Category: $($test.Category))$warnMarker" -ForegroundColor $color

        if ($warn) {
            $testWarnings += "$($test.TestName) is $($test.DurationSeconds)s without Slow category"
        }
    }

    # Issue warnings
    if ($projectWarnings.Count -gt 0 -or $testWarnings.Count -gt 0) {
        Write-Host "`n" -ForegroundColor Yellow
        foreach ($warning in $projectWarnings) {
            Write-Warning $warning
        }
        foreach ($warning in $testWarnings) {
            Write-Warning $warning
        }
    }
}

function Invoke-Gate([string]$Name, [scriptblock]$Action) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    if ($null -ne $TestCommandInvoker) {
        $exitCode = & $TestCommandInvoker $Name
        if ($exitCode -ne 0) { throw "Gate failed: $Name (exit $exitCode)" }
        return
    }

    $global:LASTEXITCODE = 0
    & $Action
    $actionSucceeded = $?
    $exitCode = $global:LASTEXITCODE
    if (-not $actionSucceeded -or $exitCode -ne 0) {
        if ($exitCode -isnot [int] -or $exitCode -eq 0) { $exitCode = 1 }
        throw "Gate failed: $Name (exit $exitCode)"
    }
}

Invoke-Gate 'Build solution' { dotnet build $solution -c $Configuration --nologo }
$testProjects = @(
    'PhotoReview.Architecture.Tests',
    'PhotoReview.Core.Tests',
    'PhotoReview.Imaging.Tests',
    'PhotoReview.Integration.Tests',
    'PhotoReview.App.Tests'
)
foreach ($testProject in $testProjects) {
    Invoke-Gate "Run xUnit: $testProject" {
        $testArgs = @(
            (Join-Path $root "tests\$testProject\$testProject.csproj"),
            '-c', $Configuration,
            '--no-build',
            '--nologo',
            '--filter', $filter,
            '--blame-hang',
            '--blame-hang-timeout', '120s',
            '--blame-hang-dump-type', 'none'
        )
        if ($TestReport) {
            $testArgs += @('--logger', "trx;LogFileName=$testProject.trx", '--results-directory', (Join-Path $root "TestResults\$testProject"))
        }
        dotnet test @testArgs
    }
}

# Q-R3: same guarantee as CI's "Run Integration-category tests that the main filter skips" step, so
# Integration-trait tests whose class is also Slow are never silently skipped by the default gate.
# Only Integration+Slow is run here (the rest already ran above), in every test project (R2-A-12).
if (-not $All -and -not $Slow) {
    foreach ($testProject in $testProjects) {
        Invoke-Gate "Run xUnit (Category=Integration&Slow): $testProject" {
            dotnet test (Join-Path $root "tests\$testProject\$testProject.csproj") -c $Configuration --no-build --nologo `
                --filter 'Category=Integration&Category=Slow&Category!=Manual&Category!=Native' `
                --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none
        }
    }
}

if ($TestReport) {
    Generate-TestReport -TestProjects $testProjects -RootPath $root
}

# DT09: Documentation budget check (warning level; error after stabilization)
Invoke-Gate 'Check documentation budget (T0 <= 16 KB total, T1 <= 24 KB per file)' {
    & (Join-Path $PSScriptRoot 'docs-budget.ps1') -Check | Out-Null
}

# AR07 §3: doc link check (non-archive markdown must have no broken links)
Invoke-Gate 'Check documentation links' {
    & (Join-Path $PSScriptRoot 'check-doc-links.ps1')
}

# R2-F-15: the former smoke-test.ps1 / fault-injection-test.ps1 gates only exercised .NET file primitives (no
# PhotoReview code could make them fail) and put an item in the real Recycle Bin on every run, so they were removed.
# File-action safety (move/copy/recycle, destination conflict, IO failure, journal states, recovery) is covered by
# FileActionServiceTests / UndoServiceTests / journal tests in the test step above.
Invoke-Gate 'Publish framework-dependent release' {
    Publish-ReleaseDirectory -Directory $ReleaseDirectory -SelfContained $false
}
Invoke-Gate 'Verify framework-dependent release' {
    & (Join-Path $PSScriptRoot 'verify-release.ps1') -ReleaseDirectory $ReleaseDirectory
}

if ($RequireSelfContained) {
    $selfContained = Join-Path $root 'outputs\release\PhotoReview-self-contained'
    Invoke-Gate 'Publish self-contained release' {
        Publish-ReleaseDirectory -Directory $selfContained -SelfContained $true
    }
    Invoke-Gate 'Verify self-contained release' {
        & (Join-Path $PSScriptRoot 'verify-release.ps1') -ReleaseDirectory $selfContained -SelfContained
    }
}

Write-Host "`nPASS: all PhotoReview verification gates" -ForegroundColor Green
