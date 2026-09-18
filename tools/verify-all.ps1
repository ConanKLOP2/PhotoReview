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
$appProject = Join-Path $root 'src\PhotoReview.App\PhotoReview.App.csproj'
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
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Gate failed: $Name (exit $LASTEXITCODE)" }
}

Invoke-Gate 'Build solution' { dotnet build $solution -c $Configuration --nologo }
Invoke-Gate 'Run persistence, journal, keyboard and association contracts' {
    dotnet run --project (Join-Path $root 'tests\PhotoReview.Tests\PhotoReview.Tests.csproj') -c $Configuration --no-build --nologo
}
Invoke-Gate 'Run xUnit test suite' {
    dotnet test (Join-Path $root 'tests\PhotoReview.Architecture.Tests\PhotoReview.Architecture.Tests.csproj') -c $Configuration --no-build --nologo
    dotnet test (Join-Path $root 'tests\PhotoReview.Core.Tests\PhotoReview.Core.Tests.csproj') -c $Configuration --no-build --nologo
    dotnet test (Join-Path $root 'tests\PhotoReview.Imaging.Tests\PhotoReview.Imaging.Tests.csproj') -c $Configuration --no-build --nologo
    dotnet test (Join-Path $root 'tests\PhotoReview.Integration.Tests\PhotoReview.Integration.Tests.csproj') -c $Configuration --no-build --nologo
    dotnet test (Join-Path $root 'tests\PhotoReview.Tests.Unit\PhotoReview.Tests.Unit.csproj') -c $Configuration --no-build --nologo
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
