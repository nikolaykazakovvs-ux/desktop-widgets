<#
.SYNOPSIS Регистрация Bridge в Claude Code через `claude mcp add-json --scope user`.
 -ClaudeConfigDir <dir>  явный каталог (выставляется CLAUDE_CONFIG_DIR только для дочернего процесса).
 Без -ClaudeConfigDir реальный профиль отклоняется без -ConfirmRealHome.
 -PrintOnly  только напечатать команду и JSON.
 Резервная копия: <dir>\.claude.json копируется в .bak-<время> до записи.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [string]$ClaudeConfigDir,
    [switch]$ConfirmRealHome,
    [switch]$PrintOnly
)
$ErrorActionPreference = 'Stop'
$json = [ordered]@{ type = 'stdio'; command = (Join-Path $InstallRoot 'bridge-server.cmd'); args = @() } | ConvertTo-Json -Compress
if ($PrintOnly) { Write-Output "claude mcp add-json --scope user ai_telemetry_bridge '$json'"; return }
if (-not $ClaudeConfigDir -and -not $ConfirmRealHome) { throw 'Specify -ClaudeConfigDir <abs-dir> (or -ConfirmRealHome for the real profile, or -PrintOnly).' }
$old = $env:CLAUDE_CONFIG_DIR
try {
    if ($ClaudeConfigDir) {
        if (-not [IO.Path]::IsPathRooted($ClaudeConfigDir)) { throw 'ClaudeConfigDir must be absolute.' }
        New-Item -ItemType Directory -Force -Path $ClaudeConfigDir | Out-Null
        $env:CLAUDE_CONFIG_DIR = $ClaudeConfigDir
        $cfgDir = $ClaudeConfigDir
    } else { $cfgDir = $env:USERPROFILE }
    $cfg = Join-Path $cfgDir '.claude.json'
    if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination "$cfg.bak-$(Get-Date -Format yyyyMMddHHmmss)" }
    $escapedJson = $json -replace '"', '\"'
    & claude mcp add-json --scope user ai_telemetry_bridge $escapedJson
    if ($LASTEXITCODE -ne 0) { throw "claude mcp add-json failed: exit $LASTEXITCODE" }
} finally { $env:CLAUDE_CONFIG_DIR = $old }
