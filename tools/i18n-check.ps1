[CmdletBinding()]
param(
    # Extra folder(s) with translation files to check too, e.g. "$env:LOCALAPPDATA\PhotoReview\Languages".
    [string[]]$Path = @(),
    # Print a machine-readable JSON result instead of the table.
    [switch]$Json,
    # Run the in-memory self-test of the strict JSON validator instead of checking real files.
    [switch]$SelfTest
)

# L10 (docs/refactoring/I18N-PLAN.md, ADR 0006): checks the JSON translation catalogs.
# - every file is strict RFC 8259 JSON: no comments, no trailing/double commas, no duplicate keys at any
#   nesting level (the shipped catalogs and their *.notes.json siblings are meant to be clean by hand,
#   even though the app's own LanguageCatalog.TryParse is deliberately lenient at runtime)
# - _meta.code is present and valid; every value is a string
# - no unknown keys (a key English does not have), no broken braces, no placeholder English does not have
# - plural groups are sane (English: every key.one has key.other)
# Exit code 1 on errors. Missing translations (incompleteness) and dropped placeholders are warnings only.
# Works in Windows PowerShell 5.1 (.NET Framework, no System.Text.Json) and PowerShell 7+ identically:
# JSON validity and duplicate-key detection both go through the StrictJsonValidator C# parser below, the
# only JSON-syntax code path on either runtime. Keep this file ASCII-only.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$shippedDir = Join-Path $repoRoot 'src\PhotoReview.Core\Localization\Languages'
$maxFileBytes = 1024 * 1024   # LanguageCatalog.MaxFileBytes

# A small hand-written recursive-descent JSON parser (RFC 8259), compiled with Add-Type so it runs
# identically on Windows PowerShell 5.1 (.NET Framework; no System.Text.Json) and PowerShell 7+ (.NET).
# C# 5-compatible syntax only. It rejects anything System.Text.Json's lenient/strict modes disagree on
# (comments, trailing commas, double commas, single-quoted or unquoted keys, trailing garbage) and reports
# duplicate keys per object at every nesting level, so there is exactly one JSON-syntax code path.
$strictJsonSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class StrictJsonValidator
{
    private sealed class ParseError : Exception
    {
        public readonly int Line;
        public readonly int Column;

        public ParseError(string message, int line, int column)
            : base(message)
        {
            Line = line;
            Column = column;
        }
    }

    // Returns null when text is valid RFC 8259 JSON with no duplicate keys in any object.
    // Otherwise returns "line L, column C: <reason>" describing the first error found.
    public static string Validate(string text)
    {
        if (text == null) return "line 1, column 1: input is null";
        try
        {
            int pos = 0;
            if (text.Length > 0 && text[0] == '﻿') pos = 1;
            SkipWhitespace(text, ref pos);
            ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
            {
                Throw(text, pos, "Unexpected trailing content after JSON value");
            }
            return null;
        }
        catch (ParseError ex)
        {
            return "line " + ex.Line + ", column " + ex.Column + ": " + ex.Message;
        }
    }

    private static void Throw(string text, int pos, string message)
    {
        int limit = pos;
        if (limit > text.Length) limit = text.Length;
        if (limit < 0) limit = 0;
        int line = 1;
        int col = 1;
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else { col++; }
        }
        throw new ParseError(message, line, col);
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
            else break;
        }
    }

    private static bool IsDigit(char c)
    {
        return c >= '0' && c <= '9';
    }

    private static bool Match(string text, int pos, string literal)
    {
        if (pos + literal.Length > text.Length) return false;
        for (int i = 0; i < literal.Length; i++)
        {
            if (text[pos + i] != literal[i]) return false;
        }
        return true;
    }

    private static void ParseValue(string text, ref int pos)
    {
        if (pos >= text.Length) { Throw(text, pos, "Unexpected end of input"); return; }
        char c = text[pos];
        if (c == '{') { ParseObject(text, ref pos); return; }
        if (c == '[') { ParseArray(text, ref pos); return; }
        if (c == '"') { ParseString(text, ref pos); return; }
        if (c == '-' || IsDigit(c)) { ParseNumber(text, ref pos); return; }
        if (Match(text, pos, "true")) { pos += 4; return; }
        if (Match(text, pos, "false")) { pos += 5; return; }
        if (Match(text, pos, "null")) { pos += 4; return; }
        Throw(text, pos, "Unexpected character '" + c + "'");
    }

    private static void ParseObject(string text, ref int pos)
    {
        pos++; // consume '{'
        SkipWhitespace(text, ref pos);
        HashSet<string> seenKeys = new HashSet<string>(StringComparer.Ordinal);
        if (pos < text.Length && text[pos] == '}') { pos++; return; }
        while (true)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length || text[pos] != '"')
            {
                Throw(text, pos, "Expected string key");
            }
            int keyStart = pos;
            string key = ParseString(text, ref pos);
            if (!seenKeys.Add(key))
            {
                Throw(text, keyStart, "Duplicate key '" + key + "'");
            }
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length || text[pos] != ':')
            {
                Throw(text, pos, "Expected ':' after key");
            }
            pos++;
            SkipWhitespace(text, ref pos);
            ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) { Throw(text, pos, "Unexpected end of input in object"); }
            if (text[pos] == ',')
            {
                pos++;
                SkipWhitespace(text, ref pos);
                if (pos < text.Length && text[pos] == '}')
                {
                    Throw(text, pos, "Trailing comma before '}'");
                }
                continue;
            }
            if (text[pos] == '}') { pos++; return; }
            Throw(text, pos, "Expected ',' or '}'");
        }
    }

    private static void ParseArray(string text, ref int pos)
    {
        pos++; // consume '['
        SkipWhitespace(text, ref pos);
        if (pos < text.Length && text[pos] == ']') { pos++; return; }
        while (true)
        {
            SkipWhitespace(text, ref pos);
            ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) { Throw(text, pos, "Unexpected end of input in array"); }
            if (text[pos] == ',')
            {
                pos++;
                SkipWhitespace(text, ref pos);
                if (pos < text.Length && text[pos] == ']')
                {
                    Throw(text, pos, "Trailing comma before ']'");
                }
                continue;
            }
            if (text[pos] == ']') { pos++; return; }
            Throw(text, pos, "Expected ',' or ']'");
        }
    }

    private static string ParseString(string text, ref int pos)
    {
        int start = pos;
        pos++; // consume opening quote
        StringBuilder sb = new StringBuilder();
        while (true)
        {
            if (pos >= text.Length) { Throw(text, start, "Unterminated string"); }
            char c = text[pos];
            if (c == '"') { pos++; break; }
            if (c == '\\')
            {
                pos++;
                if (pos >= text.Length) { Throw(text, start, "Unterminated escape sequence"); }
                char e = text[pos];
                if (e == '"') { sb.Append('"'); pos++; }
                else if (e == '\\') { sb.Append('\\'); pos++; }
                else if (e == '/') { sb.Append('/'); pos++; }
                else if (e == 'b') { sb.Append('\b'); pos++; }
                else if (e == 'f') { sb.Append('\f'); pos++; }
                else if (e == 'n') { sb.Append('\n'); pos++; }
                else if (e == 'r') { sb.Append('\r'); pos++; }
                else if (e == 't') { sb.Append('\t'); pos++; }
                else if (e == 'u')
                {
                    pos++;
                    if (pos + 4 > text.Length) { Throw(text, pos, "Invalid \\u escape"); }
                    string hex = text.Substring(pos, 4);
                    int code;
                    if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                    {
                        Throw(text, pos, "Invalid \\u escape hex digits");
                    }
                    sb.Append((char)code);
                    pos += 4;
                }
                else
                {
                    Throw(text, pos, "Invalid escape character '\\" + e + "'");
                }
                continue;
            }
            if (c < 0x20)
            {
                Throw(text, pos, "Control character in string");
            }
            sb.Append(c);
            pos++;
        }
        return sb.ToString();
    }

    private static void ParseNumber(string text, ref int pos)
    {
        int start = pos;
        if (pos < text.Length && text[pos] == '-') pos++;
        if (pos >= text.Length || !IsDigit(text[pos])) { Throw(text, start, "Invalid number"); }
        if (text[pos] == '0')
        {
            pos++;
        }
        else
        {
            while (pos < text.Length && IsDigit(text[pos])) pos++;
        }
        if (pos < text.Length && text[pos] == '.')
        {
            pos++;
            if (pos >= text.Length || !IsDigit(text[pos])) { Throw(text, pos, "Invalid number: expected digit after '.'"); }
            while (pos < text.Length && IsDigit(text[pos])) pos++;
        }
        if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
        {
            pos++;
            if (pos < text.Length && (text[pos] == '+' || text[pos] == '-')) pos++;
            if (pos >= text.Length || !IsDigit(text[pos])) { Throw(text, pos, "Invalid number: expected digit in exponent"); }
            while (pos < text.Length && IsDigit(text[pos])) pos++;
        }
    }
}
'@
if (-not ([System.Management.Automation.PSTypeName]'StrictJsonValidator').Type) {
    Add-Type -TypeDefinition $strictJsonSource -Language CSharp
}

function Invoke-SelfTest {
    # In-memory proof that StrictJsonValidator behaves as intended, run with `-SelfTest` so it needs no
    # translation files on disk. Exits 0 if every case matches its expectation, 1 otherwise.
    $cases = @(
        [pscustomobject]@{
            Name = 'ValidWithTrickyContent'
            Json = '{"a": "line with ,, comma", "b": "contains \"x\": inside string"}'
            ExpectPass = $true
        },
        [pscustomobject]@{
            Name = 'DoubleComma'
            Json = '{"a":1,,"b":2}'
            ExpectPass = $false; ExpectLine = 1; ExpectColumn = 8
        },
        [pscustomobject]@{
            Name = 'TrailingComma'
            Json = '{"a":1,}'
            ExpectPass = $false; ExpectLine = 1; ExpectColumn = 8
        },
        [pscustomobject]@{
            Name = 'DuplicateTopLevelKey'
            Json = '{"a":1,"a":2}'
            ExpectPass = $false; ExpectLine = 1; ExpectColumn = 8
        },
        [pscustomobject]@{
            Name = 'DuplicateNestedKey'
            Json = '{"outer":{"x":1,"x":2}}'
            ExpectPass = $false; ExpectLine = 1; ExpectColumn = 17
        },
        [pscustomobject]@{
            Name = 'Comment'
            Json = '{"a": 1 /* c */}'
            ExpectPass = $false; ExpectLine = 1; ExpectColumn = 9
        },
        [pscustomobject]@{
            Name = 'TrailingGarbage'
            Json = '{"a":1} extra'
            ExpectPass = $false; ExpectLine = 1; ExpectColumn = 9
        }
    )

    $allOk = $true
    foreach ($c in $cases) {
        $err = [StrictJsonValidator]::Validate($c.Json)
        $passed = ($null -eq $err)
        $ok = $false
        $detail = ''

        if ($c.ExpectPass) {
            $ok = $passed
            $detail = if ($ok) { 'PASS (as expected)' } else { "FAIL - expected PASS but got error: $err" }
        }
        else {
            if (-not $passed) {
                if ($err -match '^line (\d+), column (\d+):') {
                    $gotLine = [int]$matches[1]
                    $gotCol = [int]$matches[2]
                    if ($gotLine -eq $c.ExpectLine -and $gotCol -eq $c.ExpectColumn) {
                        $ok = $true
                        $detail = "FAIL as expected (line $gotLine, column $gotCol): $err"
                    }
                    else {
                        $detail = "FAIL - expected error at line $($c.ExpectLine), column $($c.ExpectColumn) but got line $gotLine, column $gotCol ($err)"
                    }
                }
                else {
                    $detail = "FAIL - error message missing line/column: $err"
                }
            }
            else {
                $detail = 'FAIL - expected the parser to reject this input, but it passed'
            }
        }

        if (-not $ok) { $allOk = $false }
        Write-Output ('{0,-22} {1}' -f $c.Name, $(if ($ok) { 'ok' } else { 'MISMATCH' }))
        Write-Output "  $detail"
    }

    if ($allOk) {
        Write-Output 'SELFTEST PASS: all StrictJsonValidator cases matched their expectation'
        exit 0
    }
    else {
        Write-Output 'SELFTEST FAIL: one or more StrictJsonValidator cases did not match their expectation'
        exit 1
    }
}

if ($SelfTest) { Invoke-SelfTest }

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

function Test-JsonValid([string]$File) {
    # Strictly parses JSON (RFC 8259: no comments, no trailing/double commas) and checks for duplicate
    # keys at every nesting level, via the StrictJsonValidator C# parser above. One code path, same
    # behaviour on Windows PowerShell 5.1 and PowerShell 7+.
    # Returns $null on success; returns error message string on failure.
    $text = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8)
    $err = [StrictJsonValidator]::Validate($text)
    if ($null -ne $err) { return "Invalid JSON: $err" }
    return $null
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

    # Strict JSON validation (syntax + duplicate keys at every nesting level) - applies to all JSON files
    $jsonError = Test-JsonValid $File
    if ($null -ne $jsonError) {
        $result.Errors.Add($jsonError)
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
