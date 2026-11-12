<# Обновление = установка из нового источника; предыдущая версия сохраняется как previous.
 discovery.json переписывается под новую версию. -RegisterClients: идемпотентно (пере)прописать MCP-сервер widgets-bridge в клиентах. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'),
    [switch]$RegisterClients,
    [string]$ClientsHomeDir
)
$installArgs = @{ SourceRoot = $SourceRoot; InstallRoot = $InstallRoot }
if ($RegisterClients) { $installArgs.RegisterClients = $true }
if ($ClientsHomeDir) { $installArgs.ClientsHomeDir = $ClientsHomeDir }
& "$PSScriptRoot\Install-Bridge.ps1" @installArgs
exit $LASTEXITCODE
