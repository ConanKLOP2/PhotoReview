<#
.SYNOPSIS
  Run this as the LAST step of an agent task, before the final report (docs/AGENT-HYGIENE.md).
.DESCRIPTION
  Finds what the agent left behind: processes started from its worktree (a leftover background process is what makes a
  finished agent look "still running" for hours in the UI), uncommitted work, unpushed commits, and stray files in the main
  checkout. It only ever acts on processes whose path or command line points into the given worktree - never a blanket
  "kill every testhost/dotnet", which would break other agents' runs.
  Exit code 0 = clean, 1 = something left behind (printed).
.PARAMETER Worktree
  The agent's worktree folder (default: the current directory).
.PARAMETER Stop
  Stop the leftover processes found (only those under the worktree).
#>
[CmdletBinding()]
param(
    [string]$Worktree = (Get-Location).Path,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$issues = [System.Collections.Generic.List[string]]::new()
$root = (Resolve-Path -LiteralPath $Worktree).Path.TrimEnd('\')

# 1. Processes whose executable or command line is inside this worktree (build/test hosts, the app, helper scripts).
$self = $PID
$names = 'dotnet', 'testhost', 'MSBuild', 'VBCSCompiler', 'PhotoReview', 'PhotoReview.App', 'powershell', 'pwsh', 'cmd', 'conhost', 'bash'
$left = @()
foreach ($p in Get-CimInstance Win32_Process) {
    if ($p.ProcessId -eq $self) { continue }
    $short = [System.IO.Path]::GetFileNameWithoutExtension([string]$p.Name)
    if ($names -notcontains $short) { continue }
    $text = "$($p.ExecutablePath) $($p.CommandLine)"
    if ($text.IndexOf($root, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $left += $p }
}
foreach ($p in $left) {
    $cmd = [string]$p.CommandLine
    if ($cmd.Length -gt 140) { $cmd = $cmd.Substring(0, 140) }
    $issues.Add("process $($p.ProcessId) $($p.Name) from this worktree: $cmd")
    if ($Stop) {
        try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; Write-Host "stopped $($p.ProcessId)" }
        catch { Write-Host "could not stop $($p.ProcessId): $($_.Exception.Message)" }
    }
}

# 2. Uncommitted work and unpushed commits in the worktree.
$status = git -C $root status --short 2>$null
if ($status) { $issues.Add("uncommitted changes in worktree:`n" + (($status | Select-Object -First 15) -join "`n")) }
$upstream = git -C $root rev-parse --abbrev-ref '@{u}' 2>$null
if ($LASTEXITCODE -eq 0 -and $upstream) {
    $ahead = git -C $root rev-list --count "$upstream..HEAD" 2>$null
    if ($ahead -and [int]$ahead -gt 0) { $issues.Add("$ahead commit(s) not pushed to $upstream") }
}
else {
    $issues.Add('branch has no upstream: push it (git push -u origin HEAD) or the work only exists on this machine')
}

# 3. Stray files in the main checkout (a relative path resolved against the wrong cwd creates them).
$common = (git -C $root rev-parse --git-common-dir 2>$null)
if ($common) {
    $commonPath = if ([System.IO.Path]::IsPathRooted($common)) { $common } else { Join-Path $root $common }
    $main = Split-Path -Parent (Resolve-Path -LiteralPath $commonPath).Path
    if ($main -and ($main -ne $root)) {
        $zero = git -C $main ls-files --others --exclude-standard 2>$null | ForEach-Object {
            $f = Join-Path $main $_
            if ((Test-Path -LiteralPath $f -PathType Leaf) -and ((Get-Item -LiteralPath $f).Length -eq 0)) { $_ }
        }
        if ($zero) { $issues.Add("zero-byte untracked file(s) in the main checkout (created by a wrong relative path?): $($zero -join ', ')") }
    }
}

if ($issues.Count -eq 0) {
    Write-Host '[PASS] nothing left behind'
    exit 0
}
Write-Host "[FAIL] $($issues.Count) thing(s) left behind:"
$issues | ForEach-Object { Write-Host " - $_" }
exit 1
