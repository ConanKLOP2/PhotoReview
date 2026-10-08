# Per-run folder + frame cleanup for fullscreen-capture.ps1 (dot-sourced). Own file so tests can exercise the cleanup
# without a monitor or the app (same pattern as Tune-Splits.ps1 / Fixture-Alias.ps1).

function New-CaptureRunDir([string]$Out) {
    # Unique per-run subfolder of the user-given -Out; everything the rig writes goes here and only this folder is cleaned.
    $dir = Join-Path $Out ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    $dir
}

function Remove-CaptureFrames([string]$RunDir, [string]$Out) {
    # Deletes only the *.png captured into $RunDir. Refuses anything that is not a direct 'run-*' child of $Out, so a
    # wrong argument can never reach the user's own files. Safe to call when the folder is missing; removes the run
    # folder itself only if nothing else (e.g. the config backup) is left in it.
    if ([string]::IsNullOrWhiteSpace($RunDir) -or [string]::IsNullOrWhiteSpace($Out)) { return }
    $parent = [IO.Path]::GetFullPath($Out).TrimEnd('\', '/')
    $run = [IO.Path]::GetFullPath($RunDir).TrimEnd('\', '/')
    if (-not ([IO.Path]::GetDirectoryName($run) -ieq $parent) -or -not ([IO.Path]::GetFileName($run)).StartsWith('run-', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean '$RunDir': not a run-* subfolder of '$Out'"
    }
    if (-not (Test-Path -LiteralPath $run -PathType Container)) { return }
    Get-ChildItem -LiteralPath $run -Filter '*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem -LiteralPath $run -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $run -Force -ErrorAction SilentlyContinue }
}
