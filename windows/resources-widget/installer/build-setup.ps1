<# Builds widget and optional protected local sensor; never installs or starts them. #>
param([switch]$SkipWidgetBuild, [string]$PayloadDirectory = (Join-Path $PSScriptRoot 'payload'))
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework x64 compiler is required.' }
if (-not $SkipWidgetBuild) { & (Join-Path $root 'build.ps1') }
$driver = Join-Path $PayloadDirectory 'PawnIO_setup.exe'
if ((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash -ne '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032') { throw 'PawnIO checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $driver
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=namazso\.eu') { throw 'PawnIO signature verification failed.' }
& (Join-Path $here 'build-sensor.ps1') -PayloadDirectory $PayloadDirectory
$out = Join-Path $root 'ResourcesWidgetSetup-1.1.1-x64.exe'
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll','Microsoft.CSharp.dll') | ForEach-Object { Join-Path $fw $_ }
$resources = @(
    @((Join-Path $root 'ResourcesWidget.exe'), 'payload.ResourcesWidget.exe'),
    @((Join-Path $root 'config.example.json'), 'payload.config.json'),
    @((Join-Path $here 'payload\THIRD-PARTY.txt'), 'payload.THIRD-PARTY.txt'),
    @((Join-Path $here 'SensorHelper.zip'), 'payload.SensorHelper.zip'),
    @($driver, 'payload.PawnIO_setup.exe')
) | ForEach-Object { "/resource:$($_[0]),$($_[1])" }
$src = Get-ChildItem -LiteralPath (Join-Path $here 'src') -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName }
$src += Join-Path $here 'shared/SensorSecurity.cs'
$src += Join-Path $root 'src/Sensors/LocalSensorSnapshot.cs'
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', "/out:$out", "/win32manifest:$(Join-Path $here 'setup.manifest')", "/win32icon:$(Join-Path $root 'assets\ResourcesWidget.ico')") + ($refs | ForEach-Object { "/r:$_" }) + $resources + $src
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compiler exit code: $LASTEXITCODE" }
Write-Host "OK: $out"
