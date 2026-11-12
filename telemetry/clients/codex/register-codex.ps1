<#
.SYNOPSIS Регистрация Bridge в Codex. По умолчанию ТОЛЬКО печатает TOML-фрагмент.
 -CodexHome <dir>   явная запись в <dir>\config.toml (с резервной копией config.toml.bak-<время>).
 Реальный %USERPROFILE%\.codex отклоняется без -ConfirmRealHome.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [string]$CodexHome,
    [switch]$ConfirmRealHome
)
$ErrorActionPreference = 'Stop'
$cmd = (Join-Path $InstallRoot 'bridge-server.cmd').Replace('\', '\\')
$snippet = @"
[mcp_servers.ai_telemetry_bridge]
command = "$cmd"
args = []
startup_timeout_sec = 20
tool_timeout_sec = 30
"@
if (-not $CodexHome) { Write-Output $snippet; return }
if (-not [IO.Path]::IsPathRooted($CodexHome)) { throw 'CodexHome must be absolute.' }
$real = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.codex')).TrimEnd('\')
if ([IO.Path]::GetFullPath($CodexHome).TrimEnd('\') -ieq $real -and -not $ConfirmRealHome) { throw 'Refusing real Codex home without -ConfirmRealHome.' }
New-Item -ItemType Directory -Force -Path $CodexHome | Out-Null
$cfg = Join-Path $CodexHome 'config.toml'
$text = ''
if (Test-Path -LiteralPath $cfg) {
    $text = Get-Content -LiteralPath $cfg -Raw
    if ($text -match '(?m)^\[mcp_servers\.ai_telemetry_bridge\]') { Write-Output 'already registered; nothing changed'; return }
    Copy-Item -LiteralPath $cfg -Destination "$cfg.bak-$(Get-Date -Format yyyyMMddHHmmss)"
}
$sep = if ($text -and -not $text.EndsWith("`n")) { "`r`n" } else { '' }
[IO.File]::WriteAllText($cfg, $text + $sep + "`r`n" + $snippet + "`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Output "written: $cfg"
