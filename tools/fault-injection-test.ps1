[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('PhotoReview-Fault-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $source = Join-Path $root 'source.jpg'; $destination = Join-Path $root 'target\source.jpg'
    [IO.File]::WriteAllBytes($source, [byte[]](1..32)); New-Item -ItemType Directory (Split-Path $destination) -Force | Out-Null
    $original = [IO.File]::ReadAllBytes($source)

    # Conflict injection: never overwrite an existing destination.
    [IO.File]::WriteAllBytes($destination, [byte[]](9..12))
    if (-not (Test-Path $source) -or -not ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($destination), [byte[]](9..12)))) { throw 'Conflict protection failed.' }
    Write-Host 'PASS: destination conflict preserves both files'

    # Interrupted move simulation: source remains authoritative until commit.
    Remove-Item $destination -Force
    $journal = Join-Path $root 'operations.jsonl'
    @{ Id = 'fault-1'; Kind = 'Move'; State = 'Prepared'; Source = $source; Destination = $destination } | ConvertTo-Json -Compress | Set-Content $journal
    if (-not (Test-Path $source) -or (Test-Path $destination)) { throw 'Prepared state is unsafe.' }
    Write-Host 'PASS: prepared operation leaves source recoverable'

    # Byte preservation after a completed same-volume move.
    [IO.File]::Move($source, $destination)
    if (Test-Path $source) { throw 'Completed move left source.' }
    if (-not ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($destination), $original))) { throw 'Move changed bytes.' }
    Write-Host 'PASS: completed move preserves bytes'
}
finally { if (Test-Path $root) { Remove-Item $root -Recurse -Force } }
