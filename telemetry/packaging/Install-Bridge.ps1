<#
.SYNOPSIS Устанавливает (или обновляет) AI Telemetry Bridge в версионированный каталог.
 -SourceRoot  корень репозитория (содержит telemetry\bridge, telemetry\schema, telemetry\fixtures)
 -InstallRoot по умолчанию %LOCALAPPDATA%\DesktopWidgets\bridge
 -Rollback    переключить current на previous (после проверки manifest); discovery.json переписывается под текущую версию
 -RegisterClients  после установки идемпотентно прописать MCP-сервер widgets-bridge в найденных клиентах (Register-Clients.ps1);
               без флага конфигурации клиентов не меняются
 -ClientsHomeDir   изоляция (тесты): профиль, в котором ищутся клиенты (см. Register-Clients.ps1 -HomeDir)
После успешной установки/отката атомарно пишется <InstallRoot>\discovery.json (путь к CLI и MCP, версии, данные).
Версионные каталоги неизменяемы и здесь не удаляются: активные MCP-процессы работают со своей версией.
#>
[CmdletBinding()]
param(
    [string]$SourceRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [switch]$Rollback,
    [switch]$RegisterClients,
    [string]$ClientsHomeDir
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
# Параметры регистрации проверяются ДО любых изменений установки: ошибка не должна оставлять переключённую версию без отчёта.
if ($ClientsHomeDir -and -not [IO.Path]::IsPathRooted($ClientsHomeDir)) { throw 'ClientsHomeDir must be absolute.' }
$root = Assert-SafeInstallRoot $InstallRoot
if (Test-Path -LiteralPath $root) {
    Assert-PrivateInstallRoot $root
} else {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Set-PrivateDirectoryAcl $root
}

if ($Rollback) {
    $cur = Read-Current $root
    if (-not $cur -or -not $cur.previous) { throw 'No previous version recorded; rollback impossible.' }
    Assert-VersionId $cur.previous 'current.json previous'
    $prevDir = Join-Path $root "versions\$($cur.previous)"
    if (-not (Test-Path -LiteralPath $prevDir)) { throw "Previous version dir missing: $prevDir" }
    $chk = Test-Manifest $prevDir
    if (-not $chk.ok) { throw "Previous version failed manifest check: $($chk.problems -join '; ')" }
    Write-Launchers $root $cur.previous
    $new = [ordered]@{ current = $cur.previous; previous = $cur.current; installed_at = (Get-Date).ToUniversalTime().ToString('o') }
    Write-AtomicText (Join-Path $root 'current.json') ($new | ConvertTo-Json)
    Write-Discovery $root $cur.previous
    Write-Output (@{ action = 'rollback'; current = $new.current; previous = $new.previous; discovery = (Join-Path $root 'discovery.json') } | ConvertTo-Json -Compress)
    return
}

if (-not $SourceRoot) { throw 'SourceRoot is required.' }
$tel = Join-Path $SourceRoot 'telemetry'
foreach ($d in 'bridge', 'schema', 'fixtures') { if (-not (Test-Path -LiteralPath (Join-Path $tel $d))) { throw "Source missing: $tel\$d" } }
foreach ($f in 'server.mjs', 'cli.mjs', 'core.mjs', 'package.json') { if (-not (Test-Path -LiteralPath (Join-Path $tel "bridge\$f"))) { throw "Source missing bridge\$f" } }
if (-not (Test-Path -LiteralPath (Join-Path $tel 'bridge\node_modules'))) { throw 'Source bridge\node_modules missing (run npm ci --ignore-scripts first).' }

$exitCode = 0
$stageRoot = Join-Path $root 'staging'
New-Item -ItemType Directory -Force -Path $stageRoot, (Join-Path $root 'versions') | Out-Null
$stage = Join-Path $stageRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    foreach ($d in 'bridge', 'schema', 'fixtures') {
        Copy-Item -LiteralPath (Join-Path $tel $d) -Destination (Join-Path $stage $d) -Recurse -Force
    }
    $files = New-ManifestObject $stage
    $idLines = $files | Where-Object { $_.path -match '^bridge/[^/]+\.(mjs|cmd)$' -or $_.path -match '^bridge/package(-lock)?\.json$' -or $_.path -match '^schema/([^/]+/)?[^/]+\.json$' } |
        ForEach-Object { "$($_.path)`t$($_.sha256)" }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($idLines -join "`n"))
    $hex = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', '').ToLowerInvariant().Substring(0, 12)
    $pkg = Get-Content -LiteralPath (Join-Path $stage 'bridge\package.json') -Raw | ConvertFrom-Json
    $id = "$($pkg.version)-$hex"
    $manifest = [ordered]@{ id = $id; version = $pkg.version; created_at = (Get-Date).ToUniversalTime().ToString('o'); files = $files }
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))

    $dest = Join-Path $root "versions\$id"
    $status = 'installed'
    if (Test-Path -LiteralPath $dest) {
        $chk = Test-Manifest $dest
        if (-not $chk.ok) { throw "Version $id exists but fails manifest check; refusing to overwrite: $($chk.problems -join '; ')" }
        $status = 'already-present'
    } else {
        [IO.Directory]::Move($stage, $dest)
        $stage = $null
    }

    $cur = Read-Current $root
    $prev = $null
    if ($cur) { $prev = if ($cur.current -eq $id) { $cur.previous } else { $cur.current } }
    Write-Launchers $root $id
    $new = [ordered]@{ current = $id; previous = $prev; installed_at = (Get-Date).ToUniversalTime().ToString('o') }
    Write-AtomicText (Join-Path $root 'current.json') ($new | ConvertTo-Json)
    Write-Discovery $root $id
    $result = [ordered]@{ action = $status; current = $id; previous = $prev; installRoot = $root; discovery = (Join-Path $root 'discovery.json') }
    if ($RegisterClients) {
        # Установка уже выполнена: сбой регистрации (в том числе исключение скрипта) попадает в отчёт и код возврата,
        # JSON-отчёт установки выводится всегда.
        try {
            $regArgs = @{ InstallRoot = $root }
            if ($ClientsHomeDir) { $regArgs.HomeDir = $ClientsHomeDir }
            $regOut = & "$PSScriptRoot\Register-Clients.ps1" @regArgs | Out-String
            $reg = $regOut | ConvertFrom-Json
            $result.registration = @($reg.clients)
            if (@($reg.warnings).Count -gt 0) { $result.registration_warnings = @($reg.warnings) }
            if (@($reg.clients | Where-Object { $_.status -eq 'error' }).Count -gt 0) { $exitCode = 1 }
        } catch {
            $result.registration = @([ordered]@{ client = '*'; status = 'error'; detail = $_.Exception.Message })
            $exitCode = 1
        }
    }
    Write-Output ($result | ConvertTo-Json -Compress -Depth 6)
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
