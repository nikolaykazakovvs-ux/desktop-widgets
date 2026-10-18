<# Validates release archives, installer extraction and network persistence without installing.
   Results are saved outside the release assets. No widget UI is launched. #>
param([Parameter(Mandatory=$true)][string]$ArtifactsDirectory, [Parameter(Mandatory=$true)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $results) { throw 'Use a new results directory.' }
New-Item -ItemType Directory -Path $results | Out-Null
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
foreach ($line in Get-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt')) {
    Assert ($line -match '^([a-f0-9]{64})  (.+)$') 'Malformed checksum line.'
    Assert ((Get-FileHash -LiteralPath (Join-Path $artifacts $Matches[2]) -Algorithm SHA256).Hash -eq $Matches[1]) 'Release checksum mismatch.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($archive in Get-ChildItem -LiteralPath $artifacts -Filter '*.zip') {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        $folder = if ($archive.Name.StartsWith('ResourcesWidget-')) { 'resources-widget' } else { 'limits-widget' }
        $exe = if ($folder -eq 'resources-widget') { 'ResourcesWidget.exe' } else { 'LimitsWidget.exe' }
        $allowed = @("$folder/$exe", "$folder/config.json", "$folder/config.example.json", "$folder/LICENSE", "$folder/START-HERE.txt")
        if ($folder -eq 'resources-widget') { $allowed += "$folder/THIRD-PARTY.txt" }
        Assert ($names.Count -eq $allowed.Count) 'Unexpected file count in portable archive.'
        foreach ($name in $names) { Assert ($allowed -contains $name) "Unexpected archive file: $name" }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $results
}
$extract = Join-Path $results 'installer-payload'
$setup = Join-Path $artifacts 'ResourcesWidgetSetup-1.1.0-x64.exe'
$p = Start-Process -FilePath $setup -ArgumentList @('--extract', ('"' + $extract + '"')) -WindowStyle Hidden -PassThru -Wait
Assert ($p.ExitCode -eq 0) 'Installer payload extraction failed.'
Assert (@(Get-ChildItem -LiteralPath $extract -File).Count -eq 3) 'Unexpected installer resources.'
foreach ($name in @('ResourcesWidget.exe','config.json')) {
    Assert ((Get-FileHash -LiteralPath (Join-Path $extract $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $results ('resources-widget/' + $name))).Hash) "Setup/portable mismatch: $name"
}
$cfg = Get-Content -LiteralPath (Join-Path $extract 'config.json') -Raw | ConvertFrom-Json
Assert ($cfg.pingHost -eq '1.1.1.1' -and $cfg.lhmUrl -eq 'http://127.0.0.1:8085/data.json') 'Network or sensor defaults were lost.'
$cfg = Get-Content -LiteralPath (Join-Path $results 'limits-widget/config.json') -Raw | ConvertFrom-Json
Assert (-not $cfg.allowLocalTokens) 'Portable limits config enables local credentials.'
foreach ($provider in $cfg.services.PSObject.Properties) { Assert (-not $provider.Value.enabled) 'Portable limits provider is enabled.' }
$before = (Get-FileHash -LiteralPath (Join-Path $extract 'config.json')).Hash
$p = Start-Process -FilePath $setup -ArgumentList @('--extract', ('"' + $extract + '"')) -WindowStyle Hidden -PassThru -Wait
Assert ($p.ExitCode -eq 1) 'Extraction should refuse an existing directory.'
Assert ((Get-FileHash -LiteralPath (Join-Path $extract 'config.json')).Hash -eq $before) 'Extraction overwrote an existing config.'
foreach ($flag in @('--sensor','--with-sensor','--sensor-remove')) {
    $forbidden = Join-Path $results 'sensor-must-not-install'
    $p = Start-Process -FilePath $setup -ArgumentList @($flag,'--quiet','--dir',('"' + $forbidden + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 3 -and -not (Test-Path -LiteralPath $forbidden)) 'Sensor helper did not fail closed before installation.'
    $p = Start-Process -FilePath $setup -ArgumentList @($flag,'--quiet','--extract',('"' + $forbidden + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 3 -and -not (Test-Path -LiteralPath $forbidden)) 'Mixed sensor/extract flags did not fail closed before file writes.'
}
$fw = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll') | ForEach-Object { Join-Path $fw $_ }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { Join-Path $fw ('WPF/' + $_) }
$testExe = Join-Path $results 'NetworkJournalTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'windows/resources-widget/src') -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
$argsCsc = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/main:NetworkJournalTests',"/out:$testExe") + ($refs | ForEach-Object { "/r:$_" }) + $sources + (Join-Path $PSScriptRoot 'tests/NetworkJournalTests.cs')
& (Join-Path $fw 'csc.exe') @argsCsc
Assert ($LASTEXITCODE -eq 0) 'Network integration test compilation failed.'
& $testExe (Join-Path $results 'synthetic-network-log')
Assert ($LASTEXITCODE -eq 0) 'Network integration test failed.'
$installerRoot = Join-Path $root 'windows/resources-widget/installer'
$installerTest = Join-Path $results 'InstallerFilesystemTests.exe'
$installerRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll') | ForEach-Object { Join-Path $fw $_ }
$installerSources = @(Get-ChildItem -LiteralPath (Join-Path $installerRoot 'src') -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
$installerArgs = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/main:InstallerFilesystemTests',"/out:$installerTest") + ($installerRefs | ForEach-Object { "/r:$_" }) + @(
    "/resource:$(Join-Path $extract 'ResourcesWidget.exe'),payload.ResourcesWidget.exe",
    "/resource:$(Join-Path $extract 'config.json'),payload.config.json",
    "/resource:$(Join-Path $extract 'THIRD-PARTY.txt'),payload.THIRD-PARTY.txt"
) + $installerSources + (Join-Path $PSScriptRoot 'tests/InstallerFilesystemTests.cs')
& (Join-Path $fw 'csc.exe') @installerArgs
Assert ($LASTEXITCODE -eq 0) 'Installer filesystem integration test compilation failed.'
& $installerTest (Join-Path $results 'installer-filesystem-scratch')
Assert ($LASTEXITCODE -eq 0) 'Installer filesystem integration test failed.'
foreach ($widget in @('resources-widget','limits-widget')) {
    $exe = if ($widget -eq 'resources-widget') { 'ResourcesWidget.exe' } else { 'LimitsWidget.exe' }
    $dump = Join-Path $results ($widget + '-dump.json')
    $p = Start-Process -FilePath (Join-Path $results ($widget + '/' + $exe)) -ArgumentList @('--dump', ('"' + $dump + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $dump)) "Headless smoke failed: $widget"
    Get-Content -LiteralPath $dump -Raw | ConvertFrom-Json | Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $results ($widget + '/state.json')))) 'Headless smoke wrote runtime state.'
}
$report = 'PASS: SHA256SUMS; portable allowlists; sanitized configs; embedded payload identity; safe extraction and overwrite refusal; sensor flags fail closed; synthetic network events and journal reload; installer ownership/shared/root/reparse protections and real file install/upgrade/removal preserve user sentinels/config/state/logs; both packaged binaries headless JSON smoke. Profile integration, real installation/driver/UI and profile uninstall were not executed.'
[IO.File]::WriteAllText((Join-Path $results 'validation.txt'), $report, [Text.UTF8Encoding]::new($false))
Write-Host $report
