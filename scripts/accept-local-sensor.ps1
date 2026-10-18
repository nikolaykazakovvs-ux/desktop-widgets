<# Parent/operator acceptance only. This script never elevates itself.
   Run once with normal explicit Windows UAC, using a previously hash-reviewed installer.
   Install leaves the sensor active for non-elevated read/render verification.
   LifecycleCleanup tests stop/staleness/start/update/removal and removes only this application's own helper.
   No original widgets/LHM processes/tasks/configurations or shared PawnIO service are changed. #>
param(
    [Parameter(Mandatory=$true)][string]$SetupPath,
    [Parameter(Mandatory=$true)][string]$ExpectedSHA256,
    [Parameter(Mandatory=$true)][string]$UserSid,
    [ValidateSet('Install','LifecycleCleanup','Remove')][string]$Stage = 'Install'
)
$ErrorActionPreference = 'Stop'
function Write-SafeReceipt($data) {
    $safeSid=[Security.Principal.SecurityIdentifier]::new($UserSid)
    if (-not $safeSid.IsAccountSid() -or $safeSid.Value -ne $UserSid -or $safeSid.Value.Split('-').Count -ne 8) { throw 'Invalid receipt SID.' }
    $safeRoot=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'ResourcesWidgetSensor'
    for ($item=[IO.DirectoryInfo]::new($safeRoot); $null -ne $item; $item=$item.Parent) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse point in receipt path.' }
    }
    if (-not (Test-Path -LiteralPath $safeRoot)) {
        $security=[Security.AccessControl.DirectorySecurity]::new()
        $security.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(D;OICI;FA;;;NU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200a9;;;BU)')
        [IO.Directory]::CreateDirectory($safeRoot,$security) | Out-Null
    }
    $acl=Get-Acl -LiteralPath $safeRoot
    $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if (($owner -ne 'S-1-5-18' -and $owner -ne 'S-1-5-32-544') -or -not $acl.AreAccessRulesProtected) { throw 'Untrusted receipt directory owner/inheritance.' }
    $writeMask=[Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    $deniedNetwork=$false
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        $who=$rule.IdentityReference.Value
        if ($rule.AccessControlType -eq 'Deny' -and $who -eq 'S-1-5-2' -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadData)) { $deniedNetwork=$true }
        if ($rule.AccessControlType -eq 'Allow' -and $who -ne 'S-1-5-18' -and $who -ne 'S-1-5-32-544' -and ($rule.FileSystemRights -band $writeMask)) { throw 'Non-admin writable receipt root.' }
    }
    if (-not $deniedNetwork) { throw 'Receipt root must deny NETWORK.' }
    $path=Join-Path $safeRoot ('acceptance-'+$UserSid+'-'+$Stage+'.json')
    if (Test-Path -LiteralPath $path) {
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Receipt must not be a link.' }
        $fileAcl=Get-Acl -LiteralPath $path
        $fileOwner=$fileAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($fileOwner -ne 'S-1-5-18' -and $fileOwner -ne 'S-1-5-32-544') { throw 'Untrusted receipt owner.' }
    }
    $data | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    $fileSecurity=[Security.AccessControl.FileSecurity]::new()
    $fileSecurity.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(D;;FA;;;NU)(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;'+$UserSid+')')
    [IO.File]::SetAccessControl($path,$fileSecurity)
    return $path
}
$sourceLock=$null
$evidence=[ordered]@{stage=$Stage; userSid=$UserSid; setupSHA256=$ExpectedSHA256; beganUtc=[DateTime]::UtcNow.ToString('o'); result='STARTING'}
try {
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Requires normal explicit administrator consent; this script does not bypass or request UAC itself.' }
$sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
if (-not $sid.IsAccountSid() -or $sid.Value -ne $UserSid -or $sid.Value.Split('-').Count -ne 8) { throw 'Invalid intended account SID.' }
$setup = [IO.Path]::GetFullPath($SetupPath)
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $ExpectedSHA256) { throw 'Reviewed installer hash mismatch.' }
# Hold the reviewed image across all actions, including any profile/task operations.
$sourceLock = [IO.FileStream]::new($setup,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$sensorRoot = Join-Path $programFiles 'ResourcesWidgetSensor'
$sensorDir = Join-Path $sensorRoot $UserSid
$snapshot = Join-Path $sensorDir 'temperature.json'
$foreignLhm = @(Get-Process -Name LibreHardwareMonitor -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$pawnBefore = Get-Service -Name PawnIO -ErrorAction SilentlyContinue
$evidence.foreignLhmPids=$foreignLhm
Write-SafeReceipt $evidence | Out-Null
function RunSetup([string]$flag) {
    $process = Start-Process -FilePath $setup -ArgumentList @($flag,'--quiet','--for-user',$UserSid) -WorkingDirectory ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Setup $flag failed with exit $($process.ExitCode). Inspect protected sensor-install.log/temperature.json; no runtime acceptance is claimed." }
}
function FreshSnapshot([DateTime]$after) {
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        if (Test-Path -LiteralPath $snapshot) {
            $data=Get-Content -LiteralPath $snapshot -Raw | ConvertFrom-Json
            $at=[DateTime]::Parse($data.utc).ToUniversalTime()
            if ($data.status -eq 'ok' -and $data.temperatureC -gt 0 -and $data.temperatureC -lt 150 -and $at -ge $after -and ([DateTime]::UtcNow-$at).TotalSeconds -le 5) { return $data }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'No fresh real CPU temperature snapshot.'
}
    # Fixed bounded acceptance actions; the outer catch also covers all runtime preamble failures.
    if ($Stage -eq 'Install') {
        $began=[DateTime]::UtcNow
        RunSetup '--sensor'
        $evidence.snapshot=FreshSnapshot $began
        $processes=@(Get-Process -Name ResourcesWidgetSensor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $sensorDir 'ResourcesWidgetSensor.exe') })
        if ($processes.Count -ne 1) { throw 'Expected exactly one own CPU sensor helper process.' }
        $sensorPid=$processes[0].Id
        $tcp=@(Get-NetTCPConnection -OwningProcess $sensorPid -ErrorAction SilentlyContinue)
        $udp=@(Get-NetUDPEndpoint -OwningProcess $sensorPid -ErrorAction SilentlyContinue)
        if ($tcp.Count -ne 0 -or $udp.Count -ne 0) { throw 'Own helper unexpectedly has a network endpoint.' }
        $evidence.helperPid=$sensorPid
        $evidence.tcpEndpoints=$tcp.Count
        $evidence.udpEndpoints=$udp.Count
        $evidence.snapshotAcl=(Get-Acl -LiteralPath $snapshot).Sddl
        $evidence.taskXml=(& (Join-Path $env:WINDIR 'System32/schtasks.exe') /query /tn ('ResourcesWidgetSensor-'+$UserSid) /xml | Out-String)
    } elseif ($Stage -eq 'LifecycleCleanup') {
        RunSetup '--sensor-stop'
        Start-Sleep -Seconds 6
        $p=Start-Process -FilePath $setup -ArgumentList @('--sensor-status','--quiet','--for-user',$UserSid) -WindowStyle Hidden -PassThru -Wait
        if($p.ExitCode -ne 3) { throw 'Native snapshot reader did not become unavailable after TTL.' }
        $evidence.staleAfterStop=$true
        $began=[DateTime]::UtcNow
        RunSetup '--sensor-start'
        $evidence.restarted=FreshSnapshot $began
        $began=[DateTime]::UtcNow
        RunSetup '--sensor'
        $evidence.upgraded=FreshSnapshot $began
        RunSetup '--sensor-remove'
        $evidence.ownHelperRemoved=-not (Test-Path -LiteralPath (Join-Path $sensorDir 'ResourcesWidgetSensor.exe'))
        if (-not $evidence.ownHelperRemoved) { throw 'Own helper binary survived removal.' }
    } else { RunSetup '--sensor-remove'; $evidence.ownHelperRemoved=$true }
    $foreignAfter=@(Get-Process -Name LibreHardwareMonitor -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    if (($foreignAfter -join ',') -ne ($foreignLhm -join ',')) { throw 'External LHM process set changed during acceptance.' }
    $pawnAfter=Get-Service -Name PawnIO -ErrorAction SilentlyContinue
    if ($pawnBefore -and $pawnAfter.Status -ne $pawnBefore.Status) { throw 'Shared PawnIO service state changed.' }
    $evidence.externalLhmPreserved=$true
    $evidence.pawnState=[string]$pawnAfter.Status
    $evidence.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $evidence.result='PASS'
    $report=Write-SafeReceipt $evidence
    Write-Host "PASS: $Stage. Evidence: $report"
} catch {
    $evidence.result='FAIL'
    $evidence.error=$_.Exception.ToString()
    $evidence.scriptStack=$_.ScriptStackTrace
    $evidence.finishedUtc=[DateTime]::UtcNow.ToString('o')
    try { Write-SafeReceipt $evidence | Out-Null } catch { Write-Warning ('Protected receipt unavailable: '+$_.Exception.Message) }
    throw
} finally { if ($null -ne $sourceLock) { $sourceLock.Dispose() } }
