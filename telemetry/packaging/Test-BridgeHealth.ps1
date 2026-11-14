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
        foreach ($l in 'widgets-bridge.cmd', 'widgets-bridge-server.cmd') { if (-not (Test-Path -LiteralPath (Join-Path $root $l))) { Fail "launcher_$l" 'missing (run Install or Update)' } }
        $dpath = Join-Path $root 'discovery.json'
        $useFacts = $false   # CLI is called the way discovery.json says (commands.facts only when the installed version has it)
        if (-not (Test-Path -LiteralPath $dpath)) { Fail 'discovery' 'discovery.json missing (run Install or Update)' }
        else {
            try {
                $d = Get-Content -LiteralPath $dpath -Raw | ConvertFrom-Json
                $dp = @()
                if ($d.kind -ne 'widgets-bridge-discovery' -or $d.schemaVersion -ne '1.0.0') { $dp += 'kind/schemaVersion' }
                if ($d.bridge.version_id -ne $cur.current) { $dp += "version_id $($d.bridge.version_id) differs from current $($cur.current)" }
                if (-not (Test-Path -LiteralPath $d.launchers.cli)) { $dp += 'launchers.cli does not exist' }
                if (-not (Test-Path -LiteralPath $d.launchers.mcp_server)) { $dp += 'launchers.mcp_server does not exist' }
                $useFacts = [bool]$d.commands.PSObject.Properties['facts']
                if ($d.PSObject.Properties['capabilities'] -and $d.capabilities.facts -ne $useFacts) { $dp += 'capabilities.facts differs from commands.facts' }
                if ($d.PSObject.Properties['capabilities'] -and $d.capabilities.PSObject.Properties['read_diagnostics']) {
                    $hasDiag = [bool]$d.commands.PSObject.Properties['diagnose']
                    $hasDiagTool = @($d.commands.mcp.tools) -contains 'get_read_diagnostics'
                    if ($d.capabilities.read_diagnostics -ne $hasDiag) { $dp += 'capabilities.read_diagnostics differs from commands.diagnose' }
                    if ($d.capabilities.read_diagnostics -ne $hasDiagTool) { $dp += 'capabilities.read_diagnostics differs from get_read_diagnostics in commands.mcp.tools' }
                }
                if ($dp.Count -eq 0) { $checks['discovery'] = 'ok' } else { Fail 'discovery' ($dp -join '; ') }
            } catch { Fail 'discovery' "unreadable: $($_.Exception.Message)" }
        }
        $cmd = Join-Path $root 'widgets-bridge.cmd'
        if (-not (Test-Path -LiteralPath $cmd)) { Fail 'launcher' 'widgets-bridge.cmd missing' }
        else {
            $fx = Join-Path $vdir 'fixtures\normal'
            $cliArgs = if ($useFacts) { @('facts', '--json') } else { @() }
            $out = (& $cmd @cliArgs --fixture $fx --as-of 2026-01-01T12:00:00Z 2>&1 | Out-String)
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
