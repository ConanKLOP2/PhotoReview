# Fixture-integrity helpers for run-matrix.ps1 (dot-sourced). Kept in their own file so tests can dot-source them
# without running the matrix driver.

function Get-FixtureStat([string]$path) {
    # Count + total bytes (cheap summary) plus Fingerprint: SHA-256 over the sorted listing of
    # relative-path|length|last-write-UTC-ticks. A same-size replacement (different content, same length) changes the
    # last-write ticks and/or name, so the fingerprint catches what Count + Bytes alone cannot.
    $root = (Get-Item -LiteralPath $path).FullName.TrimEnd('\')
    $files = @(Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue)
    $bytes = if ($files.Count -eq 0) { 0 } else { ($files | Measure-Object -Property Length -Sum).Sum }
    $lines = @($files | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\').ToUpperInvariant()
        '{0}|{1}|{2}' -f $rel, $_.Length, $_.LastWriteTimeUtc.Ticks
    } | Sort-Object -CaseSensitive)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))).Replace('-', '')
    }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Count = $files.Count; Bytes = [int64]$bytes; Fingerprint = $hash }
}

function Test-FixtureStatEqual($Baseline, $Current) {
    return ($Current.Count -eq $Baseline.Count -and $Current.Bytes -eq $Baseline.Bytes -and $Current.Fingerprint -ceq $Baseline.Fingerprint)
}
