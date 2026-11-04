param([Parameter(Mandatory=$true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$full = [IO.Path]::GetFullPath($OutDir)
# Isolated output only: absolute, outside widget sources, never an installed widget directory.
if (-not [IO.Path]::IsPathRooted($OutDir)) { throw 'OutDir must be absolute' }
if ($full.StartsWith((Join-Path $repo 'windows'), [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath (Join-Path $full 'LimitsWidget.exe'))) { throw 'Use an isolated output directory' }
New-Item -ItemType Directory -Force -Path $full | Out-Null
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { Join-Path $fw $_ }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { Join-Path $fw "WPF\$_" }
$src = Get-ChildItem -LiteralPath (Join-Path $repo 'windows\limits-widget\src') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName }
$src += Join-Path $repo 'telemetry\publisher-shared\TelemetrySnapshotWriter.cs'
$src += Join-Path $PSScriptRoot 'QuotasHarness.cs'
$exe = Join-Path $full 'QuotasHarness.exe'
& (Join-Path $fw 'csc.exe') @('/nologo','/target:exe','/main:LimitsWidget.QuotasHarness','/langversion:5',"/out:$exe") ($refs | ForEach-Object { "/r:$_" }) $src
if ($LASTEXITCODE -ne 0) { throw 'Harness compilation failed' }
& $exe $full
if ($LASTEXITCODE -ne 0) { throw 'Harness failed' }
python (Join-Path $PSScriptRoot 'validate.py') $full
if ($LASTEXITCODE -ne 0) { throw 'Contract validation failed' }
