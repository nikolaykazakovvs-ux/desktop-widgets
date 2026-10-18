<# Builds source in this checkout, stages only allowlisted public files and emits release assets.
   Does not launch widgets or installers and requires no downloads. #>
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$PayloadDirectory = (Join-Path $PSScriptRoot '../windows/resources-widget/installer/payload'),
    [string]$ExistingLimitsArchive,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw 'Use a new or empty output directory; release files are never overwritten.' }
} else { New-Item -ItemType Directory -Path $output | Out-Null }
if (-not $SkipBuild) {
    & (Join-Path $root 'windows\resources-widget\build.ps1')
    if (-not $ExistingLimitsArchive) { & (Join-Path $root 'windows\limits-widget\build.ps1') }
    & (Join-Path $root 'windows\resources-widget\installer\build-setup.ps1') -SkipWidgetBuild -PayloadDirectory $PayloadDirectory
}
$staging = Join-Path $output ('staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
foreach ($widget in @(
    @{Folder='resources-widget'; Exe='ResourcesWidget.exe'; Archive='ResourcesWidget-1.1.1-win-x64.zip'},
    @{Folder='limits-widget'; Exe='LimitsWidget.exe'; Archive='LimitsWidget-1.0.0-win.zip'}
)) {
    if ($widget.Folder -eq 'limits-widget' -and $ExistingLimitsArchive) { Copy-Item -LiteralPath $ExistingLimitsArchive -Destination (Join-Path $output $widget.Archive); continue }
    $source = Join-Path $root ('windows\' + $widget.Folder)
    $target = Join-Path $staging $widget.Folder
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $widget.Exe) -Destination $target
    Copy-Item -LiteralPath (Join-Path $source 'config.example.json') -Destination (Join-Path $target 'config.json')
    Copy-Item -LiteralPath (Join-Path $source 'config.example.json') -Destination $target
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $target
    Copy-Item -LiteralPath (Join-Path $source 'START-HERE.txt') -Destination $target
    if ($widget.Folder -eq 'resources-widget') {
        Copy-Item -LiteralPath (Join-Path $source 'installer\payload\THIRD-PARTY.txt') -Destination $target
    }
    Compress-Archive -LiteralPath $target -DestinationPath (Join-Path $output $widget.Archive) -CompressionLevel Optimal
}
Copy-Item -LiteralPath (Join-Path $root 'windows\resources-widget\ResourcesWidgetSetup-1.1.1-x64.exe') -Destination $output
# Bounded cleanup: staging was created under the exact requested output directory above.
$resolvedStaging = [IO.Path]::GetFullPath($staging)
if (-not $resolvedStaging.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path.' }
Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
$hashes = Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
Write-Host "Release assets: $output"
