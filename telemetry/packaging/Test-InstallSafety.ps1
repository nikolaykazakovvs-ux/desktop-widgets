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
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $results.Add([ordered]@{ check = $Name; ok = $Condition; detail = $Detail })
    Write-Host ("{0} {1} {2}" -f $(if ($Condition) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}
function Run-Script([string]$Script, [string[]]$Arguments) {
    $out = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Script) @Arguments 2>&1 | Out-String
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
    $r = Run-Script 'Uninstall-Bridge.ps1' @('-InstallRoot', $root)
    $u = Json $r.out
    Check 'uninstall_exit_ok' ($r.code -eq 0) "exit $($r.code)"
    Check 'uninstall_removed_real_version' (-not (Test-Path -LiteralPath $versionDir)) ''
    Check 'uninstall_kept_foreign_name' (Test-Path -LiteralPath (Join-Path $root 'versions\not-a-bridge-version\user-data.txt')) ''
    Check 'uninstall_kept_version_shaped_dir_without_manifest' (Test-Path -LiteralPath (Join-Path $root 'versions\1.0.0-abcdef012345\user-data.txt')) ''
    Check 'uninstall_reports_foreign' (@($u.foreign).Count -eq 2) "$(@($u.foreign) -join ', ')"

    # 7. Uninstall does nothing in a directory that is not a Bridge install root
    $other = Join-Path $WorkRoot 'not-bridge'
    New-Item -ItemType Directory -Path (Join-Path $other 'versions\something') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $other 'versions\something\data.txt') -Value 'keep'
    $r = Run-Script 'Uninstall-Bridge.ps1' @('-InstallRoot', $other)
    Check 'uninstall_ignores_foreign_root' ($r.code -eq 0 -and (Test-Path -LiteralPath (Join-Path $other 'versions\something\data.txt'))) "$($r.out.Trim())"
} finally {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { -not $_.ok })
Write-Output ([ordered]@{ checks = $results.Count; failed = $failed.Count } | ConvertTo-Json -Compress)
exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
