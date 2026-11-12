<#
.SYNOPSIS Идемпотентно прописывает (или снимает) MCP-сервер widgets-bridge в конфигурациях клиентов агентов.
 Клиенты (адаптеры): claude-code (~\.claude.json, mcpServers), codex (~\.codex\config.toml, [mcp_servers.widgets-bridge]),
 kimi-code (~\.kimi-code\mcp.json, mcpServers), antigravity (~\.gemini\config\mcp_config.json, mcpServers),
 gemini-cli (unsupported). Клиент без конфигурации получает статус not_found, файлы для него не создаются.
 -InstallRoot            каталог установки Bridge (по умолчанию %LOCALAPPDATA%\DesktopWidgets\bridge); регистрируется
                         <InstallRoot>\widgets-bridge-server.cmd (при регистрации лаунчер должен существовать)
 -Remove                 снять регистрацию: удаляется только запись widgets-bridge, указывающая на лаунчер этой установки
 -IncludeOtherInstalls   с -Remove: удалить запись widgets-bridge независимо от того, на какую установку она указывает
 -Clients <id,...>       ограничить список клиентов
 -HomeDir <abs>          изоляция (тесты): профиль, из которого берутся все пути клиентов; CLAUDE_CONFIG_DIR, CODEX_HOME и
                         KIMI_CODE_HOME тогда игнорируются
 -WhatIf                 показать план (в отчёте статус "что произошло бы", applied=false), ничего не писать
Перед правкой существующего файла рядом создаётся копия <файл>.bak-widgets-bridge-YYYYMMDD (один раз за запуск).
Закройте клиентов (Claude Code, Codex, Kimi, Antigravity) до регистрации/снятия: открытый клиент может молча затереть запись; при
запущенных процессах клиентов отчёт получает warnings (запись при этом не отменяется).
Выход: JSON-отчёт по клиентам. Статусы: registered | updated | already | removed | not_registered | kept_other_install |
not_found | unsupported | error. Код возврата 1, если хотя бы один клиент завершился error.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [switch]$Remove,
    [switch]$IncludeOtherInstalls,
    [string[]]$Clients,
    [string]$HomeDir
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\Registration.ps1"

$serverName = 'widgets-bridge'
$root = Assert-SafeInstallRoot $InstallRoot
$launcher = Join-Path $root 'widgets-bridge-server.cmd'
if (-not $Remove -and -not (Test-Path -LiteralPath $launcher)) { throw "Launcher not found (install the Bridge first): $launcher" }

if ($HomeDir) {
    if (-not [IO.Path]::IsPathRooted($HomeDir)) { throw 'HomeDir must be absolute.' }
    $envMap = $null
    $home_ = $HomeDir
} else {
    $envMap = @{ CLAUDE_CONFIG_DIR = $env:CLAUDE_CONFIG_DIR; CODEX_HOME = $env:CODEX_HOME; KIMI_CODE_HOME = $env:KIMI_CODE_HOME }
    $home_ = $env:USERPROFILE
}
if (-not $home_) { throw 'Profile directory is unknown.' }

$adapters = @(Get-ClientAdapters $home_ $envMap $serverName $launcher)
if ($Clients) {
    $unknown = @($Clients | Where-Object { $_ -notin @($adapters | ForEach-Object { $_.id }) })
    if ($unknown.Count -gt 0) { throw "Unknown client id: $($unknown -join ', ')" }
    $adapters = @($adapters | Where-Object { $Clients -contains $_.id })
}

$backedUp = @{}
$shouldWrite = { param($path) $PSCmdlet.ShouldProcess($path, $(if ($Remove) { "remove MCP server $serverName" } else { "register MCP server $serverName" })) }
$reports = foreach ($a in $adapters) {
    Invoke-ClientAdapter -A $a -Name $serverName -Launcher $launcher -Remove ([bool]$Remove) -AnyInstall ([bool]$IncludeOtherInstalls) -ShouldWrite $shouldWrite -BackedUp $backedUp
}
$reports = @($reports)
# Предупреждение: открытый клиент может сохранить свою конфигурацию из памяти и молча стереть запись уже после этого отчёта.
$warnings = @()
try {
    $running = @(Get-Process -Name 'claude', 'codex', 'kimi', 'agy', 'Antigravity' -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
    if ($running.Count -gt 0) { $warnings += "agent client processes are running ($($running -join ', ')): an open client may rewrite its configuration from memory and silently drop the entry; close the clients before changing registration, restart them afterwards and rerun this script (status already confirms the entry survived)" }
} catch { }
$report = [ordered]@{
    action = if ($Remove) { 'remove' } else { 'register' }
    whatIf = [bool]$WhatIfPreference
    server = $serverName
    launcher = $launcher
    warnings = $warnings
    clients = $reports
}
Write-Output ($report | ConvertTo-Json -Depth 6)
$errors = @($reports | Where-Object { $_.status -eq 'error' })
exit $(if ($errors.Count -eq 0) { 0 } else { 1 })
