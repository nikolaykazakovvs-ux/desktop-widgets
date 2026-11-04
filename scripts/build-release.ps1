<# Builds source in this checkout, stages only allowlisted public files and emits release assets.
   Supports -Scope All (default), -Scope Limits (v0.2.0 LimitsWidget-1.1.0-win-x64.zip + SHA256SUMS.txt only),
   and -Scope Resources. Does not launch widgets or installers and requires no downloads. #>
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$PayloadDirectory = (Join-Path $PSScriptRoot '../windows/resources-widget/installer/payload'),
    [string]$ExistingLimitsArchive,
    [ValidateSet('All', 'Limits', 'Resources')][string]$Scope = 'All',
    [switch]$LimitsOnly,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if ($LimitsOnly) { $Scope = 'Limits' }
$buildResources = ($Scope -eq 'All' -or $Scope -eq 'Resources')
$buildLimits = ($Scope -eq 'All' -or $Scope -eq 'Limits')

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function New-StandardZipArchive([string]$SourceDirectory, [string]$RootFolderName, [string]$DestinationZipPath) {
    if (Test-Path -LiteralPath $DestinationZipPath) {
        Remove-Item -LiteralPath $DestinationZipPath -Force
    }
    $fullSource = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    $zip = [IO.Compression.ZipFile]::Open($DestinationZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @(Get-ChildItem -LiteralPath $fullSource -File -Force -Recurse | Sort-Object FullName)
        foreach ($file in $files) {
            $relPath = $file.FullName.Substring($fullSource.Length + 1).Replace('\', '/')
            $entryName = "$RootFolderName/$relPath"
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                $file.FullName,
                $entryName,
                [IO.Compression.CompressionLevel]::Optimal
            )
        }
    } finally {
        $zip.Dispose()
    }
}

$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw 'Use a new or empty output directory; release files are never overwritten.' }
} else { New-Item -ItemType Directory -Path $output | Out-Null }

$staging = Join-Path $output ('staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    if ($buildResources -and -not $SkipBuild) {
        & (Join-Path $root 'windows\resources-widget\build.ps1')
        & (Join-Path $root 'windows\resources-widget\installer\build-setup.ps1') -SkipWidgetBuild -PayloadDirectory $PayloadDirectory
    }

    $limitsSourceDir = Join-Path $root 'windows\limits-widget'
    $builtLimitsExe = Join-Path $limitsSourceDir 'LimitsWidget.exe'
    if ($buildLimits -and -not $ExistingLimitsArchive) {
        if (-not $SkipBuild) {
            # Build via the existing widget build.ps1 inside an isolated staging copy so windows/limits-widget stays read-only/clean.
            $isolatedBuildRoot = Join-Path $staging 'limits-build-tree'
            $isolatedLimitsDir = Join-Path $isolatedBuildRoot 'windows\limits-widget'
            $isolatedSharedDir = Join-Path $isolatedBuildRoot 'telemetry\publisher-shared'
            New-Item -ItemType Directory -Path $isolatedLimitsDir -Force | Out-Null
            New-Item -ItemType Directory -Path $isolatedSharedDir -Force | Out-Null

            foreach ($item in @('app.manifest', 'build.ps1', 'config.example.json', 'START-HERE.txt', 'assets', 'tools', 'src')) {
                $srcItem = Join-Path $limitsSourceDir $item
                if (Test-Path -LiteralPath $srcItem) {
                    Copy-Item -LiteralPath $srcItem -Destination (Join-Path $isolatedLimitsDir $item) -Recurse -Force
                }
            }
            # Ensure staged build.ps1 has UTF-8 BOM so Windows PowerShell 5.1 parses Cyrillic/em-dash literals as UTF-8
            $stagedBuildPs1 = Join-Path $isolatedLimitsDir 'build.ps1'
            $buildPs1Text = [IO.File]::ReadAllText($stagedBuildPs1, [Text.Encoding]::UTF8)
            [IO.File]::WriteAllText($stagedBuildPs1, $buildPs1Text, [Text.UTF8Encoding]::new($true))
            Copy-Item -LiteralPath (Join-Path $root 'telemetry\publisher-shared\TelemetrySnapshotWriter.cs') -Destination (Join-Path $isolatedSharedDir 'TelemetrySnapshotWriter.cs') -Force

            & (Join-Path $isolatedLimitsDir 'build.ps1')
            $builtLimitsExe = Join-Path $isolatedLimitsDir 'LimitsWidget.exe'
        }
        if (-not (Test-Path -LiteralPath $builtLimitsExe -PathType Leaf)) {
            throw "Compiled LimitsWidget.exe not found at '$builtLimitsExe'."
        }
    }

    $widgetsToStage = @()
    if ($buildResources) {
        $widgetsToStage += @{Folder='resources-widget'; Exe='ResourcesWidget.exe'; Archive='ResourcesWidget-1.1.1-win-x64.zip'; Version='1.1.1'}
    }
    if ($buildLimits) {
        $widgetsToStage += @{Folder='limits-widget'; Exe='LimitsWidget.exe'; Archive='LimitsWidget-1.1.0-win-x64.zip'; Version='1.1.0'}
    }

    foreach ($widget in $widgetsToStage) {
        if ($widget.Folder -eq 'limits-widget' -and $ExistingLimitsArchive) {
            Copy-Item -LiteralPath $ExistingLimitsArchive -Destination (Join-Path $output $widget.Archive)
            continue
        }
        $source = Join-Path $root ('windows\' + $widget.Folder)
        $target = Join-Path $staging $widget.Folder
        New-Item -ItemType Directory -Path $target | Out-Null

        if ($widget.Folder -eq 'limits-widget') {
            Copy-Item -LiteralPath $builtLimitsExe -Destination (Join-Path $target $widget.Exe)
        } else {
            Copy-Item -LiteralPath (Join-Path $source $widget.Exe) -Destination $target
        }
        Copy-Item -LiteralPath (Join-Path $source 'config.example.json') -Destination (Join-Path $target 'config.json')
        Copy-Item -LiteralPath (Join-Path $source 'config.example.json') -Destination $target
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $target
        Copy-Item -LiteralPath (Join-Path $source 'START-HERE.txt') -Destination $target

        if ($widget.Folder -eq 'resources-widget') {
            Copy-Item -LiteralPath (Join-Path $source 'installer\payload\THIRD-PARTY.txt') -Destination $target
        } elseif ($widget.Folder -eq 'limits-widget') {
            # Ensure START-HERE.txt in the staged package reflects 1.1.0 without mutating windows/limits-widget
            $stagedStartHere = Join-Path $target 'START-HERE.txt'
            $startHereText = [IO.File]::ReadAllText($stagedStartHere, [Text.Encoding]::UTF8)
            if ($startHereText.Contains('1.0.0')) {
                $startHereText = $startHereText.Replace('1.0.0', $widget.Version)
                [IO.File]::WriteAllText($stagedStartHere, $startHereText, [Text.UTF8Encoding]::new($false))
            }

            # Verify public config.example.json / config.json has all providers disabled and allowLocalTokens = false
            $cfgObj = [IO.File]::ReadAllText((Join-Path $target 'config.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
            if ($cfgObj.allowLocalTokens -ne $false) { throw 'Public limits config.json must have allowLocalTokens = false.' }
            foreach ($svcName in @('claude', 'kimi', 'antigravity', 'codex')) {
                $svcProp = $cfgObj.services.PSObject.Properties[$svcName]
                if ($null -eq $svcProp -or $svcProp.Value.enabled -ne $false) {
                    throw "Public limits config.json must have services.$svcName.enabled = false."
                }
            }

            # Include reversible updater script in the package allowlist
            $updaterSrc = Join-Path $PSScriptRoot 'update-limits-widget.ps1'
            if (-not (Test-Path -LiteralPath $updaterSrc -PathType Leaf)) {
                throw "Missing updater script at '$updaterSrc'."
            }
            Copy-Item -LiteralPath $updaterSrc -Destination (Join-Path $target 'update-limits-widget.ps1')

            # Verify executable metadata consistency and emit package-manifest.json
            $stagedExe = Join-Path $target $widget.Exe
            $vi = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedExe)
            if ($vi.ProductName -ne 'LimitsWidget') {
                throw "LimitsWidget.exe ProductName mismatch: '$($vi.ProductName)'."
            }
            if (-not $vi.FileVersion.StartsWith($widget.Version, [StringComparison]::Ordinal)) {
                throw "LimitsWidget.exe FileVersion ($($vi.FileVersion)) does not match release version $($widget.Version)."
            }

            $allowlistedFiles = @(
                'LimitsWidget.exe',
                'config.json',
                'config.example.json',
                'LICENSE',
                'START-HERE.txt',
                'update-limits-widget.ps1'
            )
            $manifestEntries = @()
            foreach ($relName in $allowlistedFiles) {
                $fPath = Join-Path $target $relName
                $fItem = Get-Item -LiteralPath $fPath -Force
                $fHash = (Get-FileHash -LiteralPath $fPath -Algorithm SHA256).Hash.ToLowerInvariant()
                $mode = if ($relName -eq 'config.json') { 'seed-if-missing' } else { 'replace' }
                $manifestEntries += [ordered]@{
                    path        = $relName
                    sha256      = $fHash
                    size        = [int64]$fItem.Length
                    installMode = $mode
                }
            }
            $manifestObj = [ordered]@{
                schemaVersion     = '1.0'
                product           = 'LimitsWidget'
                title             = 'AI Limits'
                version           = $widget.Version
                releaseTag        = 'v0.2.0'
                archiveName       = $widget.Archive
                rootFolder        = 'limits-widget'
                executable        = $widget.Exe
                executableVersion = $vi.FileVersion
                files             = $manifestEntries
            }
            $manifestJson = $manifestObj | ConvertTo-Json -Depth 8
            [IO.File]::WriteAllText((Join-Path $target 'package-manifest.json'), $manifestJson, [Text.UTF8Encoding]::new($false))
        }

        New-StandardZipArchive -SourceDirectory $target -RootFolderName $widget.Folder -DestinationZipPath (Join-Path $output $widget.Archive)
    }

    if ($buildResources) {
        Copy-Item -LiteralPath (Join-Path $root 'windows\resources-widget\ResourcesWidgetSetup-1.1.1-x64.exe') -Destination $output
    }
} finally {
    # Bounded cleanup: staging was created under the exact requested output directory above.
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if (-not $resolvedStaging.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path.' }
    if (Test-Path -LiteralPath $resolvedStaging) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

$hashes = Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
Write-Host "Release assets ($Scope): $output"
