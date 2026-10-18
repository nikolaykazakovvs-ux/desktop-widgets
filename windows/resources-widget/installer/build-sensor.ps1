param([Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$zip = Join-Path $PayloadDirectory 'LibreHardwareMonitor.zip'
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne '086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001') { throw 'Pinned LHM release checksum mismatch.' }
$stage = Join-Path $here ('bin/build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries) {
        if (@('LibreHardwareMonitorLib.dll','System.Memory.dll','RAMSPDToolkit-NDD.dll','HidSharp.dll','DiskInfoToolkit.dll','System.Runtime.CompilerServices.Unsafe.dll','BlackSharp.Core.dll','System.Numerics.Vectors.dll','System.Buffers.dll') -notcontains $entry.FullName) { continue }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $stage $entry.Name), $false)
    }
} finally { $archive.Dispose() }
$fw = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Management.dll') | ForEach-Object { Join-Path $fw $_ }
$refs += Join-Path $stage 'LibreHardwareMonitorLib.dll'
$source = @(Get-ChildItem -LiteralPath (Join-Path $here 'helper') -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName }) + (Join-Path $here 'shared/SensorSecurity.cs')
& (Join-Path $fw 'csc.exe') @('/nologo','/target:exe','/platform:x64','/optimize+','/codepage:65001',"/out:$(Join-Path $stage 'ResourcesWidgetSensor.exe')") ($refs | ForEach-Object { "/r:$_" }) $source
if ($LASTEXITCODE -ne 0) { throw 'CPU sensor helper compilation failed.' }
Copy-Item -LiteralPath (Join-Path $here 'helper/ResourcesWidgetSensor.exe.config') -Destination $stage
$sensorZip = Join-Path $here 'SensorHelper.zip'
if (Test-Path -LiteralPath $sensorZip) { Remove-Item -LiteralPath $sensorZip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $sensorZip -CompressionLevel Optimal
Write-Host "Local read-only helper built: $stage"
