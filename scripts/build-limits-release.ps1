<# Focused release builder for AI Limits 1.1.0 (prerelease v0.2.0).
   Emits only LimitsWidget-1.1.0-win-x64.zip and SHA256SUMS.txt without building ResourcesWidget or its installer. #>
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ExistingLimitsArchive,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-release.ps1') -OutputDirectory $OutputDirectory -Scope Limits -ExistingLimitsArchive $ExistingLimitsArchive -SkipBuild:$SkipBuild
