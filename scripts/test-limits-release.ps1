<# Focused release and reversible-updater validator for AI Limits 1.1.0 (prerelease v0.2.0).
   Validates LimitsWidget-1.1.0-win-x64.zip, SHA256SUMS.txt, package-manifest.json, headless smoke,
   and all isolated updater containment/process-tree/legacy-archiving/rollback tests. #>
param(
    [Parameter(Mandatory=$true)][string]$ArtifactsDirectory,
    [Parameter(Mandatory=$true)][string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-release.ps1') -ArtifactsDirectory $ArtifactsDirectory -ResultsDirectory $ResultsDirectory -Scope Limits
