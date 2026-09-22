[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RequireSelfContained,
    [string]$ReleaseDirectory = '',
    [switch]$Stress,
    [switch]$Native,
    [switch]$Integration,
    [switch]$Slow,
    [switch]$All,
    [switch]$TestReport,
    [Parameter(DontShow)]
    [scriptblock]$TestCommandInvoker
)

# Usage examples:
# ./verify-all.ps1                    # Default: HotPath only (~2-3 min)
# ./verify-all.ps1 -Stress             # Include race-condition tests
# ./verify-all.ps1 -Slow               # Include 10+ second tests
# ./verify-all.ps1 -Stress -Slow       # Extended local run
# ./verify-all.ps1 -All                # Everything (CI+local exhaustive)
# ./verify-all.ps1 -TestReport         # Print test timing report (requires running tests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'PhotoReview.slnx'
$appProject = Join-Path $root 'src\PhotoReview.App\PhotoReview.App.csproj'

# Build filter string dynamically
# NOTE: This filter is verified to match .github/workflows/ci.yml (TS09 verification)
# xUnit uses & (not AND) to join filter conditions
$filter = "Category!=Manual"  # Always exclude Manual
if (-not $All) {
    if (-not $Stress) { $filter += "&Category!=Stress" }
    if (-not $Native) { $filter += "&Category!=Native" }
    if (-not $Slow) { $filter += "&Category!=Slow" }
    if (-not $Integration) { $filter += "&Category!=Integration" }
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
        $projectPath = Join-Path $root "tests\$testProject"
        $objPath = Join-Path $projectPath "obj\$Configuration"
        if (Test-Path -LiteralPath $objPath) {
            $trxFiles += @(Get-ChildItem -LiteralPath $objPath -Filter "*.trx" -Recurse -ErrorAction SilentlyContinue)
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

        Write-Host "  $project`: ${duration:F2}s [$status]" -ForegroundColor $color

        if ($status -eq "WARN") {
            $projectWarnings += "$project exceeds 60s threshold (${duration:F2}s)"
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
            $testArgs += @('--logger', "trx;LogFileName=$testProject.trx")
        }
        dotnet test @testArgs
    }
}

if ($TestReport) {
    Generate-TestReport -TestProjects $testProjects -RootPath $root
}

Invoke-Gate 'Run file-operation smoke test' {
    & (Join-Path $PSScriptRoot 'smoke-test.ps1')
}
Invoke-Gate 'Run fault-injection safety test' {
    & (Join-Path $PSScriptRoot 'fault-injection-test.ps1')
}
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
