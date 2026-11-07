param([string]$WorkRoot = (Join-Path $PSScriptRoot '..\..\..\.work\telemetry-writer-fault'), [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'

# Builds a TEST-ONLY fault-injected copy of the production writer. Hooks are inserted before unique anchors;
# no existing statement is removed or reordered, and the production source is not modified.
function Add-UniqueInjection([string]$Text, [string]$Label, [string]$Anchor, [string]$Replacement) {
    $first = $Text.IndexOf($Anchor, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "fault injection anchor missing: $Label" }
    $second = $Text.IndexOf($Anchor, $first + $Anchor.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) { throw "fault injection anchor not unique: $Label" }
    if (-not $Replacement.EndsWith($Anchor, [StringComparison]::Ordinal)) { throw "fault injection must only prepend: $Label" }
    return $Text.Replace($Anchor, $Replacement)
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$work = [IO.Path]::GetFullPath($WorkRoot)
if (-not [IO.Path]::IsPathRooted($WorkRoot) -or -not $work.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an absolute isolated workspace on D:' }
New-Item -ItemType Directory -Path $work -Force | Out-Null
$run = Join-Path $work ('run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null

$shared = Join-Path $repo 'telemetry\publisher-shared\TelemetrySnapshotWriter.cs'
$writerText = [IO.File]::ReadAllText($shared)
$faultCopy = Join-Path $run 'TelemetrySnapshotWriter.fault.cs'
$probe = 'global::WriterFaultProbe'
$injections = @(
    @{ Label = 'rename_fault'; Anchor = '            int code = SetRename(handle, FileRenameInfoEx, RenameReplaceIfExists | RenamePosixSemantics, targetPath);'; Replacement = "            int injectedFault = $probe.RenameFault(handle, targetPath);`n            if (injectedFault != 0) return injectedFault;`n            int code = SetRename(handle, FileRenameInfoEx, RenameReplaceIfExists | RenamePosixSemantics, targetPath);" },
    @{ Label = 'target_security_fault'; Anchor = '            string wanted = target.GetSecurityDescriptorSddlForm(AccessControlSections.Access);'; Replacement = "            target = $probe.TargetSecurityFault(target, sid);`n            string wanted = target.GetSecurityDescriptorSddlForm(AccessControlSections.Access);" },
    @{ Label = 'temp_attest_fault'; Anchor = '                AssertPrivate(tempSecurity = file.GetAccessControl(), sid);'; Replacement = "                $probe.BeforeTempAttest(file);`n                AssertPrivate(tempSecurity = file.GetAccessControl(), sid);" }
)
$faulted = $writerText
$expectedLength = $writerText.Length
foreach ($injection in $injections) {
    $faulted = Add-UniqueInjection $faulted $injection.Label $injection.Anchor $injection.Replacement
    $expectedLength += ($injection.Replacement.Length - $injection.Anchor.Length)
}
if ($faulted.Length -ne $expectedLength) { throw 'fault copy changed more than the declared additive hooks' }
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[IO.File]::WriteAllText($faultCopy, $faulted, $utf8NoBom)
$manifest = [ordered]@{
    kind = 'shared-writer-fault-injection'
    original_source = 'telemetry/publisher-shared/TelemetrySnapshotWriter.cs'
    original_sha256 = (Get-FileHash -LiteralPath $shared -Algorithm SHA256).Hash.ToLowerInvariant()
    fault_copy = 'TelemetrySnapshotWriter.fault.cs'
    fault_copy_sha256 = (Get-FileHash -LiteralPath $faultCopy -Algorithm SHA256).Hash.ToLowerInvariant()
    policy = 'test-only prepended hook statements; production source unchanged; no statement removed or reordered'
    injections = $injections
}
[IO.File]::WriteAllText((Join-Path $run 'fault-injection.json'), ($manifest | ConvertTo-Json -Depth 5), $utf8NoBom)

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $run 'WriterFaultTests.exe'
& (Join-Path $fw 'csc.exe') /nologo /target:exe /langversion:5 /main:WriterFaultTests /platform:x64 /optimize+ /codepage:65001 "/out:$exe" $faultCopy (Join-Path $PSScriptRoot 'WriterFaultTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'fault compile failed' }
if ($CompileOnly) { Write-Host "COMPILED: $run"; return }
& $exe (Join-Path $run 'fault-output')
if ($LASTEXITCODE -ne 0) { throw 'fault assertions failed' }
Write-Host "PASS writer fault injection. Evidence: $run"
