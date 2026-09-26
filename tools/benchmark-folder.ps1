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
$buffer = [byte[]]::new(1024 * 1024)
for ($run = 1; $run -le [Math]::Max(1, $Runs); $run++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $bytes = 0L
    foreach ($file in $files) {
        $bytes += $file.Length
        # Read sequentially to measure source-storage throughput without changing files.
        $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite, 1024 * 1024, [IO.FileOptions]::SequentialScan)
        try { while ($stream.Read($buffer, 0, $buffer.Length) -gt 0) {} }
        finally { $stream.Dispose() }
    }
    $timer.Stop()
    $times.Add($timer.Elapsed.TotalMilliseconds)
    Write-Host ("Run {0}: {1:N0} ms, {2} files, {3:N2} MB" -f $run, $timer.Elapsed.TotalMilliseconds, $files.Count, ($bytes / 1MB))
}

# Only run 1 can hit the disk; later runs are served from the Windows file cache. Report them separately.
$totalMb = ($files | Measure-Object Length -Sum).Sum / 1MB
$cold = $times[0]
Write-Host ("SUMMARY: folder={0}; files={1}; cold(first read)={2:N0} ms, {3:N2} MB/s" -f $resolved, $files.Count, $cold, ($totalMb / ($cold / 1000))) -ForegroundColor Green
if ($times.Count -gt 1) {
    $warm = @($times | Select-Object -Skip 1 | Sort-Object)
    $median = $warm[[int][Math]::Floor(($warm.Count - 1) / 2)]
    Write-Host ("         warm(OS cache, median of runs 2-{0})={1:N0} ms, {2:N2} MB/s" -f $times.Count, $median, ($totalMb / ($median / 1000))) -ForegroundColor Green
}
Write-Host 'Lưu ý: chỉ lần đọc đầu (cold) phản ánh storage, và nó chỉ thật sự cold nếu file chưa nằm trong OS cache; các lần sau là tốc độ RAM (OS cache), không phải storage. Không phải thời gian decode WPF. Dùng cùng folder/ổ đĩa để so sánh các bản build.'
