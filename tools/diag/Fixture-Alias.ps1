# Fixtures-file alias helper for make-subset-fixture.ps1 (dot-sourced). Own file so tests can dot-source it without running the builder.

function ConvertTo-JsonString([string]$s) { '"' + ($s -replace '\\', '\\' -replace '"', '\"') + '"' }

function Set-FixtureAliasText([string]$Raw, [string]$Alias, [string]$EntryLine) {
    # Text edit only (keeps the user's file, formatting, key order and the repo's unescaped-backslash style). An existing entry is
    # matched as a WHOLE entry across lines: "alias": { ...no nested braces... } (or a plain string value), so a multi-line entry
    # is replaced completely instead of leaving its remaining lines behind (which made the file invalid JSON).
    $entryRegex = New-Object System.Text.RegularExpressions.Regex(('^[ \t]*"' + [regex]::Escape($Alias) + '"[ \t\r\n]*:[ \t\r\n]*(\{[^{}]*\}|"[^"\r\n]*")'), 'Multiline')
    if ($entryRegex.IsMatch($Raw)) {
        return $entryRegex.Replace($Raw, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $EntryLine }, 1)
    }
    $close = $Raw.LastIndexOf('}')
    if ($close -lt 0) { throw 'fixtures file has no closing brace' }
    $head = $Raw.Substring(0, $close).TrimEnd()
    $sep = if ($head.EndsWith('{')) { '' } else { ',' }
    return $head + $sep + "`r`n" + $EntryLine + "`r`n" + $Raw.Substring($close)
}
