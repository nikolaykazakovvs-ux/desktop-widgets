<# Validates release archives, manifest/version consistency, reversible updater lifecycle,
   installer extraction and network persistence without installing on the host.
   Supports -Scope All (default), -Scope Limits (v0.2.0 LimitsWidget-1.1.0-win-x64.zip + SHA256SUMS.txt),
   and -Scope Resources. Results are saved outside the release assets. No widget UI is launched. #>
param(
    [Parameter(Mandatory=$true)][string]$ArtifactsDirectory,
    [Parameter(Mandatory=$true)][string]$ResultsDirectory,
    [ValidateSet('All', 'Limits', 'Resources')][string]$Scope = 'All',
    [switch]$LimitsOnly
)
$ErrorActionPreference = 'Stop'
if ($LimitsOnly) { $Scope = 'Limits' }
$testResources = ($Scope -eq 'All' -or $Scope -eq 'Resources')
$testLimits = ($Scope -eq 'All' -or $Scope -eq 'Limits')

$root = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $results) { throw 'Use a new results directory.' }
New-Item -ItemType Directory -Path $results | Out-Null

function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
function Get-LowerSha256([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

$sumsPath = Join-Path $artifacts 'SHA256SUMS.txt'
Assert (Test-Path -LiteralPath $sumsPath -PathType Leaf) 'Missing SHA256SUMS.txt in artifacts directory.'
$sumLines = @(Get-Content -LiteralPath $sumsPath)
Assert ($sumLines.Count -gt 0) 'Empty SHA256SUMS.txt.'
$sumListedNames = @()
foreach ($line in $sumLines) {
    Assert ($line -match '^([a-f0-9]{64})  (.+)$') 'Malformed checksum line.'
    $sumListedNames += $Matches[2]
    Assert ((Get-LowerSha256 (Join-Path $artifacts $Matches[2])) -eq $Matches[1]) "Release checksum mismatch: $($Matches[2])"
}
$actualArtifactFiles = @(Get-ChildItem -LiteralPath $artifacts -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object { $_.Name })
Assert ($actualArtifactFiles.Count -eq $sumListedNames.Count) 'SHA256SUMS.txt entry count does not match artifact file count.'
if ($Scope -eq 'Limits') {
    Assert ($actualArtifactFiles.Count -eq 1 -and $actualArtifactFiles[0] -eq 'LimitsWidget-1.1.0-win-x64.zip') 'Scoped Limits release must carry only LimitsWidget-1.1.0-win-x64.zip + SHA256SUMS.txt.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($archive in Get-ChildItem -LiteralPath $artifacts -Filter '*.zip') {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $names = @($zip.Entries | Where-Object { $_.FullName -notmatch '/$' } | ForEach-Object { $_.FullName.Replace('\','/') })
        $folder = if ($archive.Name.StartsWith('ResourcesWidget-')) { 'resources-widget' } else { 'limits-widget' }
        $exe = if ($folder -eq 'resources-widget') { 'ResourcesWidget.exe' } else { 'LimitsWidget.exe' }
        $allowed = @("$folder/$exe", "$folder/config.json", "$folder/config.example.json", "$folder/LICENSE", "$folder/START-HERE.txt")
        if ($folder -eq 'resources-widget') {
            $allowed += "$folder/THIRD-PARTY.txt"
        } else {
            $allowed += @("$folder/update-limits-widget.ps1", "$folder/package-manifest.json")
        }
        Assert ($names.Count -eq $allowed.Count) "Unexpected file count in portable archive $($archive.Name): got $($names.Count), expected $($allowed.Count)."
        foreach ($name in $names) { Assert ($allowed -contains $name) "Unexpected archive file: $name" }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $results
}

$fw = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'

if ($testResources) {
    $extract = Join-Path $results 'installer-payload'
    $setup = Join-Path $artifacts 'ResourcesWidgetSetup-1.1.1-x64.exe'
    $p = Start-Process -FilePath $setup -ArgumentList @('--extract', ('"' + $extract + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0) 'Installer payload extraction failed.'
    Assert (@(Get-ChildItem -LiteralPath $extract -File).Count -eq 5) 'Unexpected installer resources.'
    foreach ($name in @('ResourcesWidget.exe','config.json')) {
        Assert ((Get-FileHash -LiteralPath (Join-Path $extract $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $results ('resources-widget/' + $name))).Hash) "Setup/portable mismatch: $name"
    }
    $cfg = Get-Content -LiteralPath (Join-Path $extract 'config.json') -Raw | ConvertFrom-Json
    Assert ($cfg.pingHost -eq '1.1.1.1' -and $cfg.lhmUrl -eq 'http://127.0.0.1:8085/data.json') 'Network or sensor defaults were lost.'
    $before = (Get-FileHash -LiteralPath (Join-Path $extract 'config.json')).Hash
    $p = Start-Process -FilePath $setup -ArgumentList @('--extract', ('"' + $extract + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 1) 'Extraction should refuse an existing directory.'
    Assert ((Get-FileHash -LiteralPath (Join-Path $extract 'config.json')).Hash -eq $before) 'Extraction overwrote an existing config.'
    foreach ($flag in @('--sensor','--with-sensor','--sensor-remove')) {
        $forbidden = Join-Path $results 'sensor-must-not-install'
        $p = Start-Process -FilePath $setup -ArgumentList @($flag,'--quiet','--extract',('"' + $forbidden + '"')) -WindowStyle Hidden -PassThru -Wait
        Assert ($p.ExitCode -eq 2 -and -not (Test-Path -LiteralPath $forbidden)) 'Mixed sensor/extract flags did not fail closed before file writes.'
    }
    $p = Start-Process -FilePath $setup -ArgumentList @('--sensor','--dry-run','--quiet') -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0) 'Read-only sensor preflight failed.'
    $refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll') | ForEach-Object { Join-Path $fw $_ }
    $refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { Join-Path $fw ('WPF/' + $_) }
    $testExe = Join-Path $results 'NetworkJournalTests.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'windows/resources-widget/src') -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
    $sources += Join-Path $root 'windows/resources-widget/installer/shared/SensorSecurity.cs'
    $argsCsc = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/main:NetworkJournalTests',"/out:$testExe") + ($refs | ForEach-Object { "/r:$_" }) + $sources + (Join-Path $PSScriptRoot 'tests/NetworkJournalTests.cs')
    & (Join-Path $fw 'csc.exe') @argsCsc
    Assert ($LASTEXITCODE -eq 0) 'Network integration test compilation failed.'
    & $testExe (Join-Path $results 'synthetic-network-log')
    Assert ($LASTEXITCODE -eq 0) 'Network integration test failed.'
    $installerRoot = Join-Path $root 'windows/resources-widget/installer'
    $installerTest = Join-Path $results 'InstallerFilesystemTests.exe'
    $installerRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll','Microsoft.CSharp.dll') | ForEach-Object { Join-Path $fw $_ }
    $installerSources = @(Get-ChildItem -LiteralPath (Join-Path $installerRoot 'src') -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
    $installerSources += Join-Path $installerRoot 'shared/SensorSecurity.cs'
    $installerSources += Join-Path $root 'windows/resources-widget/src/Sensors/LocalSensorSnapshot.cs'
    $installerArgs = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/main:InstallerFilesystemTests',"/out:$installerTest") + ($installerRefs | ForEach-Object { "/r:$_" }) + @(
        "/resource:$(Join-Path $extract 'ResourcesWidget.exe'),payload.ResourcesWidget.exe",
        "/resource:$(Join-Path $extract 'config.json'),payload.config.json",
        "/resource:$(Join-Path $extract 'THIRD-PARTY.txt'),payload.THIRD-PARTY.txt"
    ) + $installerSources + (Join-Path $PSScriptRoot 'tests/InstallerFilesystemTests.cs')
    & (Join-Path $fw 'csc.exe') @installerArgs
    Assert ($LASTEXITCODE -eq 0) 'Installer filesystem integration test compilation failed.'
    & $installerTest (Join-Path $results 'installer-filesystem-scratch')
    Assert ($LASTEXITCODE -eq 0) 'Installer filesystem integration test failed.'
    $contractExe = Join-Path $results 'LocalSensorContractTests.exe'
    $contractSources = @((Join-Path $PSScriptRoot 'tests/LocalSensorContractTests.cs'), (Join-Path $installerRoot 'shared/SensorSecurity.cs'), (Join-Path $installerRoot 'src/Authenticode.cs'), (Join-Path $root 'windows/resources-widget/src/Sensors/LocalSensorSnapshot.cs'))
    & (Join-Path $fw 'csc.exe') @('/nologo','/utf8output','/target:exe','/platform:x64','/codepage:65001',"/out:$contractExe") ($installerRefs | ForEach-Object { "/r:$_" }) $contractSources
    Assert ($LASTEXITCODE -eq 0) 'Local sensor contract tests compilation failed.'
    & $contractExe (Join-Path $extract 'PawnIO_setup.exe')
    Assert ($LASTEXITCODE -eq 0) 'Local sensor contract tests failed.'
    Expand-Archive -LiteralPath (Join-Path $extract 'SensorHelper.zip') -DestinationPath (Join-Path $results 'helper')
    Assert (-not (Test-Path -LiteralPath (Join-Path $results 'helper/LibreHardwareMonitor.exe'))) 'GUI/web-server application accidentally included.'
    $helperProbe = Start-Process -FilePath (Join-Path $results 'helper/ResourcesWidgetSensor.exe') -ArgumentList '--probe' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $results 'helper-probe.json') -RedirectStandardError (Join-Path $results 'helper-probe-error.txt')
    Assert ($helperProbe.ExitCode -eq 0 -or $helperProbe.ExitCode -eq 3) 'Read-only helper library load/probe failed.'
    Get-Content -LiteralPath (Join-Path $results 'helper-probe.json') -Raw | ConvertFrom-Json | Out-Null

    $resDump = Join-Path $results 'resources-widget-dump.json'
    $p = Start-Process -FilePath (Join-Path $results 'resources-widget/ResourcesWidget.exe') -ArgumentList @('--dump', ('"' + $resDump + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $resDump)) 'Headless smoke failed: resources-widget'
    Get-Content -LiteralPath $resDump -Raw | ConvertFrom-Json | Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $results 'resources-widget/state.json'))) 'Headless smoke wrote runtime state.'
}

if ($testLimits) {
    $limitsDir = Join-Path $results 'limits-widget'
    $limitsZip = Join-Path $artifacts 'LimitsWidget-1.1.0-win-x64.zip'
    Assert (Test-Path -LiteralPath $limitsZip -PathType Leaf) 'Missing LimitsWidget-1.1.0-win-x64.zip.'

    # 1. Config sanitization & default opt-in verification
    $cfgRaw = Get-Content -LiteralPath (Join-Path $limitsDir 'config.json') -Raw
    $cfg = $cfgRaw | ConvertFrom-Json
    Assert (-not $cfg.allowLocalTokens) 'Portable limits config enables local credentials.'
    foreach ($svcName in @('claude', 'kimi', 'antigravity', 'codex')) {
        $prop = $cfg.services.PSObject.Properties[$svcName]
        Assert ($null -ne $prop -and $prop.Value.enabled -eq $false) "Portable limits provider '$svcName' is not disabled by default."
        Assert (-not $prop.Value.PSObject.Properties['command']) "Portable limits provider '$svcName' ships a custom command."
    }
    Assert ((Get-LowerSha256 (Join-Path $limitsDir 'config.json')) -eq (Get-LowerSha256 (Join-Path $limitsDir 'config.example.json'))) 'Packaged config.json and config.example.json differ.'

    # 2. Manifest & version consistency verification
    $manifest = Get-Content -LiteralPath (Join-Path $limitsDir 'package-manifest.json') -Raw | ConvertFrom-Json
    Assert ($manifest.schemaVersion -eq '1.0' -and $manifest.product -eq 'LimitsWidget') 'Invalid package-manifest.json identity.'
    Assert ($manifest.version -eq '1.1.0' -and $manifest.releaseTag -eq 'v0.2.0' -and $manifest.archiveName -eq 'LimitsWidget-1.1.0-win-x64.zip') 'Invalid package-manifest.json version or archive metadata.'
    $exeInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $limitsDir 'LimitsWidget.exe'))
    Assert ($exeInfo.ProductName -eq 'LimitsWidget') 'LimitsWidget.exe ProductName mismatch.'
    Assert ($exeInfo.FileVersion -eq '1.1.0.0' -and $manifest.executableVersion -eq '1.1.0.0') "LimitsWidget.exe version ($($exeInfo.FileVersion)) or manifest executableVersion ($($manifest.executableVersion)) is not 1.1.0.0."
    $startHere = Get-Content -LiteralPath (Join-Path $limitsDir 'START-HERE.txt') -Raw
    Assert ($startHere.Contains('1.1.0') -and -not $startHere.Contains('1.0.0')) 'START-HERE.txt version header mismatch.'
    foreach ($entry in $manifest.files) {
        $f = Join-Path $limitsDir ([string]$entry.path)
        Assert (Test-Path -LiteralPath $f -PathType Leaf) "Missing manifest file: $($entry.path)"
        Assert ((Get-LowerSha256 $f) -eq ([string]$entry.sha256).ToLowerInvariant()) "Manifest SHA-256 mismatch: $($entry.path)"
        Assert ((Get-Item -LiteralPath $f -Force).Length -eq [int64]$entry.size) "Manifest size mismatch: $($entry.path)"
    }

    # 3. Headless smoke test (offline, all providers disabled, no state.json written)
    $limitsDump = Join-Path $results 'limits-widget-dump.json'
    $limitsCfgPath = Join-Path $limitsDir 'config.json'
    $limitsStatePath = Join-Path $limitsDir 'state.json'
    $p = Start-Process -FilePath (Join-Path $limitsDir 'LimitsWidget.exe') -ArgumentList @('--config', ('"' + $limitsCfgPath + '"'), '--state', ('"' + $limitsStatePath + '"'), '--dump', ('"' + $limitsDump + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $limitsDump)) 'Headless smoke failed: limits-widget'
    $dumpObj = Get-Content -LiteralPath $limitsDump -Raw | ConvertFrom-Json
    Assert ($null -ne $dumpObj.generatedAt -and @($dumpObj.services).Count -eq 0) 'Headless dump with all providers disabled must not query any service.'
    Assert (-not (Test-Path -LiteralPath $limitsStatePath)) 'Headless smoke wrote runtime state.json.'

    # 4. Compile helper for NTFS junctions and isolated synthetic process tree tests
    $updaterHelperExe = Join-Path $results 'LimitsUpdaterTests.exe'
    & (Join-Path $fw 'csc.exe') @('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/main:LimitsUpdaterTests',"/out:$updaterHelperExe",(Join-Path $PSScriptRoot 'tests/LimitsUpdaterTests.cs'))
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $updaterHelperExe)) 'LimitsUpdaterTests.exe compilation failed.'

    $updaterScript = Join-Path $limitsDir 'update-limits-widget.ps1'
    $scratch = Join-Path $results 'limits-updater-scratch'
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    function Invoke-UpdaterExpectFail([hashtable]$BoundParams, [string]$Reason) {
        $failed = $false
        try {
            & $updaterScript @BoundParams | Out-Null
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        } catch {
            $failed = $true
        }
        Assert $failed "Expected updater rejection for: $Reason"
    }

    # 4a. Path, root, UNC, reserved folder, git checkout, and unowned populated directory rejection
    $volRoot = [IO.Path]::GetPathRoot($scratch)
    Invoke-UpdaterExpectFail @{ TargetPath = $volRoot; PackagePath = $limitsZip; DryRun = $true } 'volume root'
    Invoke-UpdaterExpectFail @{ TargetPath = ($volRoot.Substring(0, 2) + 'relative'); PackagePath = $limitsZip; DryRun = $true } 'drive-relative path'
    Invoke-UpdaterExpectFail @{ TargetPath = 'relative-folder'; PackagePath = $limitsZip; DryRun = $true } 'relative path'
    Invoke-UpdaterExpectFail @{ TargetPath = '\\localhost\share\limits-widget'; PackagePath = $limitsZip; DryRun = $true } 'UNC path'
    Invoke-UpdaterExpectFail @{ TargetPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile); PackagePath = $limitsZip; DryRun = $true } 'UserProfile root'
    Invoke-UpdaterExpectFail @{ TargetPath = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) 'Downloads'); PackagePath = $limitsZip; DryRun = $true } 'Downloads root'
    Invoke-UpdaterExpectFail @{ TargetPath = [IO.Path]::GetTempPath(); PackagePath = $limitsZip; DryRun = $true } 'Temp root'

    $fakeGitCheckout = Join-Path $scratch 'fake-git-checkout'
    New-Item -ItemType Directory -Path (Join-Path $fakeGitCheckout '.git') -Force | Out-Null
    Invoke-UpdaterExpectFail @{ TargetPath = $fakeGitCheckout; PackagePath = $limitsZip } 'git checkout directory'

    $unownedShared = Join-Path $scratch 'unowned-shared'
    New-Item -ItemType Directory -Path $unownedShared -Force | Out-Null
    $sentinelFile = Join-Path $unownedShared 'important-user-file.txt'
    [IO.File]::WriteAllText($sentinelFile, 'KEEP_SENTINEL', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $unownedShared 'LimitsWidget.exe'), 'not-a-valid-pe-binary', [Text.UTF8Encoding]::new($false))
    Invoke-UpdaterExpectFail @{ TargetPath = $unownedShared; PackagePath = $limitsZip } 'populated unowned directory'
    Assert ([IO.File]::ReadAllText($sentinelFile) -eq 'KEEP_SENTINEL') 'Unowned directory sentinel was modified.'

    # 4b. NTFS junction / reparse point containment guards
    $outsideDir = Join-Path $scratch 'junction-outside-target'
    New-Item -ItemType Directory -Path $outsideDir -Force | Out-Null
    $outsideSentinel = Join-Path $outsideDir 'outside-sentinel.txt'
    [IO.File]::WriteAllText($outsideSentinel, 'OUTSIDE_SAFE', [Text.UTF8Encoding]::new($false))
    $junctionDir = Join-Path $scratch 'junction-install'
    & $updaterHelperExe '--create-junction' $junctionDir $outsideDir
    Assert ($LASTEXITCODE -eq 0) 'Failed to create scratch junction fixture.'
    Invoke-UpdaterExpectFail @{ TargetPath = $junctionDir; PackagePath = $limitsZip } 'junction target directory'
    Invoke-UpdaterExpectFail @{ TargetPath = (Join-Path $junctionDir 'nested'); PackagePath = $limitsZip } 'junction ancestor directory'

    $childJunctionInstall = Join-Path $scratch 'child-junction-install'
    New-Item -ItemType Directory -Path $childJunctionInstall -Force | Out-Null
    Copy-Item -LiteralPath $updaterHelperExe -Destination (Join-Path $childJunctionInstall 'LimitsWidget.exe') -Force
    & $updaterHelperExe '--create-junction' (Join-Path $childJunctionInstall 'src') $outsideDir
    Assert ($LASTEXITCODE -eq 0) 'Failed to create child src junction fixture.'
    Invoke-UpdaterExpectFail @{ TargetPath = $childJunctionInstall; PackagePath = $limitsZip } 'junction inside legacy child src'
    Assert ([IO.File]::ReadAllText($outsideSentinel) -eq 'OUTSIDE_SAFE') 'Outside junction sentinel was modified.'

    # 4c. Package manifest / hash tamper rejection before mutation
    $cleanTarget = Join-Path $scratch 'tamper-check-target'
    Invoke-UpdaterExpectFail @{
        TargetPath            = $cleanTarget
        PackagePath           = $limitsZip
        ExpectedPackageSha256 = ('0' * 64)
    } 'mismatched ExpectedPackageSha256'
    Assert (-not (Test-Path -LiteralPath $cleanTarget)) 'Tampered archive hash check created target directory.'

    $tamperedPkgDir = Join-Path $scratch 'tampered-package-dir'
    Copy-Item -LiteralPath $limitsDir -Destination $tamperedPkgDir -Recurse -Force
    [IO.File]::AppendAllText((Join-Path $tamperedPkgDir 'LICENSE'), "`nTAMPERED", [Text.UTF8Encoding]::new($false))
    Invoke-UpdaterExpectFail @{
        TargetPath  = $cleanTarget
        PackagePath = $tamperedPkgDir
    } 'tampered file hash in package'
    Assert (-not (Test-Path -LiteralPath $cleanTarget)) 'Tampered file check mutated target directory.'

    $extraFilePkgDir = Join-Path $scratch 'extra-file-package-dir'
    Copy-Item -LiteralPath $limitsDir -Destination $extraFilePkgDir -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $extraFilePkgDir 'unexpected.txt'), 'unallowlisted', [Text.UTF8Encoding]::new($false))
    Invoke-UpdaterExpectFail @{
        TargetPath  = $cleanTarget
        PackagePath = $extraFilePkgDir
    } 'unallowlisted extra file in package'

    # 4d. Isolated extraction mode (-ExtractTo) and non-empty directory refusal
    $extractOnlyDir = Join-Path $scratch 'isolated-extract'
    $extractReceiptPath = Join-Path $scratch 'extract-receipt.json'
    & $updaterScript -PackagePath $limitsZip -ExtractTo $extractOnlyDir -ReceiptPath $extractReceiptPath | Out-Null
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $extractReceiptPath)) 'Isolated -ExtractTo failed.'
    $extractReceipt = Get-Content -LiteralPath $extractReceiptPath -Raw | ConvertFrom-Json
    Assert ($extractReceipt.mode -eq 'extract-only' -and $extractReceipt.status -eq 'EXTRACTED_ONLY' -and $extractReceipt.acceptance -eq $false -and $extractReceipt.mutatedTarget -eq $false) 'Extract receipt flags invalid.'
    Invoke-UpdaterExpectFail @{ PackagePath = $limitsZip; ExtractTo = $extractOnlyDir } 'extract into existing non-empty directory'

    # 4e. Simulated legacy installation fixture (with legacy src/build.ps1, unknown config/state fields, and user files)
    $legacyInstall = Join-Path $scratch 'legacy-install'
    New-Item -ItemType Directory -Path $legacyInstall -Force | Out-Null
    Copy-Item -LiteralPath $updaterHelperExe -Destination (Join-Path $legacyInstall 'LimitsWidget.exe') -Force
    $oldExeHash = Get-LowerSha256 (Join-Path $legacyInstall 'LimitsWidget.exe')

    $customConfigJson = @'
{
  "allowLocalTokens": false,
  "refreshSeconds": 180,
  "hotkey": "Ctrl+Alt+L",
  "customUserRootField": "preserve-root-value",
  "order": ["claude", "codex", "kimi", "antigravity"],
  "services": {
    "claude": {
      "enabled": true,
      "plan": "Max 20x",
      "customServiceField": 42
    },
    "kimi": {
      "enabled": true,
      "customKimiNote": "keep-kimi"
    },
    "antigravity": {
      "enabled": false
    },
    "codex": {
      "enabled": true
    }
  }
}
'@
    $customStateJson = @'
{
  "left": 140,
  "top": 95,
  "topmost": true,
  "theme": "dark",
  "unknownStateRootField": "keep-state-root",
  "unknownNestedObject": {
    "flag": true,
    "values": [10, 20, 30]
  },
  "creditLog": {
    "antigravity": [[1762000000, 120.5]]
  }
}
'@
    [IO.File]::WriteAllText((Join-Path $legacyInstall 'config.json'), $customConfigJson, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $legacyInstall 'state.json'), $customStateJson, [Text.UTF8Encoding]::new($false))
    $expectedConfigHash = Get-LowerSha256 (Join-Path $legacyInstall 'config.json')
    $expectedStateHash = Get-LowerSha256 (Join-Path $legacyInstall 'state.json')

    foreach ($dirName in @('src', 'assets', 'tools', 'fixtures')) {
        $d = Join-Path $legacyInstall $dirName
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $d 'legacy-item.txt'), "LEGACY_$dirName", [Text.UTF8Encoding]::new($false))
    }
    $userNoteName = [Regex]::Unescape('\u041e\u043f\u0438\u0441\u0430\u043d\u0438\u0435 \u043f\u0440\u043e\u0435\u043a\u0442\u0430.txt')
    [IO.File]::WriteAllText((Join-Path $legacyInstall 'build.ps1'), '# legacy build script', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $legacyInstall 'app.manifest'), '<assembly/>', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $legacyInstall 'README.md'), '# legacy readme', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $legacyInstall $userNoteName), 'USER_NOTE_KEEP', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $legacyInstall 'widget.log'), '2026-11-04 log line', [Text.UTF8Encoding]::new($false))

    # 4f. Dry-run verification on legacy install
    $dryRunReceiptPath = Join-Path $scratch 'dryrun-receipt.json'
    & $updaterScript -TargetPath $legacyInstall -PackagePath $limitsZip -DryRun -ReceiptPath $dryRunReceiptPath | Out-Null
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $dryRunReceiptPath)) 'Dry-run failed.'
    $dryRunReceipt = Get-Content -LiteralPath $dryRunReceiptPath -Raw | ConvertFrom-Json
    Assert ($dryRunReceipt.mode -eq 'dry-run' -and $dryRunReceipt.acceptance -eq $false -and $dryRunReceipt.mutatedTarget -eq $false) 'Dry-run receipt flags invalid.'
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'LimitsWidget.exe')) -eq $oldExeHash) 'Dry-run modified LimitsWidget.exe.'
    Assert (Test-Path -LiteralPath (Join-Path $legacyInstall 'src/legacy-item.txt')) 'Dry-run moved legacy src.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $legacyInstall '.limits-widget-backups'))) 'Dry-run created backup directory.'

    # 4g. Failure injection and automatic rollback across all stages
    foreach ($stage in @('AfterStopBeforeMutate', 'AfterArchiveLegacy', 'MidFileUpdate', 'AfterFileUpdateValidation')) {
        $failReceiptPath = Join-Path $scratch ("fail-$stage-receipt.json")
        Invoke-UpdaterExpectFail @{
            TargetPath    = $legacyInstall
            PackagePath   = $limitsZip
            InjectFailure = $stage
            ReceiptPath   = $failReceiptPath
        } "failure injection $stage"
        $failReceipt = Get-Content -LiteralPath $failReceiptPath -Raw | ConvertFrom-Json
        Assert ($failReceipt.status -eq 'ROLLED_BACK_ON_FAILURE' -and $failReceipt.mutatedTarget -eq $false) "Rollback receipt not written for $stage."
        Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'LimitsWidget.exe')) -eq $oldExeHash) "Rollback failed to restore LimitsWidget.exe after $stage."
        Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'config.json')) -eq $expectedConfigHash) "Rollback failed to preserve config.json after $stage."
        Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'state.json')) -eq $expectedStateHash) "Rollback failed to preserve state.json after $stage."
        foreach ($legacyName in @('src/legacy-item.txt', 'assets/legacy-item.txt', 'tools/legacy-item.txt', 'fixtures/legacy-item.txt', 'build.ps1', 'app.manifest', 'README.md', $userNoteName, 'widget.log')) {
            Assert (Test-Path -LiteralPath (Join-Path $legacyInstall $legacyName)) "Rollback lost '$legacyName' after $stage."
        }
    }

    # 4h. Exact target process & child tree termination vs foreign process preservation
    $foreignDir = Join-Path $scratch 'foreign-running-widget'
    New-Item -ItemType Directory -Path $foreignDir -Force | Out-Null
    $foreignExe = Join-Path $foreignDir 'LimitsWidget.exe'
    Copy-Item -LiteralPath $updaterHelperExe -Destination $foreignExe -Force
    $ownedChildExe = Join-Path $legacyInstall 'owned-child-helper.exe'
    Copy-Item -LiteralPath $updaterHelperExe -Destination $ownedChildExe -Force
    $childPidFile = Join-Path $legacyInstall 'owned-child.pid'

    $foreignProc = $null
    $ownedProc = $null
    $ownedChildPid = $null
    try {
        $foreignProc = Start-Process -FilePath $foreignExe -ArgumentList @('--sleep', '120') -WindowStyle Hidden -PassThru
        $ownedProc = Start-Process -FilePath (Join-Path $legacyInstall 'LimitsWidget.exe') -ArgumentList @('--sleep', '120', '--spawn-child', ('"' + $ownedChildExe + '"'), ('"' + $childPidFile + '"')) -WindowStyle Hidden -PassThru
        $waitDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while (-not (Test-Path -LiteralPath $childPidFile) -and [DateTime]::UtcNow -lt $waitDeadline) {
            Start-Sleep -Milliseconds 100
        }
        Assert (Test-Path -LiteralPath $childPidFile) 'Owned child process PID file was not created.'
        $ownedChildPid = [int]([IO.File]::ReadAllText($childPidFile).Trim())

        $updateReceiptPath = Join-Path $scratch 'update-receipt.json'
        & $updaterScript -TargetPath $legacyInstall -PackagePath $limitsZip -ReceiptPath $updateReceiptPath | Out-Null
        Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $updateReceiptPath)) 'Live update on scratch legacy install failed.'
        $updateReceipt = Get-Content -LiteralPath $updateReceiptPath -Raw | ConvertFrom-Json
        Assert ($updateReceipt.status -eq 'UPDATED' -and $updateReceipt.acceptance -eq $false -and $updateReceipt.mutatedTarget -eq $true) 'Update receipt status invalid.'
        Assert ($ownedProc.WaitForExit(5000)) 'Target LimitsWidget.exe was not stopped by updater.'
        $childStillRunning = Get-Process -Id $ownedChildPid -ErrorAction SilentlyContinue
        Assert ($null -eq $childStillRunning -or $childStillRunning.HasExited) 'Owned child process tree was not stopped by updater.'
        Assert (-not $foreignProc.HasExited) 'Foreign LimitsWidget.exe in another directory was killed by updater!'
    } finally {
        if ($null -ne $foreignProc -and -not $foreignProc.HasExited) { Stop-Process -Id $foreignProc.Id -Force -ErrorAction SilentlyContinue }
        if ($null -ne $ownedProc -and -not $ownedProc.HasExited) { Stop-Process -Id $ownedProc.Id -Force -ErrorAction SilentlyContinue }
        if ($null -ne $ownedChildPid) { Stop-Process -Id $ownedChildPid -Force -ErrorAction SilentlyContinue }
    }

    # Verify post-update state of $legacyInstall
    $packagedExeHash = Get-LowerSha256 (Join-Path $limitsDir 'LimitsWidget.exe')
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'LimitsWidget.exe')) -eq $packagedExeHash) 'Updated LimitsWidget.exe hash does not match package.'
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'config.json')) -eq $expectedConfigHash) 'Update modified user config.json.'
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'state.json')) -eq $expectedStateHash) 'Update modified user state.json.'
    $preservedCfg = Get-Content -LiteralPath (Join-Path $legacyInstall 'config.json') -Raw | ConvertFrom-Json
    Assert ($preservedCfg.customUserRootField -eq 'preserve-root-value' -and $preservedCfg.services.claude.enabled -eq $true -and $preservedCfg.services.claude.customServiceField -eq 42) 'Unknown or user fields in config.json were lost.'
    $preservedState = Get-Content -LiteralPath (Join-Path $legacyInstall 'state.json') -Raw | ConvertFrom-Json
    Assert ($preservedState.unknownStateRootField -eq 'keep-state-root' -and $preservedState.unknownNestedObject.flag -eq $true) 'Unknown fields in state.json were lost.'
    Assert ([IO.File]::ReadAllText((Join-Path $legacyInstall $userNoteName)) -eq 'USER_NOTE_KEEP') 'Unknown user file was lost during update.'

    # Verify legacy items were safely archived rather than destroyed
    foreach ($legacyName in @('src', 'assets', 'tools', 'fixtures', 'build.ps1', 'app.manifest', 'README.md')) {
        Assert (-not (Test-Path -LiteralPath (Join-Path $legacyInstall $legacyName))) "Legacy item '$legacyName' remained in install root after archiving."
        Assert (Test-Path -LiteralPath (Join-Path $updateReceipt.archivedLegacyPath $legacyName)) "Legacy item '$legacyName' missing from archive folder."
    }

    # 4i. Explicit rollback (-RollbackFromBackup latest) verification
    $rollbackReceiptPath = Join-Path $scratch 'rollback-receipt.json'
    & $updaterScript -TargetPath $legacyInstall -RollbackFromBackup 'latest' -ReceiptPath $rollbackReceiptPath | Out-Null
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $rollbackReceiptPath)) 'Explicit rollback failed.'
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'LimitsWidget.exe')) -eq $oldExeHash) 'Explicit rollback did not restore previous LimitsWidget.exe.'
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'config.json')) -eq $expectedConfigHash) 'Explicit rollback did not preserve config.json.'
    Assert ((Get-LowerSha256 (Join-Path $legacyInstall 'state.json')) -eq $expectedStateHash) 'Explicit rollback did not preserve state.json.'
    foreach ($legacyName in @('src/legacy-item.txt', 'assets/legacy-item.txt', 'tools/legacy-item.txt', 'fixtures/legacy-item.txt', 'build.ps1', 'app.manifest', 'README.md', $userNoteName, 'widget.log')) {
        Assert (Test-Path -LiteralPath (Join-Path $legacyInstall $legacyName)) "Explicit rollback did not restore '$legacyName'."
    }

    $limitsValidationSummary = [ordered]@{
        scope                         = $Scope
        archive                       = 'LimitsWidget-1.1.0-win-x64.zip'
        archiveSha256                 = Get-LowerSha256 $limitsZip
        executableSha256              = $packagedExeHash
        executableVersion             = $exeInfo.FileVersion
        manifestVersion               = $manifest.version
        releaseTag                    = $manifest.releaseTag
        allowlistedFilesVerified      = $true
        sanitizedDefaultConfig        = $true
        headlessDumpSmokePassed       = $true
        containmentAndJunctionGuards  = $true
        tamperRejectionBeforeMutation = $true
        isolatedExtractModeVerified   = $true
        dryRunModeVerified            = $true
        processTreeOwnershipVerified  = $true
        foreignProcessPreserved       = $true
        unknownConfigStatePreserved   = $true
        legacyItemsArchivedSafely     = $true
        failureInjectionRollbackPass  = @('AfterStopBeforeMutate', 'AfterArchiveLegacy', 'MidFileUpdate', 'AfterFileUpdateValidation')
        explicitRollbackVerified      = $true
        liveAcceptanceClaimed         = $false
        validatedAtUtc                = [DateTime]::UtcNow.ToString('o')
    }
    [IO.File]::WriteAllText((Join-Path $results 'limits-release-validation.json'), ($limitsValidationSummary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

$report = if ($Scope -eq 'Limits') {
    'PASS (Limits v0.2.0): SHA256SUMS.txt; exact LimitsWidget-1.1.0-win-x64.zip allowlist; package-manifest.json and 1.1.0/1.1.0.0 version consistency; sanitized opt-in config.json/config.example.json; packaged EXE offline headless --dump smoke; updater path/root/UNC/reserved/git-checkout/unowned/NTFS-junction containment guards; manifest/hash tamper rejection before mutation; isolated -ExtractTo and -DryRun; exact target EXE + child process tree stop with foreign LimitsWidget preservation; unknown config.json/state.json field preservation; safe legacy src/build.ps1 archiving; 4-stage failure injection auto-rollback and explicit -RollbackFromBackup. No live account calls, host installation, or live runtime acceptance was performed.'
} elseif ($Scope -eq 'Resources') {
    'PASS (Resources): SHA256SUMS; portable allowlists; sanitized configs; embedded payload identity; extraction and mixed-mode refusal; read-only sensor preflight; snapshot/schema/stale/malformed contract and Windows Authz local-allow/NETWORK-deny proof; pinned native WinVerifyTrust; real helper library-load probe; synthetic network journal; installer dedicated ownership/reparse/data-preservation lifecycle; packaged EXE headless smoke.'
} else {
    'PASS: SHA256SUMS; portable allowlists; sanitized configs; package-manifest.json and 1.1.0 version consistency; embedded payload identity; extraction and mixed-mode refusal; read-only sensor preflight; snapshot/schema/stale/malformed contract and Windows Authz local-allow/NETWORK-deny proof; pinned native WinVerifyTrust; real helper library-load probe; synthetic network journal; installer dedicated ownership/reparse/data-preservation lifecycle; reversible limits updater containment/process-tree/legacy-archive/failure-injection/rollback lifecycle; both packaged EXEs headless smoke. No UAC, driver installation, elevated task or installed-profile lifecycle was performed by this validation.'
}
[IO.File]::WriteAllText((Join-Path $results 'validation.txt'), $report, [Text.UTF8Encoding]::new($false))
Write-Host $report
