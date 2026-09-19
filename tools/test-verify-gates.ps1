[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$verifyScript = Join-Path $PSScriptRoot 'verify-all.ps1'
$expectedGates = @(
    'Build solution',
    'Run persistence, journal, keyboard and association contracts',
    'Run xUnit: PhotoReview.Architecture.Tests',
    'Run xUnit: PhotoReview.Core.Tests',
    'Run xUnit: PhotoReview.Imaging.Tests',
    'Run xUnit: PhotoReview.Integration.Tests',
    'Run xUnit: PhotoReview.App.Tests',
    'Run xUnit: PhotoReview.Tests.Unit',
    'Run file-operation smoke test',
    'Run fault-injection safety test',
    'Publish framework-dependent release',
    'Verify framework-dependent release'
)

function Invoke-FaultCase([string]$FailingGate) {
    $seen = [System.Collections.Generic.List[string]]::new()
    $invoker = {
        param([string]$Name)
        $seen.Add($Name)
        if ($Name -eq $FailingGate) { return 17 }
        return 0
    }.GetNewClosure()

    $failed = $false
    try {
        & $verifyScript -TestCommandInvoker $invoker
    }
    catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*Gate failed: $FailingGate*") { throw }
    }

    if (-not $failed) { throw "Fault injection was not detected for: $FailingGate" }
    if ($seen[$seen.Count - 1] -ne $FailingGate) { throw "Gate execution continued after failure: $FailingGate" }
}

foreach ($gate in $expectedGates) { Invoke-FaultCase $gate }

$seenAll = [System.Collections.Generic.List[string]]::new()
$passInvoker = {
    param([string]$Name)
    $seenAll.Add($Name)
    return 0
}.GetNewClosure()
& $verifyScript -TestCommandInvoker $passInvoker
if ($seenAll.Count -ne $expectedGates.Count) {
    throw "Expected $($expectedGates.Count) gates, observed $($seenAll.Count)."
}
for ($i = 0; $i -lt $expectedGates.Count; $i++) {
    if ($seenAll[$i] -ne $expectedGates[$i]) {
        throw "Gate order mismatch at ${i}: expected '$($expectedGates[$i])', got '$($seenAll[$i])'."
    }
}

Write-Host "PASS: verify-all catches failures in every gate and runs all gates on success." -ForegroundColor Green
