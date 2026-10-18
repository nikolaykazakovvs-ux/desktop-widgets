<# Builds the widget-only installer. No downloads, sensor applications or drivers.
   Embeds config.example.json, never local config.json. Does not install or start anything. #>
param([switch]$SkipWidgetBuild)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework x64 compiler is required.' }
if (-not $SkipWidgetBuild) { & (Join-Path $root 'build.ps1') }
$out = Join-Path $root 'ResourcesWidgetSetup-1.1.0-x64.exe'
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll') | ForEach-Object { Join-Path $fw $_ }
$resources = @(
    @((Join-Path $root 'ResourcesWidget.exe'), 'payload.ResourcesWidget.exe'),
    @((Join-Path $root 'config.example.json'), 'payload.config.json'),
    @((Join-Path $here 'payload\THIRD-PARTY.txt'), 'payload.THIRD-PARTY.txt')
) | ForEach-Object { "/resource:$($_[0]),$($_[1])" }
$src = Get-ChildItem -LiteralPath (Join-Path $here 'src') -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName }
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', "/out:$out", "/win32manifest:$(Join-Path $here 'setup.manifest')", "/win32icon:$(Join-Path $root 'assets\ResourcesWidget.ico')") + ($refs | ForEach-Object { "/r:$_" }) + $resources + $src
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compiler exit code: $LASTEXITCODE" }
Write-Host "OK: $out"
