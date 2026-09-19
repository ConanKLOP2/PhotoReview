[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$verifyScript = Join-Path $PSScriptRoot 'verify-all.ps1'
$expectedGates = @(
    'Build solution',
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

# Exercise the real Invoke-Gate action path with a native process. This guards
# against PowerShell reporting `$? = $true` for a completed scriptblock even
# though the native command inside it returned a non-zero exit code.
$verifySource = Get-Content -LiteralPath $verifyScript -Raw
$buildGate = "Invoke-Gate 'Build solution' { dotnet build `$solution -c `$Configuration --nologo }"
$nativeFailureGate = "Invoke-Gate 'Build solution' { cmd /c exit 17 }"
if (-not $verifySource.Contains($buildGate)) { throw 'Could not locate the build gate for native failure injection.' }
$nativeFailureScript = Join-Path $PSScriptRoot ("verify-all-native-failure-{0}.ps1" -f [Guid]::NewGuid().ToString('N'))
try {
    Set-Content -LiteralPath $nativeFailureScript -Value $verifySource.Replace($buildGate, $nativeFailureGate) -Encoding utf8
    $nativeFailureDetected = $false
    try { & $nativeFailureScript }
    catch {
        $nativeFailureDetected = $_.Exception.Message -like '*Gate failed: Build solution (exit 17)*'
        if (-not $nativeFailureDetected) { throw }
    }
    if (-not $nativeFailureDetected) { throw 'A real native exit code 17 was not detected by Invoke-Gate.' }
}
finally {
    Remove-Item -LiteralPath $nativeFailureScript -Force -ErrorAction SilentlyContinue
}

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
