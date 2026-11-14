<#
.SYNOPSIS Isolated safety checks of the Bridge packaging scripts (PowerShell 7, Windows).
 Covers: protected DACL of a new InstallRoot, refusal of an InstallRoot open to other principals, hash check of
 bridge\node_modules, Uninstall removing only real Bridge versions, version id validation in current.json.
 -WorkRoot    absolute path of a NEW directory for temporary install roots; it is deleted at the end
 -SourceRoot  repository root (default: two levels above this script); needs telemetry\bridge\node_modules
Never touches %LOCALAPPDATA%, client profiles or installed widgets.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
if (-not [IO.Path]::IsPathRooted($WorkRoot)) { throw 'WorkRoot must be absolute.' }
if (Test-Path -LiteralPath $WorkRoot) { throw "WorkRoot must not exist: $WorkRoot" }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
# Isolated client profile: the scripts never read or write real client profiles.
$fakeHome = Join-Path $WorkRoot 'home'
New-Item -ItemType Directory -Path $fakeHome | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $results.Add([ordered]@{ check = $Name; ok = $Condition; detail = $Detail })
    Write-Host ("{0} {1} {2}" -f $(if ($Condition) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}
function Run-Script([string]$Script, [string[]]$Arguments) {
    $path = if ([IO.Path]::IsPathRooted($Script)) { $Script } else { Join-Path $PSScriptRoot $Script }
    $out = & pwsh -NoProfile -ExecutionPolicy Bypass -File $path @Arguments 2>&1 | Out-String
    return @{ code = $LASTEXITCODE; out = $out }
}
function Json($text) { return $text.Substring($text.IndexOf('{')) | ConvertFrom-Json }

try {
    # 1. New InstallRoot: protected DACL for the current user, SYSTEM and Administrators only
    $root = Join-Path $WorkRoot 'root-new'
    $r = Run-Script 'Install-Bridge.ps1' @('-SourceRoot', $SourceRoot, '-InstallRoot', $root)
    Check 'install_new_root' ($r.code -eq 0) "exit $($r.code)"
    $acl = Get-Acl -LiteralPath $root
    Check 'new_root_dacl_protected' ($acl.AreAccessRulesProtected) 'inheritance removed'
    $priv = Test-PrivateDirectory $root
    Check 'new_root_only_trusted_principals' $priv.ok ($priv.problems -join '; ')
    $version = (Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json).current
    $versionDir = Join-Path $root "versions\$version"
    $versionAcl = Test-PrivateDirectory $versionDir
    Check 'version_dir_inherits_private_dacl' $versionAcl.ok ($versionAcl.problems -join '; ')

    # 2. Health check includes the DACL and the node_modules hashes
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $root)
    Check 'health_ok_on_clean_install' ($r.code -eq 0) "exit $($r.code)"
    $health = Json $r.out
    Check 'health_reports_acl' ($health.checks.acl -like 'ok*') "$($health.checks.acl)"

    # 2b. Repeated install of the same content is idempotent on an existing private root
    $r = Run-Script 'Install-Bridge.ps1' @('-SourceRoot', $SourceRoot, '-InstallRoot', $root)
    Check 'reinstall_already_present' ($r.code -eq 0 -and (Json $r.out).action -eq 'already-present') "exit $($r.code)"

    # 2c. discovery.json and the Widgets Bridge launchers appear on install and are correct
    $dpath = Join-Path $root 'discovery.json'
    Check 'discovery_written_on_install' (Test-Path -LiteralPath $dpath) ''
    $disc = Get-Content -LiteralPath $dpath -Raw | ConvertFrom-Json
    Check 'discovery_core_fields' ($disc.kind -eq 'widgets-bridge-discovery' -and $disc.schemaVersion -eq '1.0.0' -and $disc.name -eq 'widgets-bridge' -and $disc.bridge.version_id -eq $version -and $disc.bridge.install_root -eq $root) "version_id=$($disc.bridge.version_id)"
    Check 'discovery_contract_versions' ($disc.telemetry_contract.schemaVersion -eq '1.0.0' -and $disc.telemetry_contract.policy_version -eq '0.1.0' -and $disc.telemetry_contract.api_limits_extension -eq 'api-limits/1.1.0') ($disc.telemetry_contract | ConvertTo-Json -Compress)
    Check 'discovery_api_extension_is_in_extension_schema' ((Get-Content -LiteralPath (Join-Path $versionDir 'schema\extensions\api-limits-v1.1.schema.json') -Raw).Contains([string]$disc.telemetry_contract.api_limits_extension)) ''
    Check 'discovery_capabilities_detected_for_current_sources' ($disc.capabilities.facts -eq $true -and $disc.capabilities.api_limits -eq $true -and $disc.commands.facts -like '*widgets-bridge.cmd" facts --json' -and $disc.commands.cli -like '*widgets-bridge.cmd"') ($disc.capabilities | ConvertTo-Json -Compress)
    Check 'discovery_read_diagnostics_for_current_sources' ($disc.capabilities.read_diagnostics -eq $true -and $disc.commands.diagnose -like '*widgets-bridge.cmd" diagnose --json' -and (@($disc.commands.mcp.tools) -join ',') -eq 'get_telemetry_and_quotas,get_read_diagnostics') "$($disc.commands.diagnose) tools=$(@($disc.commands.mcp.tools) -join ',')"
    Check 'discovery_launchers_exist' ((Test-Path -LiteralPath $disc.launchers.cli) -and (Test-Path -LiteralPath $disc.launchers.mcp_server) -and $disc.launchers.cli -eq (Join-Path $root 'widgets-bridge.cmd') -and $disc.launchers.mcp_server -eq (Join-Path $root 'widgets-bridge-server.cmd')) ''
    Check 'discovery_commands' ($disc.commands.facts -like '*widgets-bridge.cmd" facts --json' -and $disc.commands.mcp.server_name -eq 'widgets-bridge' -and $disc.commands.mcp.command -eq $disc.launchers.mcp_server) "$($disc.commands.facts)"
    $dataKeys = @($disc.data | ForEach-Object { $_.key })
    Check 'discovery_data_list' (($dataKeys -join ',') -eq 'subscription_quotas,hardware_state,api_limits' -and (@($disc.data | Where-Object { $_.snapshot -like '*\DesktopWidgets\telemetry\*-snapshot.json' }).Count -eq 3)) ($dataKeys -join ',')
    Check 'discovery_has_no_secret_words' ((Get-Content -LiteralPath $dpath -Raw) -notmatch '(?i)password|token|secret|api[_-]?key') ''
    Check 'legacy_launchers_kept' ((Test-Path -LiteralPath (Join-Path $root 'bridge-cli.cmd')) -and (Test-Path -LiteralPath (Join-Path $root 'bridge-server.cmd'))) ''
    $fx = Join-Path $versionDir 'fixtures\normal'
    $facts = (& (Join-Path $root 'widgets-bridge.cmd') facts --json --fixture $fx --as-of 2026-01-01T12:00:00Z 2>&1 | Out-String)
    $factsOk = $false; try { $fj = $facts | ConvertFrom-Json; $factsOk = ($LASTEXITCODE -eq 0 -and $fj.mode -eq 'fixture' -and $fj.schemaVersion -eq '1.0.0') } catch { }
    Check 'widgets_bridge_facts_runs' $factsOk ''
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $root)
    Check 'health_reports_discovery' ($r.code -eq 0 -and (Json $r.out).checks.discovery -eq 'ok') "exit $($r.code)"

    # 3. InstallRoot that is open to Everyone is refused and left untouched
    $wide = Join-Path $WorkRoot 'root-wide'
    New-Item -ItemType Directory -Path $wide | Out-Null
    & icacls.exe $wide /grant '*S-1-1-0:(OI)(CI)M' | Out-Null
    $r = Run-Script 'Install-Bridge.ps1' @('-SourceRoot', $SourceRoot, '-InstallRoot', $wide)
    Check 'wide_root_refused' ($r.code -ne 0 -and $r.out -match 'accessible to other principals') "exit $($r.code)"
    Check 'wide_root_untouched' (-not (Test-Path -LiteralPath (Join-Path $wide 'versions')) -and -not (Test-Path -LiteralPath (Join-Path $wide 'current.json'))) ''
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $wide)
    Check 'health_fails_on_wide_root' ($r.code -ne 0 -and $r.out -match 'FAIL: .*allow S-1-1-0') "exit $($r.code)"

    # 4. A changed file inside bridge\node_modules is detected
    $victim = Join-Path $versionDir 'bridge\node_modules\zod\package.json'
    Add-Content -LiteralPath $victim -Value ' ' -NoNewline
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $root)
    Check 'node_modules_tamper_detected' ($r.code -ne 0 -and $r.out -match 'hash mismatch: bridge/node_modules/zod/package.json') "exit $($r.code)"
    # a file added to node_modules is detected as unlisted
    $extra = Join-Path $versionDir 'bridge\node_modules\zod\injected.js'
    Set-Content -LiteralPath $extra -Value '//'
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $root)
    Check 'node_modules_added_file_detected' ($r.code -ne 0 -and $r.out -match 'unlisted: bridge/node_modules/zod/injected.js') "exit $($r.code)"

    # 5. Invalid ids in current.json are not used
    $cur = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    $bad = [ordered]@{ current = $cur.current; previous = '..\..\outside'; installed_at = $cur.installed_at }
    [IO.File]::WriteAllText((Join-Path $root 'current.json'), ($bad | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    $r = Run-Script 'Install-Bridge.ps1' @('-InstallRoot', $root, '-Rollback')
    Check 'rollback_rejects_invalid_previous_id' ($r.code -ne 0 -and $r.out -match 'not a valid version id') "exit $($r.code)"
    $bad.current = '1.0.0"&calc&"'
    [IO.File]::WriteAllText((Join-Path $root 'current.json'), ($bad | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $root)
    Check 'health_rejects_invalid_current_id' ($r.code -ne 0 -and $r.out -match 'not a valid version id') "exit $($r.code)"
    [IO.File]::WriteAllText((Join-Path $root 'current.json'), ($cur | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

    # 6. Uninstall removes only real Bridge versions; foreign directories and files stay
    New-Item -ItemType Directory -Path (Join-Path $root 'versions\not-a-bridge-version') | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'versions\not-a-bridge-version\user-data.txt') -Value 'keep'
    New-Item -ItemType Directory -Path (Join-Path $root 'versions\1.0.0-abcdef012345') | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'versions\1.0.0-abcdef012345\user-data.txt') -Value 'keep'
    $r = Run-Script 'Uninstall-Bridge.ps1' @('-InstallRoot', $root, '-ClientsHomeDir', $fakeHome)
    $u = Json $r.out
    Check 'uninstall_exit_ok' ($r.code -eq 0 -and -not $u.PSObject.Properties['partial']) "exit $($r.code)"
    Check 'uninstall_removed_real_version' (-not (Test-Path -LiteralPath $versionDir)) ''
    Check 'uninstall_removed_discovery_and_launchers' (-not (Test-Path -LiteralPath $dpath) -and -not (Test-Path -LiteralPath (Join-Path $root 'widgets-bridge.cmd')) -and -not (Test-Path -LiteralPath (Join-Path $root 'widgets-bridge-server.cmd'))) ''
    Check 'uninstall_kept_foreign_name' (Test-Path -LiteralPath (Join-Path $root 'versions\not-a-bridge-version\user-data.txt')) ''
    Check 'uninstall_kept_version_shaped_dir_without_manifest' (Test-Path -LiteralPath (Join-Path $root 'versions\1.0.0-abcdef012345\user-data.txt')) ''
    Check 'uninstall_reports_foreign' (@($u.foreign).Count -eq 2) "$(@($u.foreign) -join ', ')"

    # 7. Uninstall does nothing in a directory that is not a Bridge install root
    $other = Join-Path $WorkRoot 'not-bridge'
    New-Item -ItemType Directory -Path (Join-Path $other 'versions\something') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $other 'versions\something\data.txt') -Value 'keep'
    $r = Run-Script 'Uninstall-Bridge.ps1' @('-InstallRoot', $other, '-ClientsHomeDir', $fakeHome)
    Check 'uninstall_ignores_foreign_root' ($r.code -eq 0 -and (Test-Path -LiteralPath (Join-Path $other 'versions\something\data.txt'))) "$($r.out.Trim())"

    # 8. Lifecycle: install without the flag leaves clients alone; Update -RegisterClients registers; Rollback rewrites
    #    discovery.json; Uninstall removes discovery.json and unregisters (all against fake client profiles)
    $life = Join-Path $WorkRoot 'root-life'
    $lifeHome = Join-Path $WorkRoot 'home-life'
    New-Item -ItemType Directory -Path (Join-Path $lifeHome '.codex') -Force | Out-Null
    $claudeCfg = Join-Path $lifeHome '.claude.json'
    $codexCfg = Join-Path $lifeHome '.codex\config.toml'
    [IO.File]::WriteAllText($claudeCfg, "{`n  `"numStartups`": 1,`n  `"mcpServers`": {`n    `"other`": { `"command`": `"x`" }`n  }`n}`n", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($codexCfg, "model = `"m`"`n[mcp_servers.other]`ncommand = `"y`"`n", (New-Object Text.UTF8Encoding($false)))
    $h1 = @((Get-Sha256 $claudeCfg), (Get-Sha256 $codexCfg))
    $src2 = Join-Path $WorkRoot 'src2'
    New-Item -ItemType Directory -Path (Join-Path $src2 'telemetry') -Force | Out-Null
    foreach ($d in 'bridge', 'schema', 'fixtures') { Copy-Item -LiteralPath (Join-Path $SourceRoot "telemetry\$d") -Destination (Join-Path $src2 "telemetry\$d") -Recurse }
    Add-Content -LiteralPath (Join-Path $src2 'telemetry\bridge\cli.mjs') -Value '// second version for the rollback test'

    $r = Run-Script 'Install-Bridge.ps1' @('-SourceRoot', $SourceRoot, '-InstallRoot', $life, '-ClientsHomeDir', $lifeHome)
    $v1 = (Json $r.out).current
    Check 'install_without_flag_leaves_clients_untouched' ($r.code -eq 0 -and $h1[0] -eq (Get-Sha256 $claudeCfg) -and $h1[1] -eq (Get-Sha256 $codexCfg) -and -not (Json $r.out).PSObject.Properties['registration'] -and @(Get-ChildItem -LiteralPath $lifeHome -Recurse -Force -File | Where-Object Name -like '*.bak-*').Count -eq 0) "exit $($r.code)"

    $r = Run-Script 'Update-Bridge.ps1' @('-SourceRoot', $src2, '-InstallRoot', $life, '-RegisterClients', '-ClientsHomeDir', $lifeHome)
    $u = Json $r.out
    $v2 = $u.current
    Check 'update_installs_new_version' ($r.code -eq 0 -and $v2 -ne $v1 -and $u.previous -eq $v1) "exit $($r.code) v1=$v1 v2=$v2"
    $st = @{}; foreach ($c in @($u.registration)) { $st[$c.client] = $c.status }
    Check 'update_registers_clients' ($st['claude-code'] -eq 'registered' -and $st['codex'] -eq 'registered' -and $st['kimi-code'] -eq 'not_found' -and $st['antigravity'] -eq 'not_found') (($st.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ',')
    $disc = Get-Content -LiteralPath (Join-Path $life 'discovery.json') -Raw | ConvertFrom-Json
    Check 'update_rewrites_discovery' ($disc.bridge.version_id -eq $v2) "$($disc.bridge.version_id)"
    $cj = Get-Content -LiteralPath $claudeCfg -Raw | ConvertFrom-Json
    Check 'registered_entry_points_to_launcher' ($cj.mcpServers.'widgets-bridge'.command -eq (Join-Path $life 'widgets-bridge-server.cmd') -and $cj.mcpServers.other.command -eq 'x' -and (Get-Content -LiteralPath $codexCfg -Raw) -match '\[mcp_servers\.widgets-bridge\]' -and (Get-Content -LiteralPath $codexCfg -Raw) -match '\[mcp_servers\.other\]') ''

    $r = Run-Script 'Install-Bridge.ps1' @('-InstallRoot', $life, '-Rollback')
    $disc = Get-Content -LiteralPath (Join-Path $life 'discovery.json') -Raw | ConvertFrom-Json
    $curNow = (Get-Content -LiteralPath (Join-Path $life 'current.json') -Raw | ConvertFrom-Json).current
    Check 'rollback_rewrites_discovery' ($r.code -eq 0 -and $curNow -eq $v1 -and $disc.bridge.version_id -eq $v1) "exit $($r.code) current=$curNow discovery=$($disc.bridge.version_id)"
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $life)
    Check 'health_ok_after_rollback' ($r.code -eq 0) "exit $($r.code)"

    $r = Run-Script 'Uninstall-Bridge.ps1' @('-InstallRoot', $life, '-ClientsHomeDir', $lifeHome)
    $uu = Json $r.out
    $cj = Get-Content -LiteralPath $claudeCfg -Raw | ConvertFrom-Json
    Check 'uninstall_removes_discovery' ($r.code -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $life 'discovery.json'))) "exit $($r.code)"
    Check 'uninstall_unregisters_clients_only_own_entry' (-not $cj.mcpServers.PSObject.Properties['widgets-bridge'] -and $cj.mcpServers.other.command -eq 'x' -and (Get-Content -LiteralPath $codexCfg -Raw) -notmatch 'widgets-bridge' -and (Get-Content -LiteralPath $codexCfg -Raw) -match '\[mcp_servers\.other\]') "$((@($uu.registration) | ForEach-Object { $_.client + '=' + $_.status }) -join ',')"

    # 9. Capabilities in discovery.json follow the INSTALLED version (review F1): after a rollback to a version without
    #    `facts` the file must not promise `facts --json`, and health must call the CLI the way discovery says.
    #    The old version is built from the current sources with the `facts`/`--json` handling removed from options.mjs.
    $srcOld = Join-Path $WorkRoot 'src-old'
    New-Item -ItemType Directory -Path (Join-Path $srcOld 'telemetry') -Force | Out-Null
    foreach ($d in 'bridge', 'schema', 'fixtures') { Copy-Item -LiteralPath (Join-Path $SourceRoot "telemetry\$d") -Destination (Join-Path $srcOld "telemetry\$d") -Recurse }
    $optPath = Join-Path $srcOld 'telemetry\bridge\options.mjs'
    $optText = [IO.File]::ReadAllText($optPath)
    $optOld = [regex]::Replace($optText, "(?m)^[ \t]*if \(cli && (i === 0 && flag === 'facts'|flag === '--json')\) continue;\r?\n", '')
    Check 'old_version_fixture_built_without_facts' ($optOld -ne $optText -and $optOld -notmatch "['""]facts['""]") ''
    [IO.File]::WriteAllText($optPath, $optOld, (New-Object Text.UTF8Encoding($false)))
    $rootOld = Join-Path $WorkRoot 'root-old'
    $r = Run-Script 'Install-Bridge.ps1' @('-SourceRoot', $srcOld, '-InstallRoot', $rootOld)
    $vOld = (Json $r.out).current
    $discOld = Get-Content -LiteralPath (Join-Path $rootOld 'discovery.json') -Raw | ConvertFrom-Json
    Check 'old_version_discovery_has_no_facts' ($r.code -eq 0 -and $discOld.capabilities.facts -eq $false -and -not $discOld.commands.PSObject.Properties['facts'] -and $discOld.commands.cli -like '*widgets-bridge.cmd"') "exit $($r.code) capabilities=$($discOld.capabilities | ConvertTo-Json -Compress)"
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $rootOld)
    Check 'old_version_health_ok_without_facts' ($r.code -eq 0 -and (Json $r.out).checks.cli -like 'ok*') "exit $($r.code)"
    $r = Run-Script 'Update-Bridge.ps1' @('-SourceRoot', $SourceRoot, '-InstallRoot', $rootOld)
    $discNew = Get-Content -LiteralPath (Join-Path $rootOld 'discovery.json') -Raw | ConvertFrom-Json
    Check 'update_to_new_version_adds_facts' ($r.code -eq 0 -and $discNew.bridge.version_id -ne $vOld -and $discNew.capabilities.facts -eq $true -and $discNew.commands.facts -like '*widgets-bridge.cmd" facts --json') "exit $($r.code)"
    $r = Run-Script 'Install-Bridge.ps1' @('-InstallRoot', $rootOld, '-Rollback')
    $discBack = Get-Content -LiteralPath (Join-Path $rootOld 'discovery.json') -Raw | ConvertFrom-Json
    Check 'rollback_to_version_without_facts_drops_facts_from_discovery' ($r.code -eq 0 -and $discBack.bridge.version_id -eq $vOld -and $discBack.capabilities.facts -eq $false -and -not $discBack.commands.PSObject.Properties['facts']) "exit $($r.code) version_id=$($discBack.bridge.version_id) commands=$($discBack.commands | ConvertTo-Json -Compress -Depth 3)"
    $r = Run-Script 'Test-BridgeHealth.ps1' @('-InstallRoot', $rootOld)
    Check 'health_ok_after_rollback_to_version_without_facts' ($r.code -eq 0 -and (Json $r.out).checks.discovery -eq 'ok') "exit $($r.code)"
    $oldFx = Join-Path $rootOld "versions\$vOld\fixtures\normal"
    $null = & (Join-Path $rootOld 'widgets-bridge.cmd') facts --json --fixture $oldFx --as-of 2026-01-01T12:00:00Z 2>&1
    $factsExit = $LASTEXITCODE
    $null = & (Join-Path $rootOld 'widgets-bridge.cmd') --fixture $oldFx --as-of 2026-01-01T12:00:00Z 2>&1
    Check 'old_version_really_lacks_facts_and_bare_call_works' ($factsExit -eq 2 -and $LASTEXITCODE -eq 0) "facts exit $factsExit, bare exit $LASTEXITCODE"

    # 9b. Capability detection on synthetic version directories (no installation): comments do not count, no api_limits without
    #     the extension and the snapshot reader.
    $synRoot = Join-Path $WorkRoot 'root-synth'
    $synId = '1.0.0-aaaaaaaaaaaa'
    $synDir = Join-Path $synRoot "versions\$synId"
    New-Item -ItemType Directory -Path (Join-Path $synDir 'bridge'), (Join-Path $synDir 'schema') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'telemetry\schema\bridge-output.schema.json') -Destination (Join-Path $synDir 'schema')
    $enc = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $synDir 'manifest.json'), (@{ id = $synId; version = '1.0.0'; files = @() } | ConvertTo-Json), $enc)
    [IO.File]::WriteAllText((Join-Path $synRoot 'current.json'), (@{ current = $synId; previous = $null; installed_at = '2026-01-01T00:00:00Z' } | ConvertTo-Json), $enc)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\core.mjs'), "export const x = 1;`n", $enc)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\reader.mjs'), "export const FILES = { hardware: 'hardware-snapshot.json', quotas: 'quotas-snapshot.json' };`n", $enc)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\options.mjs'), "// 'facts' is mentioned only in a comment`nexport const a = 1;`n", $enc)
    Write-Discovery $synRoot $synId
    $ds = Get-Content -LiteralPath (Join-Path $synRoot 'discovery.json') -Raw | ConvertFrom-Json
    Check 'synthetic_old_version_has_no_facts_and_no_api_limits' ($ds.capabilities.facts -eq $false -and $ds.capabilities.api_limits -eq $false -and -not $ds.commands.PSObject.Properties['facts'] -and $null -eq $ds.telemetry_contract.api_limits_extension -and (@($ds.data | ForEach-Object { $_.key }) -join ',') -eq 'subscription_quotas,hardware_state') ($ds.capabilities | ConvertTo-Json -Compress)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\core.mjs'), "const e = { schema_extension: 'api-limits/1.1.0' };`n", $enc)
    Write-Discovery $synRoot $synId
    $ds = Get-Content -LiteralPath (Join-Path $synRoot 'discovery.json') -Raw | ConvertFrom-Json
    Check 'synthetic_extension_without_reader_support_is_not_api_limits' ($ds.telemetry_contract.api_limits_extension -eq 'api-limits/1.1.0' -and $ds.capabilities.api_limits -eq $false -and (@($ds.data | ForEach-Object { $_.key }) -notcontains 'api_limits')) ($ds.capabilities | ConvertTo-Json -Compress)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\reader.mjs'), "export const FILES = { hardware: 'hardware-snapshot.json', quotas: 'quotas-snapshot.json', api: 'api-balances-snapshot.json' };`n", $enc)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\options.mjs'), "if (cli && i === 0 && flag === 'facts') continue;`n", $enc)
    Write-Discovery $synRoot $synId
    $ds = Get-Content -LiteralPath (Join-Path $synRoot 'discovery.json') -Raw | ConvertFrom-Json
    Check 'synthetic_full_version_has_facts_and_api_limits' ($ds.capabilities.facts -eq $true -and $ds.capabilities.api_limits -eq $true -and $ds.commands.facts -like '*widgets-bridge.cmd" facts --json' -and (@($ds.data | ForEach-Object { $_.key }) -join ',') -eq 'subscription_quotas,hardware_state,api_limits') ($ds.capabilities | ConvertTo-Json -Compress)
    Check 'synthetic_version_without_diagnostics_promises_none' ($ds.capabilities.read_diagnostics -eq $false -and -not $ds.commands.PSObject.Properties['diagnose'] -and (@($ds.commands.mcp.tools) -join ',') -eq 'get_telemetry_and_quotas') ($ds.capabilities | ConvertTo-Json -Compress)
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\diagnostics.mjs'), "export const x = 1;`n", $enc)
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'telemetry\schema\read-diagnostics.schema.json') -Destination (Join-Path $synDir 'schema')
    [IO.File]::WriteAllText((Join-Path $synDir 'bridge\options.mjs'), "if (cli && i === 0 && flag === 'facts') continue;`nif (cli && i === 0 && flag === 'diagnose') { options.command = 'diagnose'; continue; }`n", $enc)
    Write-Discovery $synRoot $synId
    $ds = Get-Content -LiteralPath (Join-Path $synRoot 'discovery.json') -Raw | ConvertFrom-Json
    Check 'synthetic_version_with_diagnostics_lists_command_and_tool' ($ds.capabilities.read_diagnostics -eq $true -and $ds.commands.diagnose -like '*widgets-bridge.cmd" diagnose --json' -and (@($ds.commands.mcp.tools) -join ',') -eq 'get_telemetry_and_quotas,get_read_diagnostics') ($ds.capabilities | ConvertTo-Json -Compress)

    # 10. Uninstall with a failed unregistration is partial: the rest is removed, exit code 1 and partial=true (review F2)
    $partRoot = Join-Path $WorkRoot 'root-partial'
    New-Item -ItemType Directory -Path $partRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $partRoot 'widgets-bridge-server.cmd') -Value '@echo off'
    $badHome = Join-Path $WorkRoot 'home-bad'
    New-Item -ItemType Directory -Path $badHome | Out-Null
    [IO.File]::WriteAllText((Join-Path $badHome '.claude.json'), '{ "mcpServers": ', (New-Object Text.UTF8Encoding($false)))
    $badHash = Get-Sha256 (Join-Path $badHome '.claude.json')
    $r = Run-Script 'Uninstall-Bridge.ps1' @('-InstallRoot', $partRoot, '-ClientsHomeDir', $badHome)
    $up = Json $r.out
    $regSt = @{}; foreach ($c in @($up.registration)) { $regSt[$c.client] = $c.status }
    Check 'uninstall_with_registration_error_is_partial_exit_1' ($r.code -eq 1 -and $up.partial -eq $true -and $regSt['claude-code'] -eq 'error' -and -not (Test-Path -LiteralPath (Join-Path $partRoot 'widgets-bridge-server.cmd')) -and $badHash -eq (Get-Sha256 (Join-Path $badHome '.claude.json'))) "exit $($r.code) partial=$($up.partial) claude=$($regSt['claude-code'])"

    # 11. Install -RegisterClients: parameters are checked BEFORE the install; a failing registration step still yields the report (review F3)
    $f3a = Join-Path $WorkRoot 'root-f3a'
    $r = Run-Script 'Install-Bridge.ps1' @('-SourceRoot', $SourceRoot, '-InstallRoot', $f3a, '-RegisterClients', '-ClientsHomeDir', 'relative\home')
    Check 'install_refuses_relative_clientshomedir_before_installing' ($r.code -ne 0 -and $r.out -match 'ClientsHomeDir must be absolute' -and -not (Test-Path -LiteralPath $f3a)) "exit $($r.code)"
    $brokenPkg = Join-Path $WorkRoot 'pkg-broken'
    New-Item -ItemType Directory -Path $brokenPkg | Out-Null
    foreach ($f in 'Common.ps1', 'Install-Bridge.ps1', 'Register-Clients.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination $brokenPkg }   # Registration.ps1 is deliberately missing
    $f3b = Join-Path $WorkRoot 'root-f3b'
    $r = Run-Script (Join-Path $brokenPkg 'Install-Bridge.ps1') @('-SourceRoot', $SourceRoot, '-InstallRoot', $f3b, '-RegisterClients', '-ClientsHomeDir', $fakeHome)
    $ib = $null; try { $ib = Json $r.out } catch { }
    Check 'install_reports_result_when_registration_throws' ($r.code -eq 1 -and $null -ne $ib -and $ib.action -eq 'installed' -and @($ib.registration)[0].status -eq 'error' -and (Test-Path -LiteralPath (Join-Path $f3b 'current.json')) -and (Test-Path -LiteralPath (Join-Path $f3b 'discovery.json'))) "exit $($r.code)"
} finally {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { -not $_.ok })
Write-Output ([ordered]@{ checks = $results.Count; failed = $failed.Count } | ConvertTo-Json -Compress)
exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
