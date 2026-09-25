[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Folder,
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $Folder).Path
$files = Get-ChildItem -LiteralPath $resolved -File | Where-Object { $_.Extension -match '^\.(jpg|jpeg|png|bmp|gif|tif|tiff)$' }
if ($files.Count -eq 0) { throw "Không tìm thấy ảnh được hỗ trợ trong: $resolved" }

$times = [System.Collections.Generic.List[double]]::new()
for ($run = 1; $run -le [Math]::Max(1, $Runs); $run++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $bytes = 0L
    foreach ($file in $files) {
        $bytes += $file.Length
        # Read sequentially to measure source-storage throughput without changing files.
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $buffer = [byte[]]::new(1024 * 1024); while ($stream.Read($buffer, 0, $buffer.Length) -gt 0) {} }
        finally { $stream.Dispose() }
    }
    $timer.Stop()
    $times.Add($timer.Elapsed.TotalMilliseconds)
    Write-Host ("Run {0}: {1:N0} ms, {2} files, {3:N2} MB" -f $run, $timer.Elapsed.TotalMilliseconds, $files.Count, ($bytes / 1MB))
}

$ordered = $times | Sort-Object
$median = $ordered[[int][Math]::Floor(($ordered.Count - 1) / 2)]
Write-Host ("SUMMARY: folder={0}; files={1}; median-read={2:N0} ms; throughput={3:N2} MB/s" -f $resolved, $files.Count, $median, (($files | Measure-Object Length -Sum).Sum / 1MB / ($median / 1000))) -ForegroundColor Green
Write-Host 'Lưu ý: đây là baseline đọc storage, không phải thời gian decode WPF. Dùng cùng folder/ổ đĩa để so sánh các bản build.'
