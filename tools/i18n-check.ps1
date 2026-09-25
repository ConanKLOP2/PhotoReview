[CmdletBinding()]
param(
    # Extra folder(s) with translation files to check too, e.g. "$env:LOCALAPPDATA\PhotoReview\Languages".
    [string[]]$Path = @(),
    # Print a machine-readable JSON result instead of the table.
    [switch]$Json
)

# L10 (docs/refactoring/I18N-PLAN.md, ADR 0006): checks the JSON translation catalogs.
# - every file is valid JSON (comments and trailing commas allowed, like LanguageCatalog.TryParse)
# - _meta.code is present and valid; every value is a string
# - no unknown keys (a key English does not have), no broken braces, no placeholder English does not have
# - plural groups are sane (English: every key.one has key.other)
# Exit code 1 on errors. Missing translations (incompleteness) and dropped placeholders are warnings only.
# Works in Windows PowerShell 5.1 and PowerShell 7+. Keep this file ASCII-only.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$shippedDir = Join-Path $repoRoot 'src\PhotoReview.Core\Localization\Languages'
$maxFileBytes = 1024 * 1024   # LanguageCatalog.MaxFileBytes

# Try to load System.Text.Json for strict JSON parsing
$hasStrictJson = $false
try {
    [void][System.Reflection.Assembly]::Load('System.Text.Json')
    $hasStrictJson = $true
}
catch {
    # System.Text.Json not available in this PowerShell/Windows version
}

function Remove-JsonComments([string]$Text) {
    # Drops // and /* */ comments outside strings, then trailing commas before } or ].
    $sb = New-Object System.Text.StringBuilder $Text.Length
    $i = 0
    $n = $Text.Length
    while ($i -lt $n) {
        $c = $Text[$i]
        if ($c -eq '"') {
            $start = $i
            $i++
            while ($i -lt $n -and $Text[$i] -ne '"') {
                if ($Text[$i] -eq '\') { $i++ }
                $i++
            }
            $i++
            [void]$sb.Append($Text, $start, [Math]::Min($i, $n) - $start)
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $Text[$i + 1] -eq '/') {
            while ($i -lt $n -and $Text[$i] -ne "`n") { $i++ }
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $Text[$i + 1] -eq '*') {
            $end = $Text.IndexOf('*/', $i + 2, [StringComparison]::Ordinal)
            $i = if ($end -lt 0) { $n } else { $end + 2 }
            continue
        }
        if ($c -eq ',') {
            $j = $i + 1
            while ($j -lt $n -and [char]::IsWhiteSpace($Text[$j])) { $j++ }
            if ($j -lt $n -and ($Text[$j] -eq '}' -or $Text[$j] -eq ']')) { $i++; continue }
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

function Get-Placeholders([string]$Text) {
    # Mirrors LocTemplate.TryParse: {{ and }} are literal braces, {name} is ASCII letter + letters/digits.
    # Returns $null when the braces are broken.
    $names = New-Object System.Collections.Generic.List[string]
    $i = 0
    while ($i -lt $Text.Length) {
        $c = $Text[$i]
        if ($c -eq '{') {
            if ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '{') { $i += 2; continue }
            $end = $Text.IndexOf('}', $i + 1)
            if ($end -lt 0) { return $null }
            $name = $Text.Substring($i + 1, $end - $i - 1)
            if ($name -cnotmatch '^[A-Za-z][A-Za-z0-9]*$') { return $null }
            $names.Add($name)
            $i = $end + 1
            continue
        }
        if ($c -eq '}') {
            if ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '}') { $i += 2; continue }
            return $null
        }
        $i++
    }
    return , $names.ToArray()
}

function Test-JsonStrict([string]$File) {
    # Strictly parse JSON. Detects invalid syntax like double commas.
    # Returns $null on success; returns error message string on failure.
    $text = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8)

    if ($hasStrictJson) {
        try {
            $parseOpts = New-Object 'System.Text.Json.JsonSerializerOptions'
            $parseOpts.AllowTrailingCommas = $false
            $parseOpts.ReadCommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
            [System.Text.Json.JsonDocument]::Parse($text, $parseOpts) | Out-Null
            return $null
        }
        catch {
            return "Invalid JSON: $($_.Exception.Message)"
        }
    }
    else {
        # Fallback: detect common JSON errors
        # Check for double commas
        if ($text -match ',,') {
            return "Invalid JSON: unexpected token - double comma detected"
        }

        # Try parsing
        try {
            $text | ConvertFrom-Json | Out-Null
            return $null
        }
        catch {
            return "Invalid JSON: $($_.Exception.Message)"
        }
    }
}

function Test-JsonDuplicateKeys([string]$File) {
    # Check for duplicate keys in the root-level JSON object.
    # Returns $null on success; returns error message string on failure.
    $text = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8)

    if ($hasStrictJson) {
        try {
            $parseOpts = New-Object 'System.Text.Json.JsonSerializerOptions'
            $parseOpts.AllowTrailingCommas = $false
            $parseOpts.ReadCommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
            $doc = [System.Text.Json.JsonDocument]::Parse($text, $parseOpts)

            if ($doc.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                return "Root is not a JSON object"
            }

            $seenKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            foreach ($prop in $doc.RootElement.EnumerateObject()) {
                if (-not $seenKeys.Add($prop.Name)) {
                    return "Duplicate key: '$($prop.Name)'"
                }
            }
            return $null
        }
        catch {
            return "JSON validation error: $($_.Exception.Message)"
        }
    }
    else {
        # Fallback: parse with ConvertFrom-Json and check for duplicates using regex
        try {
            $doc = $text | ConvertFrom-Json
            if ($null -eq $doc -or $doc -isnot [System.Management.Automation.PSCustomObject]) {
                return "Root is not a JSON object"
            }

            # Check for duplicate keys by parsing the raw JSON text
            $keyPattern = '"([^"\\]*(?:\\.[^"\\]*)*)"\s*:'
            $matches = [regex]::Matches($text, $keyPattern)
            $seenKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)

            foreach ($match in $matches) {
                $key = $match.Groups[1].Value
                if (-not $seenKeys.Add($key)) {
                    return "Duplicate key: '$key'"
                }
            }
            return $null
        }
        catch {
            return "JSON validation error: $($_.Exception.Message)"
        }
    }
}

function Read-Catalog([string]$File) {
    $result = [ordered]@{
        File = $File; Code = ''; NativeName = ''; Plural = 'one-other'
        Entries = (New-Object System.Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal)); Errors = New-Object System.Collections.Generic.List[string]
        Warnings = New-Object System.Collections.Generic.List[string]
    }
    $name = Split-Path -Leaf $File
    $isNotesFile = $name -like '*.notes.json'

    if ((Get-Item -LiteralPath $File).Length -gt $maxFileBytes) {
        $result.Errors.Add("$name is larger than $maxFileBytes bytes")
        return $result
    }

    # Strict JSON validation (no comments or trailing commas) - applies to all JSON files
    $strictError = Test-JsonStrict $File
    if ($null -ne $strictError) {
        $result.Errors.Add($strictError)
        return $result
    }

    # Check for duplicate keys - applies to all JSON files
    $dupError = Test-JsonDuplicateKeys $File
    if ($null -ne $dupError) {
        $result.Errors.Add($dupError)
        return $result
    }

    # For notes files, we only validate JSON structure; skip catalog-specific checks
    if ($isNotesFile) {
        return $result
    }

    try {
        $text = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8)
        $doc = (Remove-JsonComments $text) | ConvertFrom-Json
    }
    catch {
        $result.Errors.Add("invalid JSON: $($_.Exception.Message)")
        return $result
    }
    if ($null -eq $doc -or $doc -isnot [System.Management.Automation.PSCustomObject]) {
        $result.Errors.Add('root must be a JSON object')
        return $result
    }
    foreach ($p in $doc.PSObject.Properties) {
        if ($p.Name -ceq '_meta') {
            $meta = $p.Value
            if ($meta -isnot [System.Management.Automation.PSCustomObject]) { $result.Errors.Add('_meta must be an object'); continue }
            if ($meta.code -is [string]) { $result.Code = $meta.code.ToLowerInvariant() }
            if ($meta.nativeName -is [string]) { $result.NativeName = $meta.nativeName }
            elseif ($meta.name -is [string]) { $result.NativeName = $meta.name }
            if ($meta.plural -is [string]) {
                if ($meta.plural -eq 'none') { $result.Plural = 'none' }
                elseif ($meta.plural -ne 'one-other') { $result.Warnings.Add("_meta.plural '$($meta.plural)' is not 'one-other' or 'none'; 'one-other' is used") }
            }
            continue
        }
        if ($p.Name.StartsWith('_')) { continue }
        if ($p.Value -isnot [string]) { $result.Errors.Add("'$($p.Name)' is not a string"); continue }
        $result.Entries[$p.Name] = $p.Value
    }
    if ([string]::IsNullOrWhiteSpace($result.Code) -or $result.Code -notmatch '^[a-z][a-z0-9-]{1,15}$') {
        $result.Errors.Add('_meta.code is missing or invalid')
    }
    elseif ($name -notlike "$($result.Code).json") {
        $result.Warnings.Add("file name '$name' does not match _meta.code '$($result.Code)'")
    }
    if ([string]::IsNullOrWhiteSpace($result.NativeName)) { $result.NativeName = $result.Code }
    return $result
}

function New-Row($Catalog, [string]$Source, [int]$Translated, [int]$Total) {
    # Source: 'shipped' (src/.../Languages) or 'extra' (a -Path folder); Folder has the full directory.
    $pct = if ($Total -gt 0) { [Math]::Round(100.0 * $Translated / $Total, 1) } else { 100.0 }
    return [pscustomobject][ordered]@{
        Code         = $Catalog.Code
        NativeName   = $Catalog.NativeName
        Source       = $Source
        Folder       = (Split-Path -Parent $Catalog.File)
        File         = (Split-Path -Leaf $Catalog.File)
        Translated   = $Translated
        Total        = $Total
        Completeness = $pct
        Errors       = @($Catalog.Errors)
        Warnings     = @($Catalog.Warnings)
    }
}

# --- English (source language) ---
$englishFile = Join-Path $shippedDir 'en.json'
if (-not (Test-Path -LiteralPath $englishFile -PathType Leaf)) { Write-Error "Missing $englishFile"; exit 1 }
$en = Read-Catalog $englishFile
$enPlaceholders = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
foreach ($key in $en.Entries.Keys) {
    $ph = Get-Placeholders $en.Entries[$key]
    if ($null -eq $ph) { $en.Errors.Add("'$key' has unbalanced braces or an invalid placeholder") }
    else { $enPlaceholders[$key] = $ph }
    if ($key.EndsWith('.one') -and -not $en.Entries.Contains($key.Substring(0, $key.Length - 4) + '.other')) {
        $en.Errors.Add("plural group '$($key.Substring(0, $key.Length - 4))' has .one but no .other")
    }
}
$rows = New-Object System.Collections.Generic.List[object]
$rows.Add((New-Row $en 'shipped' $en.Entries.Count $en.Entries.Count))

# --- Translations ---
$sources = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem -LiteralPath $shippedDir -Filter '*.json' | Sort-Object Name) { $sources.Add(@('shipped', $f.FullName)) }
foreach ($dir in $Path) {
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { Write-Error "Folder not found: $dir"; exit 1 }
    foreach ($f in Get-ChildItem -LiteralPath $dir -Filter '*.json' | Sort-Object Name) { $sources.Add(@('extra', $f.FullName)) }
}

foreach ($s in $sources) {
    $file = $s[1]
    $leaf = Split-Path -Leaf $file
    $isNotesFile = $leaf -like '*.notes.json'
    if ($s[0] -eq 'shipped' -and $leaf -eq 'en.json') { continue }

    $cat = Read-Catalog $file

    # For notes files, only validate JSON structure; skip translation checks
    if ($isNotesFile) {
        $rows.Add((New-Row $cat $s[0] 0 0))
        continue
    }

    $translated = 0
    $total = 0
    foreach ($key in $en.Entries.Keys) {
        # Languages without plural forms never use key.one.
        if ($cat.Plural -eq 'none' -and $key.EndsWith('.one')) { continue }
        $total++
    }
    foreach ($key in $cat.Entries.Keys) {
        if (-not $en.Entries.Contains($key)) { $cat.Errors.Add("unknown key '$key'"); continue }
        $ph = Get-Placeholders $cat.Entries[$key]
        if ($null -eq $ph) { $cat.Errors.Add("'$key' has unbalanced braces or an invalid placeholder"); continue }
        $allowed = @($enPlaceholders[$key])
        $unknown = @($ph | Where-Object { $allowed -cnotcontains $_ })
        if ($unknown.Count -gt 0) { $cat.Errors.Add("'$key' uses unknown placeholder {$($unknown[0])}"); continue }
        $missing = @($allowed | Where-Object { $ph -cnotcontains $_ })
        if ($missing.Count -gt 0) { $cat.Warnings.Add("'$key' does not use placeholder {$($missing[0])}") }
        if ($key.EndsWith('.one')) {
            if ($cat.Plural -eq 'none') { $cat.Warnings.Add("'$key' is never used (_meta.plural is 'none')"); continue }
            if (-not $cat.Entries.Contains($key.Substring(0, $key.Length - 4) + '.other')) {
                $cat.Warnings.Add("'$key' is translated but its .other form is not (English is used for other counts)")
            }
        }
        $translated++
    }
    $rows.Add((New-Row $cat $s[0] $translated $total))
}

$errorCount = ($rows | ForEach-Object { $_.Errors.Count } | Measure-Object -Sum).Sum
if ($Json) {
    [pscustomobject]@{ ok = ($errorCount -eq 0); catalogs = $rows } | ConvertTo-Json -Depth 5
}
else {
    $rows | Select-Object Code, NativeName, File, Source, @{ n = 'Keys'; e = { "$($_.Translated) / $($_.Total)" } },
        @{ n = 'Complete'; e = { '{0:0.0} %' -f $_.Completeness } },
        @{ n = 'Errors'; e = { $_.Errors.Count } }, @{ n = 'Warnings'; e = { $_.Warnings.Count } } |
        Format-Table -AutoSize | Out-String -Width 200 | Write-Output
    foreach ($r in $rows) {
        foreach ($e in $r.Errors) { Write-Output "ERROR   [$($r.File)] $e" }
        foreach ($w in $r.Warnings) { Write-Output "WARNING [$($r.File)] $w" }
    }
    if ($errorCount -eq 0) { Write-Output 'PASS: translation catalogs are valid' }
    else { Write-Output "FAIL: $errorCount error(s) in translation catalogs" }
}
if ($errorCount -gt 0) { exit 1 }
exit 0
