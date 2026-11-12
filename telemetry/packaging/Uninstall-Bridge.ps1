<#
.SYNOPSIS Удаляет лаунчеры, current.json и версионные каталоги Bridge.
 Каталог версии, занятый процессом (IOException/UnauthorizedAccess), не форсируется: статус deferred.
 -KeepVersion <id,...>  не удалять указанные версии. Процессы не останавливаются.
 -KeepRegistration      не снимать регистрацию widgets-bridge в клиентах (по умолчанию снимается запись, указывающая на
                        лаунчер этой установки; чужие записи и записи другой установки не трогаются; Register-Clients.ps1 -Remove);
                        при ошибке снятия регистрации остальное удаляется, отчёт получает partial=true, код возврата 1
 -ClientsHomeDir <abs>  изоляция (тесты): профиль, в котором ищутся клиенты
 Удаляются также лаунчеры widgets-bridge*.cmd и discovery.json.
 Удаляются только каталоги versions\<semver>-<12 hex> с manifest.json, чей id равен имени; прочее остаётся (foreign).
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [string[]]$KeepVersion = @(),
    [switch]$KeepRegistration,
    [string]$ClientsHomeDir
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
$hasMarker = (Test-Path -LiteralPath (Join-Path $root 'current.json')) -or (Test-Path -LiteralPath (Join-Path $root 'bridge-cli.cmd')) -or (Test-Path -LiteralPath (Join-Path $root 'bridge-server.cmd')) -or (Test-Path -LiteralPath (Join-Path $root 'widgets-bridge.cmd')) -or (Test-Path -LiteralPath (Join-Path $root 'widgets-bridge-server.cmd')) -or (Test-Path -LiteralPath (Join-Path $root 'discovery.json'))
$hasVersion = (Test-Path -LiteralPath $vrootCheck) -and [bool](Get-ChildItem -LiteralPath $vrootCheck -Directory -Force | Where-Object { Test-BridgeVersionDir $_ } | Select-Object -First 1)
if (-not $hasMarker -and -not $hasVersion) { Write-Output ([ordered]@{ removed = @(); deferred = @(); kept = @(); foreign = @(); note = 'not a Bridge install root; nothing removed' } | ConvertTo-Json -Compress); return }
# Сначала снимается регистрация в клиентах (пока лаунчер ещё на месте и по нему отличается своя запись от чужой).
$registration = $null
$registrationWarnings = @()
if (-not $KeepRegistration) {
    try {
        $regArgs = @{ InstallRoot = $root; Remove = $true }
        if ($ClientsHomeDir) { $regArgs.HomeDir = $ClientsHomeDir }
        $regOut = & "$PSScriptRoot\Register-Clients.ps1" @regArgs | Out-String
        $regParsed = $regOut | ConvertFrom-Json
        $registration = @($regParsed.clients)
        $registrationWarnings = @($regParsed.warnings)
    } catch { $registration = @([ordered]@{ client = '*'; status = 'error'; detail = $_.Exception.Message }) }
}
foreach ($f in 'bridge-server.cmd', 'bridge-cli.cmd', 'widgets-bridge-server.cmd', 'widgets-bridge.cmd', 'discovery.json', 'current.json') {
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
$result = [ordered]@{ removed = $removed; deferred = $deferred; kept = $kept; foreign = $foreign }
# Ошибка снятия регистрации деинсталляцию не останавливает, но результат частичный: код возврата 1 и partial=true
# (запись клиента может остаться и указывать на удалённый лаунчер; повторите Register-Clients.ps1 -Remove после исправления).
$exitCode = 0
if ($null -ne $registration) {
    $result.registration = $registration
    if (@($registrationWarnings).Count -gt 0) { $result.registration_warnings = @($registrationWarnings) }
    if (@($registration | Where-Object { $_.status -eq 'error' }).Count -gt 0) { $result.partial = $true; $exitCode = 1 }
}
Write-Output ($result | ConvertTo-Json -Compress -Depth 6)
exit $exitCode
