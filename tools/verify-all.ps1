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
    [Parameter(DontShow)]
    [scriptblock]$TestCommandInvoker
)

# Usage examples:
# ./verify-all.ps1                    # Default: HotPath only (~2-3 min)
# ./verify-all.ps1 -Stress             # Include race-condition tests
# ./verify-all.ps1 -Slow               # Include 10+ second tests
# ./verify-all.ps1 -Stress -Slow       # Extended local run
# ./verify-all.ps1 -All                # Everything (CI+local exhaustive)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'PhotoReview.slnx'
$appProject = Join-Path $root 'src\PhotoReview.App\PhotoReview.App.csproj'

# Build filter string dynamically
$filter = "Category!=Manual"  # Always exclude Manual
if (-not $All) {
    if (-not $Stress) { $filter += " AND Category!=Stress" }
    if (-not $Native) { $filter += " AND Category!=Native" }
    if (-not $Slow) { $filter += " AND Category!=Slow" }
    if (-not $Integration) { $filter += " AND Category!=Integration" }
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
        dotnet test (Join-Path $root "tests\$testProject\$testProject.csproj") -c $Configuration --no-build --nologo --filter "$filter"
    }
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
