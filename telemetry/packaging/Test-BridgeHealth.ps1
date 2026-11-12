[CmdletBinding()]
param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DesktopWidgets\bridge'))
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
$checks = [ordered]@{}; $script:ok = $true
function Fail($k, $m) { $script:checks[$k] = "FAIL: $m"; $script:ok = $false }
try {
    $root = Assert-SafeInstallRoot $InstallRoot
    if (Test-Path -LiteralPath $root) {
        $acl = Test-PrivateDirectory $root
        if ($acl.ok) { $checks['acl'] = 'ok (owner and allow ACEs: current user, SYSTEM, Administrators)' } else { Fail 'acl' ($acl.problems -join '; ') }
    }
    $cur = Read-Current $root
    if (-not $cur) { Fail 'current.json' 'missing' } else {
        $checks['current'] = $cur.current; $checks['previous'] = $cur.previous
        Assert-VersionId $cur.current 'current.json current'
        $vdir = Join-Path $root "versions\$($cur.current)"
        if (-not (Test-Path -LiteralPath $vdir)) { Fail 'version_dir' 'missing' }
        else {
            $m = Test-Manifest $vdir
            if ($m.ok) { $checks['manifest'] = 'ok' } else { Fail 'manifest' ($m.problems -join '; ') }
        }
        $nv = "$(& node --version 2>&1)"
        if ($nv -match '^v(\d+)' -and [int]$Matches[1] -ge 20) { $checks['node'] = $nv } else { Fail 'node' "need >=20, got $nv" }
        $cmd = Join-Path $root 'bridge-cli.cmd'
        if (-not (Test-Path -LiteralPath $cmd)) { Fail 'launcher' 'bridge-cli.cmd missing' }
        else {
            $fx = Join-Path $vdir 'fixtures\normal'
            $out = (& $cmd --fixture $fx --as-of 2026-01-01T12:00:00Z 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { Fail 'cli' "exit $LASTEXITCODE" }
            else {
                try {
                    $j = $out | ConvertFrom-Json
                    if ($j.mode -eq 'fixture' -and $j.schemaVersion -eq '1.0.0') { $checks['cli'] = 'ok (mode=fixture, schemaVersion=1.0.0)' }
                    else { Fail 'cli' "unexpected mode=$($j.mode) schemaVersion=$($j.schemaVersion)" }
                } catch { Fail 'cli' "non-JSON output: $($_.Exception.Message)" }
            }
        }
    }
} catch { Fail 'error' $_.Exception.Message }
[ordered]@{ ok = $script:ok; installRoot = $InstallRoot; checks = $checks } | ConvertTo-Json -Depth 4
exit $(if ($script:ok) { 0 } else { 1 })
