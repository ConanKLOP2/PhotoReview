[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RequireSelfContained,
    [string]$ReleaseDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'PhotoReview.slnx'
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $root 'outputs\release\PhotoReview-framework-dependent'
}

function Invoke-Gate([string]$Name, [scriptblock]$Action) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Gate failed: $Name (exit $LASTEXITCODE)" }
}

Invoke-Gate 'Build solution' { dotnet build $solution -c $Configuration --nologo }
Invoke-Gate 'Run persistence, journal, keyboard and association contracts' {
    dotnet run --project (Join-Path $root 'PhotoReview.Tests\PhotoReview.Tests.csproj') -c $Configuration --no-build --nologo
}
Invoke-Gate 'Run xUnit test suite' {
    dotnet test (Join-Path $root 'PhotoReview.Tests.Unit\PhotoReview.Tests.Unit.csproj') -c $Configuration --no-build --nologo
}
Invoke-Gate 'Run file-operation smoke test' {
    & (Join-Path $PSScriptRoot 'smoke-test.ps1')
}
Invoke-Gate 'Run fault-injection safety test' {
    & (Join-Path $PSScriptRoot 'fault-injection-test.ps1')
}
Invoke-Gate 'Verify framework-dependent release' {
    & (Join-Path $PSScriptRoot 'verify-release.ps1') -ReleaseDirectory $ReleaseDirectory
}

if ($RequireSelfContained) {
    $selfContained = Join-Path $root 'outputs\release\PhotoReview-self-contained'
    Invoke-Gate 'Verify self-contained release' {
        & (Join-Path $PSScriptRoot 'verify-release.ps1') -ReleaseDirectory $selfContained -SelfContained
    }
}

Write-Host "`nPASS: all PhotoReview verification gates" -ForegroundColor Green
