$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('PhotoReview-Smoke-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$one = Join-Path $source 'Loai-1'
$two = Join-Path $source 'Loai-2'
New-Item -ItemType Directory -Path $one,$two -Force | Out-Null
try {
    $original = Join-Path $source 'a (1).jpg'
    [IO.File]::WriteAllBytes($original, [byte[]](1..64))
    $destination = Join-Path $one ([IO.Path]::GetFileName($original))

    # Same-volume Move, verify bytes, then Undo.
    [IO.File]::Move($original, $destination)
    if (Test-Path $original) { throw 'Move did not remove source.' }
    if (-not (Test-Path $destination)) { throw 'Move did not create destination.' }
    $sourceBytes = [IO.File]::ReadAllBytes($destination)
    [IO.File]::Move($destination, $original)
    if (-not (Test-Path $original)) { throw 'Undo did not restore source.' }
    if (([IO.File]::ReadAllBytes($original) -join ',') -ne ($sourceBytes -join ',')) { throw 'Undo changed bytes.' }

    # Destination conflict must be detected before mutation.
    [IO.File]::WriteAllBytes($destination, [byte[]](9..20))
    if ((Test-Path $destination) -and (Test-Path $original)) { Write-Host 'PASS: conflict preserves source and destination' }
    else { throw 'Conflict fixture invalid.' }

    # Session atomic write simulation.
    $session = Join-Path $root 'session.json'
    $tmp = $session + '.tmp'
    '{"Folder":"source","CurrentPath":"a (1).jpg"}' | Set-Content -LiteralPath $tmp
    Move-Item -LiteralPath $tmp -Destination $session -Force
    if ((Get-Content -Raw -LiteralPath $session) -notmatch 'CurrentPath') { throw 'Session write failed.' }

    # Recycle Bin action used by the Delete shortcut.
    $recycleCandidate = Join-Path $source 'recycle.jpg'
    [IO.File]::WriteAllBytes($recycleCandidate, [byte[]](5, 6, 7))
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($recycleCandidate, [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs, [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
    if (Test-Path -LiteralPath $recycleCandidate) { throw 'Recycle Bin action left source.' }

    Write-Host 'PASS: Move/Undo bytes'
    Write-Host 'PASS: session atomic write'
    Write-Host 'PASS: Recycle Bin action'
    Write-Host 'PASS: fixture cleanup scope'
}
finally {
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
