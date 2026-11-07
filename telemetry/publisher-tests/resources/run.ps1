param([string]$WorkRoot = (Join-Path $PSScriptRoot '..\..\..\.work\telemetry-publisher-resources'), [switch]$CompileOnly, [switch]$LiveSmoke)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$work = [IO.Path]::GetFullPath($WorkRoot)
if (-not [IO.Path]::IsPathRooted($WorkRoot) -or -not $work.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an absolute isolated workspace on D:' }
New-Item -ItemType Directory -Path $work -Force | Out-Null
$run = Join-Path $work ('run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$shared = Join-Path $repo 'telemetry\publisher-shared\TelemetrySnapshotWriter.cs'
$writerExe = Join-Path $run 'WriterTests.exe'
& $csc /nologo /target:exe /langversion:5 /platform:x64 /optimize+ /codepage:65001 "/out:$writerExe" $shared (Join-Path $repo 'telemetry\publisher-tests\shared\WriterTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'shared compile failed' }
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll') | ForEach-Object { '/r:' + (Join-Path $fw $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $fw ('WPF\' + $_)) }
$src = @(Get-ChildItem -LiteralPath (Join-Path $repo 'windows\resources-widget\src') -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })
$src += Join-Path $repo 'windows\resources-widget\installer\shared\SensorSecurity.cs'
$src += $shared
$src += Join-Path $PSScriptRoot 'ResourcesPublisherTests.cs'
$resourceExe = Join-Path $run 'ResourcesPublisherTests.exe'
& $csc /nologo /target:exe /langversion:5 /main:ResourcesPublisherTests /platform:x64 /optimize+ /codepage:65001 "/out:$resourceExe" @refs @src
if ($LASTEXITCODE -ne 0) { throw 'resources compile failed' }
if ($CompileOnly) { Write-Host "COMPILED: $run"; return }
& $writerExe (Join-Path $run 'shared-output')
if ($LASTEXITCODE -ne 0) { throw 'shared assertions failed' }
$junctionTarget = Join-Path $run 'junction-target'
New-Item -ItemType Directory -Path $junctionTarget | Out-Null
$junction = Join-Path $run 'junction'
New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
& $writerExe --reject-path (Join-Path $junction 'escape')
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath (Join-Path $junctionTarget 'escape'))) { throw 'reparse path rejection failed' }
Write-Host 'PASS directory junction escape rejected before directory creation'
& $resourceExe (Join-Path $run 'resource-output')
if ($LASTEXITCODE -ne 0) { throw 'resources assertions failed' }
if ($LiveSmoke) {
    & $resourceExe --live-smoke (Join-Path $run 'live-output')
    if ($LASTEXITCODE -ne 0) { throw 'real sampler integration failed' }
    Get-ChildItem -LiteralPath (Join-Path $run 'live-output') -Filter '*.json' | ForEach-Object {
        & python (Join-Path $repo 'telemetry\tests\contract.py') hardware-snapshot $_.FullName
        if ($LASTEXITCODE -ne 0) { throw 'real sampler contract failed' }
    }
}
Get-ChildItem -LiteralPath (Join-Path $run 'resource-output') -Filter '*.json' | ForEach-Object {
    & python (Join-Path $repo 'telemetry\tests\contract.py') hardware-snapshot $_.FullName --as-of 2026-10-26T00:00:00Z
    if ($LASTEXITCODE -ne 0) { throw ('contract validation failed: ' + $_.Name) }
}
# No sensor/UI/provider constructor runs in the assertions above. Verify normal Loop hook ordering and headless guard.
$sampler = Get-Content -Raw -LiteralPath (Join-Path $repo 'windows\resources-widget\src\Sampler.cs')
if ($sampler -notmatch '(?s)Collect\(true\);\s+if \(telemetry != null\) telemetry.Submit\(s\);\s+var handler = Updated' -or $sampler -notmatch 'if \(!journalReadOnly\)') { throw 'sampler hook/read-only guard smoke failed' }
Write-Host "PASS hook ordering and headless guard. Evidence: $run"
