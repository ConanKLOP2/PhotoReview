param(
    # Which registration to remove: 'Wpf' (default, unchanged) = ProgId "PhotoReview.App" + folder key "PhotoReview";
    # 'Shell' = ProgId "PhotoReview.Shell" + folder key "PhotoReview.Shell". Never both at once, never a neighbour key.
    [ValidateSet('Wpf', 'Shell')]
    [string]$Variant = 'Wpf',

    # Registry root to clean. Default = the real per-user classes. Tests pass a private key.
    [string]$ClassesPath = 'HKCU:\Software\Classes'
)

$ErrorActionPreference = 'Stop'
if ($Variant -eq 'Shell') { $progId = 'PhotoReview.Shell'; $verbKey = 'PhotoReview.Shell' }
else { $progId = 'PhotoReview.App'; $verbKey = 'PhotoReview' }
$classes = $ClassesPath.TrimEnd('\')

foreach ($extension in '.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff') {
    $openWith = Join-Path $classes "$extension\OpenWithProgids"
    if (Test-Path -LiteralPath $openWith) {
        Remove-ItemProperty -LiteralPath $openWith -Name $progId -ErrorAction SilentlyContinue
    }
}

$progPath = Join-Path $classes $progId
if (Test-Path -LiteralPath $progPath) {
    Remove-Item -LiteralPath $progPath -Recurse -Force
}

# The Explorer folder command "Browse with PhotoReview": only these two keys, never their neighbours.
foreach ($verb in "Directory\shell\$verbKey", "Directory\Background\shell\$verbKey") {
    $verbPath = Join-Path $classes $verb
    if (Test-Path -LiteralPath $verbPath) {
        Remove-Item -LiteralPath $verbPath -Recurse -Force
    }
}

Write-Host "Photo Review ($Variant) file association and Explorer folder menu removed for the current Windows user."
Write-Host 'Published files and source files were not deleted.'
