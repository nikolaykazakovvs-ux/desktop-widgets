<# Обновление = установка из нового источника; предыдущая версия сохраняется как previous. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'))
& "$PSScriptRoot\Install-Bridge.ps1" -SourceRoot $SourceRoot -InstallRoot $InstallRoot
