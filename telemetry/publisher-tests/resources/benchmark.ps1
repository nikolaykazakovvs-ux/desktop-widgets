param([string]$WorkRoot = (Join-Path $PSScriptRoot '..\..\..\.work\telemetry-publisher-benchmark'), [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'

function Add-UniqueInstrumentation([string]$Text, [string]$Label, [string]$Anchor, [string]$Replacement) {
    $first = $Text.IndexOf($Anchor, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "benchmark instrumentation anchor missing: $Label" }
    $second = $Text.IndexOf($Anchor, $first + $Anchor.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) { throw "benchmark instrumentation anchor not unique: $Label" }
    if (-not $Replacement.Contains($Anchor)) { throw "benchmark instrumentation replacement lost anchor: $Label" }
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
$originalCopy = Join-Path $run 'TelemetrySnapshotWriter.original.cs'
$instrumentedCopy = Join-Path $run 'TelemetrySnapshotWriter.instrumented.cs'
[IO.File]::Copy($shared, $originalCopy)
$probe = 'global::TelemetryWriterBenchmarkProbe'
$injections = @(
    @{ Label = 'worker_entry'; Anchor = '            Mutex mutex = null;'; Replacement = "            $probe.OnWorkerThreadStarted();`n            Mutex mutex = null;" },
    @{ Label = 'worker_ready'; Anchor = '                var clock = Stopwatch.StartNew();'; Replacement = "                $probe.OnWorkerReady();`n                var clock = Stopwatch.StartNew();" },
    @{ Label = 'serialization'; Anchor = '                        string json = value(DateTime.UtcNow);'; Replacement = "                        $probe.OnOperationStart();`n                        string json = value(DateTime.UtcNow);`n                        $probe.OnSerializationEnd();" },
    @{ Label = 'publish'; Anchor = '                        Publish(bytes, sid);'; Replacement = "                        $probe.OnWriteStart();`n                        Publish(bytes, sid);`n                        $probe.OnWriteEnd();" },
    @{ Label = 'completion'; Anchor = '                        lastError = null;'; Replacement = "                        lastError = null;`n                        $probe.OnPublicationComplete();" },
    @{ Label = 'publish_directory_acl'; Anchor = '            AssertPrivate(Directory.GetAccessControl(directory), sid);'; Replacement = "            $probe.OnInitialReparseEnd();`n            AssertPrivate(Directory.GetAccessControl(directory), sid);`n            $probe.OnDirectoryAclEnd();" },
    @{ Label = 'publish_temp_create_start'; Anchor = '                handle = CreateFileW(temp, TempAccess, 0, IntPtr.Zero, CreateNew, TempFlags, IntPtr.Zero);'; Replacement = "                $probe.OnTempCreateStart();`n                handle = CreateFileW(temp, TempAccess, 0, IntPtr.Zero, CreateNew, TempFlags, IntPtr.Zero);" },
    @{ Label = 'publish_temp_write'; Anchor = '                file.Write(bytes, 0, bytes.Length);'; Replacement = "                $probe.OnTempCreateEnd();`n                file.Write(bytes, 0, bytes.Length);`n                $probe.OnTempWriteEnd();" },
    @{ Label = 'publish_temp_flush'; Anchor = ' file.Flush();'; Replacement = " file.Flush();`n                $probe.OnTempFlushEnd();" },
    @{ Label = 'publish_temp_acl'; Anchor = '                AssertPrivate(tempSecurity = file.GetAccessControl(), sid);'; Replacement = "                AssertPrivate(tempSecurity = file.GetAccessControl(), sid);`n                $probe.OnTempAclEnd();" },
    # The temp is no longer closed before rename; this phase now ends at retry setup and the close is in finally_cleanup_check.
    @{ Label = 'publish_temp_close'; Anchor = '                var retry = Stopwatch.StartNew();'; Replacement = "                $probe.OnTempCloseEnd();`n                var retry = Stopwatch.StartNew();" },
    @{ Label = 'publish_final_reparse'; Anchor = '                    AssertNoReparse(directory);'; Replacement = "                    $probe.OnFinalReparseStart();`n                    AssertNoReparse(directory);`n                    $probe.OnFinalReparseEnd();" },
    @{ Label = 'publish_final_target_guard'; Anchor = '                    FileSecurity targetSecurity = AssertTarget(sid);'; Replacement = "                    FileSecurity targetSecurity = AssertTarget(sid);`n                    $probe.OnFinalTargetGuardEnd();" },
    @{ Label = 'publish_target_acl'; Anchor = '            AssertPrivate(security = File.GetAccessControl(TargetPath), sid);'; Replacement = "            $probe.OnTargetAclStart();`n            AssertPrivate(security = File.GetAccessControl(TargetPath), sid);`n            $probe.OnTargetAclEnd();" },
    # replace_or_move now spans the DACL carry check and the handle rename.
    @{ Label = 'publish_replace_or_move'; Anchor = '                        return;'; Replacement = "                        $probe.OnReplaceOrMoveEnd();`n                        return;" },
    @{ Label = 'sharing_retry'; Anchor = '                    Thread.Sleep(waits[attempt]);'; Replacement = "                    $probe.OnRetryStart(waits[attempt]);`n                    Thread.Sleep(waits[attempt]);`n                    $probe.OnRetryEnd();" }
)
$instrumented = $writerText
$expectedLength = $writerText.Length
foreach ($injection in $injections) {
    $instrumented = Add-UniqueInstrumentation $instrumented $injection.Label $injection.Anchor $injection.Replacement
    $expectedLength += ($injection.Replacement.Length - $injection.Anchor.Length)
}
if ($instrumented.Length -ne $expectedLength) { throw 'benchmark instrumentation changed more than the declared additive hooks' }
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[IO.File]::WriteAllText($instrumentedCopy, $instrumented, $utf8NoBom)
$originalSha256 = (Get-FileHash -LiteralPath $shared -Algorithm SHA256).Hash.ToLowerInvariant()
$originalCopySha256 = (Get-FileHash -LiteralPath $originalCopy -Algorithm SHA256).Hash.ToLowerInvariant()
if ($originalCopySha256 -ne $originalSha256) { throw 'benchmark original source copy hash mismatch' }
$instrumentedSha256 = (Get-FileHash -LiteralPath $instrumentedCopy -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    kind = 'resources-publisher-benchmark-instrumentation'
    original_source = 'telemetry/publisher-shared/TelemetrySnapshotWriter.cs'
    original_sha256 = $originalSha256
    original_copy = 'TelemetrySnapshotWriter.original.cs'
    original_copy_sha256 = $originalCopySha256
    instrumented_copy = 'TelemetrySnapshotWriter.instrumented.cs'
    instrumented_sha256 = $instrumentedSha256
    policy = 'additive test-only diagnostic calls; no existing statement removed or reordered'
    completion = 'OnPublicationComplete records its timestamp before event notification; notification and observer wakeup are outside worker_operation'
    injections = $injections
}
[IO.File]::WriteAllText((Join-Path $run 'benchmark-instrumentation.json'), ($manifest | ConvertTo-Json -Depth 5), $utf8NoBom)

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll') | ForEach-Object { '/r:' + (Join-Path $fw $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $fw ('WPF\' + $_)) }
$src = @(Get-ChildItem -LiteralPath (Join-Path $repo 'windows\resources-widget\src') -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })
$src += Join-Path $repo 'windows\resources-widget\installer\shared\SensorSecurity.cs'
$src += $instrumentedCopy
$src += Join-Path $PSScriptRoot 'ResourcesPublisherBenchmark.cs'
$exe = Join-Path $run 'ResourcesPublisherBenchmark.exe'
& (Join-Path $fw 'csc.exe') /nologo /target:exe /langversion:5 /main:ResourcesPublisherBenchmark /platform:x64 /optimize+ /codepage:65001 "/out:$exe" @refs @src
if ($LASTEXITCODE -ne 0) { throw 'benchmark compile failed' }
if ($CompileOnly) { Write-Host "COMPILED: $run"; return }
& $exe $run $originalSha256
if ($LASTEXITCODE -ne 0) { throw 'benchmark failed' }
& python (Join-Path $repo 'telemetry\tests\contract.py') hardware-snapshot (Join-Path $run 'snapshot\hardware-snapshot.json')
if ($LASTEXITCODE -ne 0) { throw 'benchmark snapshot contract failed' }
Write-Host "BENCHMARK EVIDENCE: $run"
