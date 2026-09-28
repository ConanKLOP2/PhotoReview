#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Regenerates the "Decided" table in docs/refactoring/OPEN-DECISIONS.md from the frontmatter
  of docs/refactoring/decisions/*.md, so a routine PR never has to hand-edit the shared table
  (the shared insertion point, not the content, was the source of repeated merge conflicts on
  2026-09-27 -- see AGENTS.md > "Avoid append-conflicts").

.DESCRIPTION
  Every file under docs/refactoring/decisions/ that owns a row in the Decided table carries a
  small YAML-ish frontmatter block at its very top:

      ---
      id: P02
      order: 14
      summary: |-
        Recovery retry re-checks the journal's latest entry and live marker so a concurrent
        retry in another window can no longer journal a completed Move as Failed.
      ---

  - id: the short decision ID(s) exactly as written in the table's first column (e.g. `P02`,
    `R01/R02/R03/R13`, `Q-R05 / Q-R12`).
  - order: an explicit integer used only to sort the generated rows. An earlier version of this
    script tried to avoid this field by sorting on each file's first-commit date
    (`git log --format=%aI -- <file> | tail -1`), on the theory that git history is a good-enough
    proxy for "the order decisions were added". That was measured against the real repo history
    during the 2026-09-27 migration and rejected: several decisions files share the exact same
    first-commit timestamp (squashed/batched commits), and at least one pair (Q-R05-Q-R12.md vs
    R15-R16-R17-R18.md vs LEDGER-DEEP-REVIEW.md) has first-commit dates that are NOT monotonic
    with the table's actual row order (git log put Q-R05/Q-R12 before R15-18 before the ledger
    review, but the table -- and the narrative dependency between those decisions -- has them in
    the opposite order). Rebases/cherry-picks/squash-merges make this worse over time, and a
    silently-wrong sort would corrupt the table without the generator ever failing. An explicit,
    hand-set `order:` field is a few extra bytes per file but is exact, and makes the generator's
    output fully deterministic from the files' content alone (no git history dependency at all,
    which also keeps this script usable in a shallow checkout).
  - summary: the exact one-line prose from the table's second column. PROSE ONLY -- no leading
    `|`/table syntax and no `[Detail](...)` link; the link is always derived from the file's own
    path (`decisions/<filename>`) and appended by this script, never hand-listed.

  A handful of the OLDEST rows in the table (Q-Z1 .. F-WIN-2) predate this convention and were
  decided/implemented before any per-decision file existed under decisions/ (their rationale, per
  AGENTS.md's documentation-tiering rule, lives only in ADRs / HISTORY.md / older archived
  snapshots, not in a decisions/*.md file of their own). Rather than inventing files for them,
  they are kept verbatim as a small hardcoded exception list below ($StaticLeadingRows) -- exactly
  the "leave that one row as a manual hardcoded line" case the migration plan called for.

  This script rewrites ONLY the region of OPEN-DECISIONS.md between the BEGIN/END marker comments.
  Everything else in the file (header prose, the Open table, the "Older groups" summary line) is
  left untouched. Running the script twice with no source changes produces byte-identical output
  (idempotent) -- there is no timestamp, random ordering, or other non-deterministic input.

.NOTES
  Used by tools/check-open-decisions.ps1 (CI gate) and referenced from AGENTS.md /
  docs/refactoring/OPEN-DECISIONS.md's own header prose.
#>
[CmdletBinding()]
param(
    # Overrides which file gets (re)written. Used by tools/check-open-decisions.ps1 to regenerate
    # into a scratch copy without touching the real docs/refactoring/OPEN-DECISIONS.md, so the CI
    # check is a pure read + diff, never a silent write to the working tree.
    [string]$TargetPath = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$decisionsDir = Join-Path $root 'docs\refactoring\decisions'
$openDecisionsPath = if ($TargetPath) { $TargetPath } else { Join-Path $root 'docs\refactoring\OPEN-DECISIONS.md' }

$BeginMarker = '<!-- BEGIN GENERATED DECIDED TABLE (tools/generate-open-decisions.ps1 -- do not hand-edit below) -->'
$EndMarker = '<!-- END GENERATED DECIDED TABLE -->'

# Rows decided/implemented before the decisions/*.md-per-row convention existed. Their detail lives
# in ADRs / HISTORY.md / git history (see AGENTS.md's documentation-tiering rule), not in a file
# under decisions/, so there is nothing to migrate them to -- they stay as a literal, hand-maintained
# exception list, verbatim (character-for-character) from the table as it existed before this script.
# A NEW row must never be added here: every new decision gets its own decisions/<ID>.md file with
# frontmatter instead (see AGENTS.md > "Avoid append-conflicts").
$StaticLeadingRows = @(
    [pscustomobject]@{ Id = 'Q-Z1'; Summary = 'Zoom 100 % = 1 source pixel, original decoded on demand (#43, #47, [ADR 0008](../adr/0008-zoom-source-pixel.md)).' }
    [pscustomobject]@{ Id = 'Q-R1..R6'; Summary = '(a) for all (2026-09-24, #76): never persist previews with alpha; relative action destinations stay inside the photo folder; Integration tests run in CI, `Stress` dropped; scripts/example config in `deploy/`; wait up to 2 s for the session write at shutdown; accessibility scope = user-facing windows.' }
    [pscustomobject]@{ Id = 'Q-R7'; Summary = 'Opaque PNG/WebP previews may be disk-cached (alpha scanned on the downscaled bitmap, #77).' }
    [pscustomobject]@{ Id = 'Q-R8'; Summary = 'Recycle on removable/network/UNC drives refused by default; opt-in "allow permanent delete" (default off), #79.' }
    [pscustomobject]@{ Id = 'Q-R9'; Summary = 'DECLINED: no Recycle Bin orphan sweep (would delete from the real bin). Tests only clean their own items.' }
    [pscustomobject]@{ Id = 'Q-R10'; Summary = 'Opening a photo from Explorer while the app runs is forwarded to the running instance over a named pipe (#78); Q-R12: unknown outcome exits without the "already open" dialog.' }
    [pscustomobject]@{ Id = 'Q-R11'; Summary = 'Leave as is: session resume discarded when Explorer order applies; benchmark profiles run with disk cache off; dead `MemoryReserveBytes`; per-solution version computation.' }
    [pscustomobject]@{ Id = 'Q-R13..R16'; Summary = 'Single-step undo kept; duplicate cleanup on drives without a Recycle Bin stays refused (one up-front message); group B/C fixes all done.' }
    [pscustomobject]@{ Id = 'Q-R17'; Summary = 'Whole-folder preload estimate: box bound `entries x w x h x 4` until 8 previews are measured, then measured mean x 1.25 (capped at the bound); Q-R26 fixed the resulting UI-thread cost (`preloadKick`).' }
    [pscustomobject]@{ Id = 'Q-R18'; Summary = 'Setting `InstanceMode`: SingleWindow (default) or PerFolder.' }
    [pscustomobject]@{ Id = 'Q-R19..R22'; Summary = 'Defaults reviewed by the user: RAM cache 50 % (applies after restart, Q-AR6), keys End/1/I/M/Y, Move/Copy-to asks each time; `ExifInfoFields` default excludes FileName/Dimensions, `ShowFolderInfo` and `ClickToZoomEnabled` default off; `DecoderBackend=WicDirect`, `ShowExifInfo=false`, Defaults button reads `new AppSettings()`; default action names from the catalog, folders `Group-2/3/4`.' }
    [pscustomobject]@{ Id = 'Q-R23'; Summary = 'Updates: manual "Check for updates" button only (GitHub Releases API); no auto-check/download.' }
    [pscustomobject]@{ Id = 'Q-R24'; Summary = 'CI tags `v2.0.N` after each merge; releases are published manually (Actions > Release > Run workflow, tag input), no automatic draft.' }
    [pscustomobject]@{ Id = 'Q-R25'; Summary = 'Overnight review merged (#96, #99, #103); follow-ups Q-R26/Q-R27.' }
    [pscustomobject]@{ Id = 'Q-AR6..AR10'; Summary = 'RAM % keeps restart (AR10 closed); readability probe runs in the background after the first frame (c, AR16); TurboJpeg labelled experimental; benchmark window kept; AR14 comment only, AR19 `ShowSkippedFiles` via `IDialogService`.' }
    [pscustomobject]@{ Id = 'Q-R26'; Summary = 'Keep Q-R17 whole-folder preload; cause of the higher first-visual was the per-navigation cache lookup on the UI thread, fixed (`PreloadKickOffCallerTests`).' }
    [pscustomobject]@{ Id = 'Q-R27'; Summary = 'Each running operation holds a named kernel event (`WindowsLiveOperationRegistry`) from Prepared to outcome; startup reconcile skips live operations (#116).' }
    [pscustomobject]@{ Id = 'Q-R28'; Summary = 'Settings > Export strings writes every key (untranslated first); Reload translations reports file/entry problems (#120).' }
    [pscustomobject]@{ Id = 'Q-R30'; Summary = 'UI feedback: dark title bar + scrollbars, toolbar auto-hide, Open folder/Settings in the context menu, title-bar fields setting, optional Modified-date EXIF field, info font size, click-zoom key `2`, glide smoothing `Predict`; Shift+arrow panning declined (#119, #123).' }
    [pscustomobject]@{ Id = 'Q-R31'; Summary = 'Preload window configurable (Settings > Performance, defaults 32 ahead / 8 behind, 1-500 / 0-500, applies after restart), #125.' }
    [pscustomobject]@{ Id = 'Q-R32'; Summary = 'Arrow keys on a zoomed image only pan, never navigate; setting `ArrowKeyNavigatesAtZoomEdge` (default off) restores edge navigation (#126). Pan step = `ArrowPanStepPercent` (default 10 %, 1-100, #133).' }
    [pscustomobject]@{ Id = 'Q-R33'; Summary = 'Sort modes `Default` (file-system order, no sort), `NameAscending`/`NameDescending` (app natural order) ignore Explorer order; `Name` follows Explorer. Default for new installs changed from `Name` to `Default` (2026-09-27, user request, PR #186).' }
    [pscustomobject]@{ Id = 'Q-R34'; Summary = '`ToolbarAutoHide` default off (saved true kept); new `InfoOverlayAutoHide` (default off, own delay 3000 ms) fades info on the photo, never while a message/progress/compare/dialog needs it (#129).' }
    [pscustomobject]@{ Id = 'F-WIN-2'; Summary = '(A) 2026-09-27: on a fixed drive whose Recycle Bin is off, too small for the file, or unreadable, Recycle is refused before journaling (no silent permanent delete; "allow permanent delete" does not apply), [ADR 0007](../adr/0007-io-durability-contract.md) amendment; branch `fix/recyclebin-quota-guard`.' }
)

function Get-DecisionFrontmatter {
    param([string]$Path)

    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    # Normalize line endings so the block-scalar parsing below doesn't have to care about \r.
    $text = $text -replace "`r`n", "`n"

    if ($text -notmatch '(?s)^---\n(.*?)\n---\n') {
        throw "No frontmatter block found at the top of $Path"
    }
    $fm = $Matches[1]
    $lines = $fm -split "`n"

    $id = $null
    $order = $null
    $summaryLines = @()
    $i = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        if ($line -match '^id:\s*(.*)$') {
            $id = $Matches[1].Trim()
            $i++
        }
        elseif ($line -match '^order:\s*(\d+)\s*$') {
            $order = [int]$Matches[1]
            $i++
        }
        elseif ($line -match '^summary:\s*\|-?\s*$') {
            $i++
            while ($i -lt $lines.Count -and ($lines[$i] -match '^  (.*)$' -or $lines[$i] -eq '')) {
                if ($lines[$i] -eq '') { $summaryLines += ''; $i++; continue }
                $summaryLines += $Matches[1]
                $i++
            }
        }
        else {
            $i++
        }
    }

    if (-not $id) { throw "Frontmatter in $Path is missing 'id'" }
    if ($null -eq $order) { throw "Frontmatter in $Path is missing 'order'" }
    if ($summaryLines.Count -eq 0) { throw "Frontmatter in $Path is missing 'summary'" }

    # A block literal scalar (|-) joins its lines with '\n'; our summaries are always a single
    # logical line today, but join defensively in case a future one wraps.
    $summary = ($summaryLines -join "`n").Trim()

    return [pscustomobject]@{
        Id      = $id
        Order   = $order
        Summary = $summary
        File    = (Split-Path -Leaf $Path)
    }
}

$decisionFiles = @(Get-ChildItem -LiteralPath $decisionsDir -Filter '*.md' | Sort-Object Name)
$decisions = @($decisionFiles | ForEach-Object { Get-DecisionFrontmatter -Path $_.FullName })

# Fail loudly on a duplicate order or id -- a silent collision would silently reorder/hide a row.
$dupOrder = $decisions | Group-Object Order | Where-Object { $_.Count -gt 1 }
if ($dupOrder) {
    throw "Duplicate 'order' value(s) in docs/refactoring/decisions frontmatter: $(($dupOrder | ForEach-Object { $_.Name }) -join ', ')"
}
$dupId = $decisions | Group-Object Id | Where-Object { $_.Count -gt 1 }
if ($dupId) {
    throw "Duplicate 'id' value(s) in docs/refactoring/decisions frontmatter: $(($dupId | ForEach-Object { $_.Name }) -join ', ')"
}

$sortedDecisions = @($decisions | Sort-Object Order)

$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('| ID | Decision |')
$rows.Add('|---|---|')
foreach ($row in $StaticLeadingRows) {
    $rows.Add("| $($row.Id) | $($row.Summary) |")
}
foreach ($d in $sortedDecisions) {
    $rows.Add("| $($d.Id) | $($d.Summary) [Detail](decisions/$($d.File)) |")
}

$generatedBlock = ($rows -join "`n")

$original = [System.IO.File]::ReadAllText($openDecisionsPath, [System.Text.Encoding]::UTF8)
$originalNoCr = $original -replace "`r`n", "`n"

$pattern = [regex]::Escape($BeginMarker) + '(?s).*?' + [regex]::Escape($EndMarker)
if ($originalNoCr -notmatch $pattern) {
    throw "Could not find the BEGIN/END generated-table markers in $openDecisionsPath. Expected:`n$BeginMarker`n...`n$EndMarker"
}

$replacement = "$BeginMarker`n$generatedBlock`n$EndMarker"
$updated = [regex]::Replace($originalNoCr, $pattern, { param($m) $replacement }, 'Singleline')

if ($updated -ne $originalNoCr) {
    # Preserve the file's original line-ending style (this repo's docs are CRLF).
    if ($original -match "`r`n") {
        $updated = $updated -replace "`n", "`r`n"
    }
    [System.IO.File]::WriteAllText($openDecisionsPath, $updated, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[UPDATED] $openDecisionsPath ($($sortedDecisions.Count) generated rows + $($StaticLeadingRows.Count) static rows)" -ForegroundColor Green
}
else {
    Write-Host "[OK] $openDecisionsPath already up to date ($($sortedDecisions.Count) generated rows + $($StaticLeadingRows.Count) static rows)" -ForegroundColor Green
}
