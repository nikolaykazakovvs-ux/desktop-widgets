<#
.SYNOPSIS Удаляет лаунчеры, current.json и версионные каталоги Bridge.
 Каталог версии, занятый процессом (IOException/UnauthorizedAccess), не форсируется: статус deferred.
 -KeepVersion <id,...>  не удалять указанные версии. Процессы не останавливаются.
 Удаляются только каталоги versions\<semver>-<12 hex> с manifest.json, чей id равен имени; прочее остаётся (foreign).
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [string[]]$KeepVersion = @()
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
$root = Assert-SafeInstallRoot $InstallRoot
$removed = @(); $deferred = @(); $kept = @(); $foreign = @()
if (-not (Test-Path -LiteralPath $root)) { Write-Output '{"removed":[],"deferred":[],"kept":[],"foreign":[],"note":"nothing installed"}'; return }
# Only a Bridge install root is touched: a marker (current.json or a launcher) or at least one real version directory.
function Test-BridgeVersionDir($Dir) {
    if (-not (Test-VersionId $Dir.Name)) { return $false }
    $mf = Join-Path $Dir.FullName 'manifest.json'
    if (-not (Test-Path -LiteralPath $mf)) { return $false }
    try { return ((Get-Content -LiteralPath $mf -Raw | ConvertFrom-Json).id -ceq $Dir.Name) } catch { return $false }
}
$vrootCheck = Join-Path $root 'versions'
$hasMarker = (Test-Path -LiteralPath (Join-Path $root 'current.json')) -or (Test-Path -LiteralPath (Join-Path $root 'bridge-cli.cmd')) -or (Test-Path -LiteralPath (Join-Path $root 'bridge-server.cmd'))
$hasVersion = (Test-Path -LiteralPath $vrootCheck) -and [bool](Get-ChildItem -LiteralPath $vrootCheck -Directory -Force | Where-Object { Test-BridgeVersionDir $_ } | Select-Object -First 1)
if (-not $hasMarker -and -not $hasVersion) { Write-Output ([ordered]@{ removed = @(); deferred = @(); kept = @(); foreign = @(); note = 'not a Bridge install root; nothing removed' } | ConvertTo-Json -Compress); return }
foreach ($f in 'bridge-server.cmd', 'bridge-cli.cmd', 'current.json') {
    $p = Join-Path $root $f
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; $removed += $f }
}
$vroot = Join-Path $root 'versions'
if (Test-Path -LiteralPath $vroot) {
    # Windows does not lock loaded Node modules, so a delete can succeed under a live MCP session.
    # Read-only observation of command lines (never Stop-Process): a referenced version is deferred.
    $commandLines = @(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue | ForEach-Object { $_.CommandLine } | Where-Object { $_ })
    foreach ($v in Get-ChildItem -LiteralPath $vroot -Directory -Force) {
        if (-not (Test-BridgeVersionDir $v)) { $foreign += "versions\$($v.Name)"; continue }
        if ($KeepVersion -contains $v.Name) { $kept += $v.Name; continue }
        $needle = $v.FullName.TrimEnd('\') + '\'
        if ($commandLines | Where-Object { $_.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) {
            $deferred += [ordered]@{ version = $v.Name; reason = 'in_use_by_running_process'; dirStillExists = $true }
            continue
        }
        try { [IO.Directory]::Delete($v.FullName, $true); $removed += "versions\$($v.Name)" }
        catch [IO.IOException], [UnauthorizedAccessException] {
            $deferred += [ordered]@{ version = $v.Name; reason = $_.Exception.GetType().Name; dirStillExists = (Test-Path -LiteralPath $v.FullName) }
        }
    }
}
$st = Join-Path $root 'staging'
if (Test-Path -LiteralPath $st) { Remove-Item -LiteralPath $st -Recurse -Force -ErrorAction SilentlyContinue }
if (-not $deferred -and -not $kept) {
    if ((Test-Path -LiteralPath $vroot) -and -not (Get-ChildItem -LiteralPath $vroot -Force)) { Remove-Item -LiteralPath $vroot -Force }
    if (-not (Get-ChildItem -LiteralPath $root -Force)) { Remove-Item -LiteralPath $root -Force; $removed += '<InstallRoot>' }
}
Write-Output ([ordered]@{ removed = $removed; deferred = $deferred; kept = $kept; foreign = $foreign } | ConvertTo-Json -Compress -Depth 4)
