# Самопроверка прототипа на Windows в реальном окне (без сна: SIGSTOP на Windows нет — сон проверяется вручную).
param([Parameter(Mandatory)] [string] $OutDir, [string[]] $AppArgs = @())
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'Limits.App/bin/Release/net10.0/LimitsXplat.dll'
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
$argList = @($dll, '--selftest', $OutDir, '--selftest-seconds', '15') + $AppArgs
$p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -PassThru -NoNewWindow
if (-not $p.WaitForExit(240000)) { $p.Kill($true); throw 'selftest timeout' }
Get-Content (Join-Path $OutDir 'report.json')
exit $p.ExitCode
