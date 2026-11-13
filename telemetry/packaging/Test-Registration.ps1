<#
.SYNOPSIS Isolated checks of Register-Clients.ps1 / Registration.ps1 on fictitious client configurations (PowerShell 7, Windows).
 Covers: JSON (claude-code, kimi-code, antigravity) and TOML (codex) editing engines (insert, idempotence, removal that
 restores the original bytes, damaged or ambiguous configs are refused without changes), backups, -WhatIf, per-client
 isolation of failures, removal of only own entries, entries of another installation, unsupported/not_found clients.
 -WorkRoot  absolute path of a NEW directory for temporary profiles; it is deleted at the end
Never reads or writes real client profiles: every script call passes -HomeDir inside WorkRoot.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkRoot)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\Registration.ps1"
if (-not [IO.Path]::IsPathRooted($WorkRoot)) { throw 'WorkRoot must be absolute.' }
if (Test-Path -LiteralPath $WorkRoot) { throw "WorkRoot must not exist: $WorkRoot" }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $results.Add([ordered]@{ check = $Name; ok = $Condition; detail = $Detail })
    Write-Host ("{0} {1} {2}" -f $(if ($Condition) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}
$u8 = New-Object Text.UTF8Encoding($false)
function B([string]$s) { return ,$u8.GetBytes($s) }
function S([byte[]]$b) { return $u8.GetString($b) }
function Throws([scriptblock]$Block) { try { & $Block | Out-Null; return $false } catch { return $true } }
function Same([byte[]]$a, [byte[]]$b) { return [System.Linq.Enumerable]::SequenceEqual($a, $b) }
$ru = -join ([char[]](0x041F, 0x0440, 0x0438, 0x0432, 0x0435, 0x0442))   # non-ASCII payload without non-ASCII source
$L = 'C:\fake\bridge\widgets-bridge-server.cmd'
$L2 = 'C:\other\bridge\widgets-bridge-server.cmd'
$N = 'widgets-bridge'
function JEntry([string]$cmd = $L) { return [ordered]@{ type = 'stdio'; command = $cmd; args = @() } }
function TEntry([string]$cmd = $L) { return [ordered]@{ command = $cmd; args = @(); startup_timeout_sec = 20; tool_timeout_sec = 30 } }
function JEdit([string]$text, [bool]$remove = $false, [string]$cmd = $L, [bool]$any = $false) { return Edit-JsonMcpServers -Bytes (B $text) -Name $N -Desired (JEntry $cmd) -Launcher $cmd -Remove $remove -AnyInstall $any }
function TEdit([string]$text, [bool]$remove = $false, [string]$cmd = $L, [bool]$any = $false) { return Edit-TomlMcpServer -Bytes (B $text) -Name $N -Desired (TEntry $cmd) -Launcher $cmd -Remove $remove -AnyInstall $any }
function Valid([string]$json) { try { $null = $json | ConvertFrom-Json; return $true } catch { return $false } }
function Run-Reg([string[]]$Arguments) {
    $out = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Register-Clients.ps1') @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    $json = $null
    $i = $out.IndexOf('{')
    if ($i -ge 0) { try { $json = $out.Substring($i) | ConvertFrom-Json } catch { } }
    return @{ code = $code; out = $out; json = $json }
}
function Status($run, [string]$client) { return @($run.json.clients | Where-Object { $_.client -eq $client })[0].status }
function Hash([string]$p) { if (Test-Path -LiteralPath $p) { return Get-Sha256 $p } else { return 'absent' } }
function Baks([string]$dir) { return @(Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.bak-widgets-bridge-*' }) }

try {
    # ---------- JSON engine ----------
    $j1 = "{`n  `"numStartups`": 3,`n  `"name`": `"$ru`",`n  `"mcpServers`": {`n    `"a`": {`n      `"type`": `"stdio`",`n      `"command`": `"x`",`n      `"args`": [`"--flag`", `"widgets-bridge`"]`n    }`n  },`n  `"tail`": [1, 2.50, true, null]`n}`n"
    $r = JEdit $j1
    Check 'json_add_registers' ($r.status -eq 'registered' -and $r.changed -and (Valid (S $r.bytes))) $r.status
    $o = (S $r.bytes) | ConvertFrom-Json
    Check 'json_add_entry_correct' ($o.mcpServers.'widgets-bridge'.command -eq $L -and $o.mcpServers.'widgets-bridge'.type -eq 'stdio' -and @($o.mcpServers.'widgets-bridge'.args).Count -eq 0 -and $o.mcpServers.a.command -eq 'x' -and $o.name -eq $ru) ''
    $t = S $r.bytes
    Check 'json_add_keeps_other_bytes_and_number_text' ($t.Contains('"tail": [1, 2.50, true, null]') -and $t.StartsWith($j1.Substring(0, $j1.IndexOf('    }'))) ) ''
    $r2 = JEdit $t
    Check 'json_second_run_already_no_change' ($r2.status -eq 'already' -and -not $r2.changed -and (Same $r2.bytes (B $t))) $r2.status
    $r3 = JEdit $t $true
    Check 'json_remove_restores_original_bytes' ($r3.status -eq 'removed' -and (Same $r3.bytes (B $j1))) $r3.status
    $r4 = JEdit $j1 $true
    Check 'json_remove_absent_is_noop' ($r4.status -eq 'not_registered' -and -not $r4.changed) $r4.status

    $noServers = "{`n  `"a`": 1`n}`n"
    $r = JEdit $noServers
    $o = (S $r.bytes) | ConvertFrom-Json
    Check 'json_creates_mcpServers' ($r.status -eq 'registered' -and (Valid (S $r.bytes)) -and $o.a -eq 1 -and $o.mcpServers.'widgets-bridge'.command -eq $L) ''
    $r5 = JEdit (S $r.bytes) $true
    Check 'json_remove_last_leaves_empty_object' ($r5.status -eq 'removed' -and (Valid (S $r5.bytes)) -and -not ((S $r5.bytes) | ConvertFrom-Json).mcpServers.PSObject.Properties['widgets-bridge']) ''
    foreach ($case in @(@('empty_object', '{}'), @('empty_servers', "{`"mcpServers`": {}}"), @('empty_servers_multiline', "{`n  `"mcpServers`": {`n  }`n}`n"), @('whitespace_file', "  `n"))) {
        $r = JEdit $case[1]
        Check "json_insert_$($case[0])" ($r.status -eq 'registered' -and (Valid (S $r.bytes)) -and ((S $r.bytes) | ConvertFrom-Json).mcpServers.'widgets-bridge'.command -eq $L) ''
    }
    $oneLine = '{"mcpServers":{"a":{"command":"x"}},"z":1}'
    $r = JEdit $oneLine
    Check 'json_insert_single_line_compact' ($r.status -eq 'registered' -and (Valid (S $r.bytes)) -and -not (S $r.bytes).Contains("`n") -and (JEdit (S $r.bytes) $true).status -eq 'removed' -and (S (JEdit (S $r.bytes) $true).bytes) -eq $oneLine) ''

    $mid = "{`n  `"mcpServers`": {`n    `"a`": {`"command`": `"x`"},`n    `"widgets-bridge`": {`"type`": `"stdio`", `"command`": `"$($L.Replace('\', '\\'))`", `"args`": []},`n    `"b`": {`"command`": `"y`"}`n  }`n}`n"
    $r = JEdit $mid $true
    $o = (S $r.bytes) | ConvertFrom-Json
    Check 'json_remove_middle_member' ($r.status -eq 'removed' -and (Valid (S $r.bytes)) -and $o.mcpServers.a.command -eq 'x' -and $o.mcpServers.b.command -eq 'y' -and -not $o.mcpServers.PSObject.Properties['widgets-bridge']) ''
    $r = JEdit $mid
    Check 'json_existing_equal_entry_is_already' ($r.status -eq 'already' -and -not $r.changed) $r.status
    $onlyFirst = "{`n  `"mcpServers`": {`n    `"widgets-bridge`": {`"command`": `"$($L.Replace('\', '\\'))`", `"args`": [], `"type`": `"stdio`", `"user_extra`": 5},`n    `"b`": 1`n  }`n}`n"
    Check 'json_extra_user_fields_do_not_break_already' ((JEdit $onlyFirst).status -eq 'already') ''
    $r = JEdit $onlyFirst $true
    Check 'json_remove_first_member' ($r.status -eq 'removed' -and (Valid (S $r.bytes)) -and ((S $r.bytes) | ConvertFrom-Json).mcpServers.b -eq 1) ''

    $stale = "{`n  `"mcpServers`": {`n    `"widgets-bridge`": {`"type`": `"stdio`", `"command`": `"$($L2.Replace('\', '\\'))`", `"args`": []}`n  }`n}`n"
    $r = JEdit $stale
    $o = (S $r.bytes) | ConvertFrom-Json
    Check 'json_changed_command_is_updated_in_place' ($r.status -eq 'updated' -and (Valid (S $r.bytes)) -and $o.mcpServers.'widgets-bridge'.command -eq $L) $r.status
    $r = JEdit $stale $true
    Check 'json_remove_other_install_is_kept' ($r.status -eq 'kept_other_install' -and -not $r.changed) $r.status
    $r = JEdit $stale $true $L $true
    Check 'json_remove_other_install_with_flag' ($r.status -eq 'removed' -and (Valid (S $r.bytes))) $r.status

    $decoy = "{`n  `"mcpServers`": {`n    `"a`": {`"command`": `"x`", `"args`": [`"widgets-bridge`"], `"env`": {`"widgets-bridge`": `"1`"}}`n  },`n  `"widgets-bridge`": `"top-level lookalike`"`n}`n"
    $r = JEdit $decoy
    $o = (S $r.bytes) | ConvertFrom-Json
    Check 'json_lookalikes_are_not_ours' ($r.status -eq 'registered' -and $o.'widgets-bridge' -eq 'top-level lookalike' -and $o.mcpServers.a.env.'widgets-bridge' -eq '1') ''
    Check 'json_remove_ignores_lookalikes' ((JEdit $decoy $true).status -eq 'not_registered') ''

    $crlf = "{`r`n  `"n`": `"$ru`",`r`n  `"mcpServers`": {`r`n    `"a`": {`"command`": `"x`"}`r`n  }`r`n}`r`n"
    $bomBytes = [byte[]](@(0xEF, 0xBB, 0xBF) + [byte[]](B $crlf))
    $r = Edit-JsonMcpServers -Bytes $bomBytes -Name $N -Desired (JEntry) -Launcher $L -Remove $false -AnyInstall $false
    $t = $u8.GetString($r.bytes, 3, $r.bytes.Length - 3)
    Check 'json_bom_and_crlf_preserved' ($r.status -eq 'registered' -and $r.bytes[0] -eq 0xEF -and (Valid $t) -and ($t -replace "`r`n", '').IndexOf("`n") -lt 0 -and $t.Contains($ru)) ''
    $r2 = Edit-JsonMcpServers -Bytes $r.bytes -Name $N -Desired (JEntry) -Launcher $L -Remove $true -AnyInstall $false
    Check 'json_bom_crlf_roundtrip_identical' (Same $r2.bytes $bomBytes) ''

    foreach ($bad in @(
        @('truncated', "{`"mcpServers`": {`"a`": {"), @('trailing_comma', "{`"mcpServers`": {`"a`": 1,}}"), @('comment', "{`"mcpServers`": {} // c`n}"),
        @('duplicate_server_key', "{`"mcpServers`": {`"a`": 1, `"a`": 2}}"), @('duplicate_mcpServers', "{`"mcpServers`": {}, `"mcpServers`": {}}"),
        @('mcpServers_not_object', "{`"mcpServers`": []}"), @('root_array', '[]'), @('trailing_content', '{} {}'), @('not_json', 'model = "x"'))) {
        Check "json_refused_$($bad[0])" (Throws { Edit-JsonMcpServers -Bytes (B $bad[1]) -Name $N -Desired (JEntry) -Launcher $L -Remove $false -AnyInstall $false }) ''
    }
    Check 'json_refused_invalid_utf8' (Throws { Edit-JsonMcpServers -Bytes ([byte[]](0x7B, 0xFF, 0x7D)) -Name $N -Desired (JEntry) -Launcher $L -Remove $false -AnyInstall $false }) ''

    # ---------- TOML engine ----------
    $t1 = "model = `"m`"`n`n[mcp_servers.a]`ncommand = 'x'`nargs = [`"--k`"]`n`n[windows]`nsandbox = `"s`"`n"
    $r = TEdit $t1
    $t = S $r.bytes
    Check 'toml_add_appends_block' ($r.status -eq 'registered' -and $t.StartsWith($t1) -and $t.Contains("[mcp_servers.widgets-bridge]`ncommand = '$L'`nargs = []`nstartup_timeout_sec = 20`ntool_timeout_sec = 30`n")) $r.status
    $r2 = TEdit $t
    Check 'toml_second_run_already_no_change' ($r2.status -eq 'already' -and -not $r2.changed) $r2.status
    $r3 = TEdit $t $true
    Check 'toml_remove_restores_original_bytes' ($r3.status -eq 'removed' -and (Same $r3.bytes (B $t1))) $r3.status
    $noNl = "model = `"m`""
    $r = TEdit $noNl
    Check 'toml_add_to_file_without_trailing_newline' ($r.status -eq 'registered' -and (S $r.bytes).StartsWith("model = `"m`"`n`n[mcp_servers.widgets-bridge]`n")) ''
    $r = TEdit ''
    Check 'toml_add_to_empty_file' ($r.status -eq 'registered' -and (S $r.bytes).StartsWith('[mcp_servers.widgets-bridge]') -and (S (TEdit (S $r.bytes) $true).bytes) -eq '') ''
    $c1 = $t1.Replace("`n", "`r`n")
    $r = TEdit $c1
    $t = S $r.bytes
    Check 'toml_crlf_uses_crlf' ($r.status -eq 'registered' -and ($t.Replace("`r`n", '').IndexOf("`n") -lt 0) -and (Same (TEdit $t $true).bytes (B $c1))) ''
    $bomT = [byte[]](@(0xEF, 0xBB, 0xBF) + [byte[]](B $t1))
    $r = Edit-TomlMcpServer -Bytes $bomT -Name $N -Desired (TEntry) -Launcher $L -Remove $false -AnyInstall $false
    Check 'toml_bom_preserved' ($r.status -eq 'registered' -and $r.bytes[0] -eq 0xEF) ''

    $fake = "[mcp_servers.a]`ncommand = `"x`"`nnotes = `"`"`"`n[mcp_servers.widgets-bridge]`ncommand = `"fake`"`n`"`"`"`nlist = [`n  `"[mcp_servers.widgets-bridge]`",`n  # [mcp_servers.widgets-bridge]`n]`nlit = '''`n[mcp_servers.widgets-bridge]`n'''`n# [mcp_servers.widgets-bridge]`ninline = { k = `"[mcp_servers.widgets-bridge]`" }`n"
    $r = TEdit $fake
    Check 'toml_fake_headers_in_strings_comments_arrays_ignored' ($r.status -eq 'registered' -and (S $r.bytes).StartsWith($fake)) $r.status
    Check 'toml_remove_ignores_fake_headers' ((TEdit $fake $true).status -eq 'not_registered') ''

    $q = "[mcp_servers.`"widgets-bridge`"]`ncommand = `"$($L.Replace('\', '\\'))`"`nargs = []`n"
    Check 'toml_quoted_key_header_is_same_table' ((TEdit $q).status -eq 'already') ''
    $dup = "[mcp_servers.widgets-bridge]`ncommand = 'a'`n[mcp_servers.widgets-bridge]`ncommand = 'b'`n"
    Check 'toml_refused_duplicate_blocks' ((Throws { TEdit $dup }) -and (Throws { TEdit $dup $true })) ''
    Check 'toml_refused_array_of_tables' (Throws { TEdit "[[mcp_servers.widgets-bridge]]`ncommand = 'a'`n" }) ''
    Check 'toml_refused_dotted_key_under_parent' (Throws { TEdit "[mcp_servers]`nwidgets-bridge.command = 'a'`n" }) ''
    Check 'toml_refused_inline_parent_value' (Throws { TEdit "mcp_servers = { widgets-bridge = { command = 'a' } }`n" }) ''
    Check 'toml_refused_dotted_key_at_root' (Throws { TEdit "mcp_servers.widgets-bridge.command = 'a'`n" }) ''
    Check 'toml_refused_orphan_subtable' (Throws { TEdit "[mcp_servers.widgets-bridge.env]`nK = 'v'`n" }) ''
    foreach ($bad in @(@('unterminated_string', "a = `"x`n"), @('bad_header', "[mcp_servers.x`ncommand = 'a'`n"), @('missing_equals', "command 'a'`n"), @('unterminated_multiline', "a = `"`"`"x`n"), @('unterminated_array', "a = [1, 2`n"))) {
        Check "toml_refused_$($bad[0])" (Throws { TEdit $bad[1] }) ''
    }
    Check 'toml_refused_invalid_utf8' (Throws { Edit-TomlMcpServer -Bytes ([byte[]](0x61, 0x20, 0x3D, 0x20, 0xFF)) -Name $N -Desired (TEntry) -Launcher $L -Remove $false -AnyInstall $false }) ''

    $sub = "[mcp_servers.widgets-bridge]`ncommand = '$L2'`nargs = []`n`n[mcp_servers.widgets-bridge.env]`nK = 'v'`n`n[windows]`nx = 1`n"
    $r = TEdit $sub
    $t = S $r.bytes
    Check 'toml_update_replaces_main_keeps_subtable_and_neighbours' ($r.status -eq 'updated' -and $t.Contains("command = '$L'") -and -not $t.Contains($L2) -and $t.Contains("[mcp_servers.widgets-bridge.env]`nK = 'v'") -and $t.Contains("[windows]`nx = 1")) $r.status
    Check 'toml_update_is_stable' ((TEdit $t).status -eq 'already') ''
    $r = TEdit $sub $true
    Check 'toml_remove_other_install_is_kept' ($r.status -eq 'kept_other_install' -and -not $r.changed) $r.status
    $r = TEdit $sub $true $L $true
    $t = S $r.bytes
    Check 'toml_remove_takes_block_and_its_subtables_only' ($r.status -eq 'removed' -and $t -eq "[windows]`nx = 1`n") $t
    $quoteCmd = "C:\it's\bridge\widgets-bridge-server.cmd"
    $r = TEdit '' $false $quoteCmd
    Check 'toml_command_with_apostrophe_uses_basic_string' ($r.status -eq 'registered' -and (S $r.bytes).Contains('command = "C:\\it') -and (TEdit (S $r.bytes) $false $quoteCmd).status -eq 'already') ''

    # ---------- Review fixes: comments, case, trailing newline, dates, backups ----------
    # REG-2: comments above the next table are not ours: removal and replacement keep them
    $cm = "[mcp_servers.widgets-bridge]`ncommand = '$L'`nargs = []`n`n# === Windows sandbox settings (user note) ===`n[windows]`nx = 1`n"
    $r = TEdit $cm $true
    Check 'toml_remove_keeps_comment_above_next_table' ($r.status -eq 'removed' -and (S $r.bytes) -eq "# === Windows sandbox settings (user note) ===`n[windows]`nx = 1`n") (S $r.bytes)
    $cm2 = $cm.Replace($L, $L2)
    $r = TEdit $cm2
    Check 'toml_update_keeps_comment_above_next_table' ($r.status -eq 'updated' -and (S $r.bytes) -eq "[mcp_servers.widgets-bridge]`ncommand = '$L'`nargs = []`nstartup_timeout_sec = 20`ntool_timeout_sec = 30`n`n# === Windows sandbox settings (user note) ===`n[windows]`nx = 1`n") (S $r.bytes)
    $r = TEdit "[mcp_servers.widgets-bridge]`ncommand = '$L'`nargs = []`n# trailing note`n" $true
    Check 'toml_remove_keeps_comment_at_end_of_file' ($r.status -eq 'removed' -and (S $r.bytes) -eq "# trailing note`n") (S $r.bytes)
    $r = TEdit "[mcp_servers.widgets-bridge]`r`ncommand = '$L'`r`nargs = []`r`n`r`n# note`r`n[windows]`r`nx = 1`r`n" $true
    Check 'toml_remove_keeps_comment_crlf' ($r.status -eq 'removed' -and (S $r.bytes) -eq "# note`r`n[windows]`r`nx = 1`r`n") ''
    $blank = "[mcp_servers.widgets-bridge]`ncommand = '$L'`nargs = []`n`n`n[windows]`nx = 1`n"
    Check 'toml_remove_without_comments_unchanged_rule' ((S (TEdit $blank $true).bytes) -eq "[windows]`nx = 1`n") ''

    # REG-3: the launcher path is compared without case (Windows path), as in removal
    $upJson = "{`n  `"mcpServers`": {`n    `"widgets-bridge`": {`"type`": `"stdio`", `"command`": `"$($L.ToUpper().Replace('\', '\\'))`", `"args`": []}`n  }`n}`n"
    Check 'json_launcher_case_difference_is_already' ((JEdit $upJson).status -eq 'already' -and (JEdit $upJson $true).status -eq 'removed') ''
    $upToml = "[mcp_servers.widgets-bridge]`ncommand = '$($L.ToUpper())'`nargs = []`n"
    Check 'toml_launcher_case_difference_is_already' ((TEdit $upToml).status -eq 'already' -and (TEdit $upToml $true).status -eq 'removed') ''

    # REG-5: a TOML file without a final newline is restored byte for byte
    $noNl = "model = `"m`""
    $r = TEdit $noNl
    $rt = S $r.bytes
    $rr = TEdit $rt $true
    Check 'toml_no_trailing_newline_roundtrip_identical' ($r.status -eq 'registered' -and $rt.StartsWith("model = `"m`"`n`n[mcp_servers.widgets-bridge]`n") -and -not $rt.EndsWith("`n") -and (TEdit $rt).status -eq 'already' -and (Same $rr.bytes (B $noNl))) ($rt.Length)
    $noNlOther = "[a]`nx = 1"
    $rr = TEdit (S (TEdit $noNlOther).bytes) $true
    Check 'toml_no_trailing_newline_roundtrip_after_table' (Same $rr.bytes (B $noNlOther)) ''

    # REG-6: a TOML date-time with a space inside an array is not a syntax error
    $dt = "d = [1979-05-27 07:32:00Z, 1979-05-28 07:32:00Z]`nlist = [ 1979-05-27 07:32:00Z ]`nx = 1979-05-27 07:32:00Z`n"
    $r = TEdit $dt
    Check 'toml_datetime_with_space_in_array_accepted' ($r.status -eq 'registered' -and (S $r.bytes).StartsWith($dt) -and (Same (TEdit (S $r.bytes) $true).bytes (B $dt))) $r.status

    # REG-7: a backup never overwrites an existing file and uses the next free name
    $bdir = Join-Path $WorkRoot 'bak'; New-Item -ItemType Directory -Path $bdir | Out-Null
    $bcfg = Join-Path $bdir 'c.json'
    [IO.File]::WriteAllText($bcfg, 'A', $u8)
    $b1 = New-ConfigBackup $bcfg
    [IO.File]::WriteAllText($bcfg, 'B', $u8)
    $b2 = New-ConfigBackup $bcfg
    Check 'backup_names_are_unique_and_not_overwritten' ($b1 -ne $b2 -and $b2.EndsWith('-2') -and [IO.File]::ReadAllText($b1) -eq 'A' -and [IO.File]::ReadAllText($b2) -eq 'B') "$([IO.Path]::GetFileName($b1)) $([IO.Path]::GetFileName($b2))"

    # ---------- REG-1: after the write only our OWN failed edit is rolled back ----------
    # Write-AtomicBytes and Edit-JsonMcpServers are temporarily replaced to simulate a client saving its own file.
    $realWrite = ${function:Write-AtomicBytes}
    $realEditJson = ${function:Edit-JsonMcpServers}
    $script:raceCalls = 0; $script:raceBefore = $null; $script:raceAfter = $null
    function Write-AtomicBytes([string]$Path, [byte[]]$Bytes, [scriptblock]$Guard = $null) {
        $script:raceCalls++
        if ($script:raceBefore -and $script:raceCalls -eq 1) { [IO.File]::WriteAllText($Path, $script:raceBefore, $u8) }
        $wrote = & $realWrite $Path $Bytes $Guard
        if ($script:raceAfter -and $script:raceCalls -eq 1 -and $wrote) { [IO.File]::WriteAllText($Path, $script:raceAfter, $u8) }
        return $wrote
    }
    function New-RaceHome([string]$Name) {
        $h = Join-Path $WorkRoot $Name
        New-Item -ItemType Directory -Path (Join-Path $h '.kimi-code'), (Join-Path $h '.codex') -Force | Out-Null
        return $h
    }
    function Invoke-Race($Adapter) {
        $bk = @{}
        return Invoke-ClientAdapter -A $Adapter -Name $N -Launcher $L -Remove $false -AnyInstall $false -ShouldWrite { param($p) $true } -BackedUp $bk
    }
    function Get-RaceAdapter([string]$HomeDir, [string]$Id) { return @(Get-ClientAdapters $HomeDir $null $N $L | Where-Object { $_.id -eq $Id })[0] }
    $startJson = "{`n  `"mcpServers`": {`n    `"keep`": { `"command`": `"k`" }`n  }`n}`n"
    $clientJson = "{`n  `"mcpServers`": {`n    `"client-new`": { `"command`": `"c`" }`n  }`n}`n"
    try {
        # (a) the client saves its own file right after our replace: that save must survive, status error, backup of the original
        $h = New-RaceHome 'race-a'; $p = Join-Path $h '.kimi-code\mcp.json'
        [IO.File]::WriteAllText($p, $startJson, $u8)
        $script:raceCalls = 0; $script:raceBefore = $null; $script:raceAfter = $clientJson
        $rep = Invoke-Race (Get-RaceAdapter $h 'kimi-code')
        $bak = @(Get-ChildItem -LiteralPath (Split-Path $p) -Filter '*.bak-widgets-bridge-*')
        Check 'race_client_save_after_write_is_not_overwritten' ($rep.status -eq 'error' -and -not $rep.applied -and [IO.File]::ReadAllText($p) -eq $clientJson -and $rep.detail -match 'not restored') "$($rep.status): $($rep.detail)"
        Check 'race_backup_keeps_pre_edit_original' ($bak.Count -eq 1 -and [IO.File]::ReadAllText($bak[0].FullName) -eq $startJson -and $rep.detail -match 'backup:') "backups=$($bak.Count)"
        # (b) the file did not exist: the client creates it right after our write; it must not be deleted
        $h = New-RaceHome 'race-b'; $p = Join-Path $h '.codex\config.toml'
        $clientToml = "model = `"client`"`n"
        $script:raceCalls = 0; $script:raceBefore = $null; $script:raceAfter = $clientToml
        $rep = Invoke-Race (Get-RaceAdapter $h 'codex')
        Check 'race_client_created_file_is_not_deleted' ($rep.status -eq 'error' -and (Test-Path -LiteralPath $p) -and [IO.File]::ReadAllText($p) -eq $clientToml -and $rep.detail -match 'not restored') "$($rep.status): $($rep.detail)"
        # (c) own failed edit (the written file does not pass verification): the original content is restored
        $h = New-RaceHome 'race-c'; $p = Join-Path $h '.kimi-code\mcp.json'
        [IO.File]::WriteAllText($p, $startJson, $u8)
        $script:raceCalls = 0; $script:raceBefore = $null; $script:raceAfter = $null
        Set-Item -Path function:Edit-JsonMcpServers -Value { param([byte[]]$Bytes, [string]$Name, $Desired, [string]$Launcher, [bool]$Remove, [bool]$AnyInstall, [string]$Key = 'mcpServers') return @{ status = 'registered'; bytes = [byte[]](0x7B, 0x7D); changed = $true } }
        $rep = Invoke-Race (Get-RaceAdapter $h 'kimi-code')
        Set-Item -Path function:Edit-JsonMcpServers -Value $realEditJson
        Check 'race_own_failed_edit_is_rolled_back' ($rep.status -eq 'error' -and [IO.File]::ReadAllText($p) -eq $startJson -and $rep.detail -match 'original content was restored') "$($rep.status): $($rep.detail)"
        $script:raceCalls = 0
        Set-Item -Path function:Edit-JsonMcpServers -Value { param([byte[]]$Bytes, [string]$Name, $Desired, [string]$Launcher, [bool]$Remove, [bool]$AnyInstall, [string]$Key = 'mcpServers') return @{ status = 'registered'; bytes = [byte[]](0x7B, 0x7D); changed = $true } }
        $h = New-RaceHome 'race-c3'; $p = Join-Path $h '.kimi-code\mcp.json'
        [IO.File]::WriteAllText($p, '', $u8)
        $rep = Invoke-Race (Get-RaceAdapter $h 'kimi-code')
        Set-Item -Path function:Edit-JsonMcpServers -Value $realEditJson
        Check 'race_own_failed_edit_of_empty_existing_file_restores_it' ($rep.status -eq 'error' -and (Test-Path -LiteralPath $p) -and (Get-Item -LiteralPath $p).Length -eq 0) "$($rep.status)"
        # (d) the client saves its own file but our entry is in it: no error, the file is not touched
        $h = New-RaceHome 'race-d'; $p = Join-Path $h '.kimi-code\mcp.json'
        [IO.File]::WriteAllText($p, $startJson, $u8)
        $withOurs = "{`n  `"mcpServers`": {`n    `"client-new`": { `"command`": `"c`" },`n    `"widgets-bridge`": { `"command`": `"$($L.Replace('\', '\\'))`", `"args`": [], `"startupTimeoutMs`": 20000, `"toolTimeoutMs`": 30000 }`n  }`n}`n"
        $script:raceCalls = 0; $script:raceBefore = $null; $script:raceAfter = $withOurs
        $rep = Invoke-Race (Get-RaceAdapter $h 'kimi-code')
        Check 'race_client_save_that_keeps_our_entry_is_accepted' ($rep.status -eq 'registered' -and $rep.applied -and [IO.File]::ReadAllText($p) -eq $withOurs -and $rep.detail -match 'changed by another program') "$($rep.status): $($rep.detail)"
        # (e) the client saves its file BEFORE our replace: the guard sees it, the file is re-read and the entry is added on top of it
        $h = New-RaceHome 'race-e'; $p = Join-Path $h '.kimi-code\mcp.json'
        [IO.File]::WriteAllText($p, $startJson, $u8)
        $script:raceCalls = 0; $script:raceBefore = $clientJson; $script:raceAfter = $null
        $rep = Invoke-Race (Get-RaceAdapter $h 'kimi-code')
        $bak = @(Get-ChildItem -LiteralPath (Split-Path $p) -Filter '*.bak-widgets-bridge-*')
        $o = [IO.File]::ReadAllText($p) | ConvertFrom-Json
        Check 'race_client_save_before_replace_is_merged_not_lost' ($rep.status -eq 'registered' -and $rep.applied -and $o.mcpServers.'client-new'.command -eq 'c' -and $o.mcpServers.'widgets-bridge'.command -eq $L) "$($rep.status)"
        Check 'race_stale_backup_is_dropped_only_fresh_one_kept' ($bak.Count -eq 1 -and [IO.File]::ReadAllText($bak[0].FullName) -eq $clientJson) "backups=$($bak.Count)"
        # (f) review N1: a reader holds the file without FILE_SHARE_DELETE (antivirus/indexer), File.Replace
        # retries after IOException; meanwhile the holder releases and the client saves its file. The guard runs
        # before EVERY attempt, so whatever the timing, the client's entry must survive (merged or left alone).
        Set-Item -Path function:Write-AtomicBytes -Value $realWrite
        $h = New-RaceHome 'race-f'; $p = Join-Path $h '.kimi-code\mcp.json'
        [IO.File]::WriteAllText($p, $startJson, $u8)
        $sig = Join-Path $h 'holder-ready'
        $job = Start-ThreadJob -ArgumentList $p, $clientJson, $sig -ScriptBlock {
            param($path, $txt, $sig)
            $fs = [IO.FileStream]::new($path, 'Open', 'Read', 'Read')   # no Delete/Write sharing -> Replace throws
            [IO.File]::WriteAllText($sig, '1')
            Start-Sleep -Milliseconds 300
            $fs.Dispose()
            [IO.File]::WriteAllText($path, $txt, (New-Object Text.UTF8Encoding($false)))
        }
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path -LiteralPath $sig)) { if ([DateTime]::UtcNow -gt $deadline) { throw 'race-f holder did not start' }; Start-Sleep -Milliseconds 10 }
            $rep = Invoke-Race (Get-RaceAdapter $h 'kimi-code')
        } finally { Receive-Job -Job $job -Wait -AutoRemoveJob | Out-Null }
        $o = [IO.File]::ReadAllText($p) | ConvertFrom-Json
        $clientSurvived = [bool]$o.mcpServers.PSObject.Properties['client-new']
        Check 'race_ioexception_retry_does_not_clobber_client_save' $clientSurvived "$($rep.status): client-new lost"
        Check 'race_ioexception_retry_registered_means_both_entries' ($rep.status -ne 'registered' -or ($clientSurvived -and $o.mcpServers.'widgets-bridge'.command -eq $L)) "$($rep.status)"
    } finally {
        Set-Item -Path function:Write-AtomicBytes -Value $realWrite
        Set-Item -Path function:Edit-JsonMcpServers -Value $realEditJson
    }

    # ---------- Script end to end (fictitious profiles inside WorkRoot) ----------
    function New-Profile([string]$Name) {
        $h = Join-Path $WorkRoot $Name
        New-Item -ItemType Directory -Path (Join-Path $h '.codex'), (Join-Path $h '.kimi-code'), (Join-Path $h '.gemini\config') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $h '.claude.json'), "{`n  `"numStartups`": 2,`n  `"mcpServers`": {`n    `"keep`": { `"type`": `"stdio`", `"command`": `"k`" }`n  }`n}`n", $u8)
        [IO.File]::WriteAllText((Join-Path $h '.codex\config.toml'), "model = `"m`"`n[mcp_servers.keep]`ncommand = `"k`"`n", $u8)
        [IO.File]::WriteAllText((Join-Path $h '.kimi-code\mcp.json'), "{`n  `"mcpServers`": {`n    `"keep`": { `"command`": `"k`" }`n  }`n}`n", $u8)
        [IO.File]::WriteAllText((Join-Path $h '.gemini\config\mcp_config.json'), "{`n  `"mcpServers`": {`n    `"keep`": { `"command`": `"k`", `"args`": [] }`n  }`n}`n", $u8)
        return $h
    }
    $files = @('.claude.json', '.codex\config.toml', '.kimi-code\mcp.json', '.gemini\config\mcp_config.json')
    $rootA = Join-Path $WorkRoot 'bridge-a'; $rootB = Join-Path $WorkRoot 'bridge-b'
    foreach ($rt in $rootA, $rootB) { New-Item -ItemType Directory -Path $rt | Out-Null; Set-Content -LiteralPath (Join-Path $rt 'widgets-bridge-server.cmd') -Value '@echo off' }
    $homeA = New-Profile 'home-a'
    $orig = @{}; foreach ($f in $files) { $orig[$f] = Hash (Join-Path $homeA $f) }

    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA, '-WhatIf')
    $changed = @($files | Where-Object { $orig[$_] -ne (Hash (Join-Path $homeA $_)) })
    Check 'script_whatif_changes_nothing' ($r.code -eq 0 -and $changed.Count -eq 0 -and @(Baks $homeA).Count -eq 0 -and $r.json.whatIf -eq $true -and (Status $r 'claude-code') -eq 'registered' -and @($r.json.clients | Where-Object { $_.applied }).Count -eq 0) "exit $($r.code)"

    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA)
    $st = @('claude-code', 'codex', 'kimi-code', 'antigravity', 'gemini-cli' | ForEach-Object { "$_=$(Status $r $_)" }) -join ','
    Check 'script_registers_all_found_clients' ($r.code -eq 0 -and $st -eq 'claude-code=registered,codex=registered,kimi-code=registered,antigravity=registered,gemini-cli=unsupported') $st
    Check 'script_report_has_warnings_field' ($null -ne $r.json.PSObject.Properties['warnings']) ''
    $baks = @(Baks $homeA)
    Check 'script_one_backup_per_changed_file_with_original_content' ($baks.Count -eq 4 -and @($files | Where-Object { $f = $_; @($baks | Where-Object { $_.FullName -like "$(Join-Path $homeA $f).bak-widgets-bridge-$(Get-Date -Format yyyyMMdd)*" -and (Get-Sha256 $_.FullName) -eq $orig[$f] }).Count -eq 1 }).Count -eq 4) "backups=$($baks.Count)"
    $cj = Get-Content -LiteralPath (Join-Path $homeA '.claude.json') -Raw | ConvertFrom-Json
    $kj = Get-Content -LiteralPath (Join-Path $homeA '.kimi-code\mcp.json') -Raw | ConvertFrom-Json
    $aj = Get-Content -LiteralPath (Join-Path $homeA '.gemini\config\mcp_config.json') -Raw | ConvertFrom-Json
    $launcherA = Join-Path $rootA 'widgets-bridge-server.cmd'
    Check 'script_entries_have_client_formats' ($cj.mcpServers.'widgets-bridge'.type -eq 'stdio' -and $cj.mcpServers.'widgets-bridge'.command -eq $launcherA -and $kj.mcpServers.'widgets-bridge'.command -eq $launcherA -and $kj.mcpServers.'widgets-bridge'.startupTimeoutMs -eq 20000 -and $aj.mcpServers.'widgets-bridge'.command -eq $launcherA -and -not $aj.mcpServers.'widgets-bridge'.PSObject.Properties['type'] -and $cj.mcpServers.keep.command -eq 'k' -and $kj.mcpServers.keep.command -eq 'k' -and $aj.mcpServers.keep.command -eq 'k') ''
    $afterReg = @{}; foreach ($f in $files) { $afterReg[$f] = Hash (Join-Path $homeA $f) }

    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA)
    $same = @($files | Where-Object { $afterReg[$_] -ne (Hash (Join-Path $homeA $_)) }).Count -eq 0
    Check 'script_second_run_is_idempotent' ($r.code -eq 0 -and $same -and @(Baks $homeA).Count -eq 4 -and @($r.json.clients | Where-Object { $_.status -eq 'already' }).Count -eq 4) "backups=$(@(Baks $homeA).Count)"

    $r = Run-Reg @('-InstallRoot', $rootB, '-HomeDir', $homeA, '-Remove')
    $same = @($files | Where-Object { $afterReg[$_] -ne (Hash (Join-Path $homeA $_)) }).Count -eq 0
    Check 'script_remove_of_other_install_keeps_entries' ($r.code -eq 0 -and $same -and @($r.json.clients | Where-Object { $_.status -eq 'kept_other_install' }).Count -eq 4) ''

    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA, '-Clients', 'codex', '-Remove')
    Check 'script_clients_filter' ($r.code -eq 0 -and @($r.json.clients).Count -eq 1 -and (Status $r 'codex') -eq 'removed' -and $afterReg['.claude.json'] -eq (Hash (Join-Path $homeA '.claude.json')) -and $orig['.codex\config.toml'] -eq (Hash (Join-Path $homeA '.codex\config.toml'))) ''
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA, '-Remove')
    $back = @($files | Where-Object { $orig[$_] -eq (Hash (Join-Path $homeA $_)) })
    Check 'script_remove_restores_original_files' ($r.code -eq 0 -and (Status $r 'claude-code') -eq 'removed' -and (Status $r 'codex') -eq 'not_registered' -and $back.Count -eq 4) "restored=$($back.Count)"
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA, '-Remove')
    Check 'script_remove_twice_is_noop' ($r.code -eq 0 -and @($r.json.clients | Where-Object { $_.status -eq 'not_registered' }).Count -eq 4) ''

    # entry of another installation is replaced on register, then removed with -IncludeOtherInstalls
    $r = Run-Reg @('-InstallRoot', $rootB, '-HomeDir', $homeA, '-Clients', 'claude-code')
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA, '-Clients', 'claude-code')
    $cj = Get-Content -LiteralPath (Join-Path $homeA '.claude.json') -Raw | ConvertFrom-Json
    Check 'script_register_updates_entry_of_other_install' ((Status $r 'claude-code') -eq 'updated' -and $cj.mcpServers.'widgets-bridge'.command -eq $launcherA) ''
    $r = Run-Reg @('-InstallRoot', $rootB, '-HomeDir', $homeA, '-Clients', 'claude-code', '-Remove', '-IncludeOtherInstalls')
    Check 'script_include_other_installs_removes' ((Status $r 'claude-code') -eq 'removed' -and $orig['.claude.json'] -eq (Hash (Join-Path $homeA '.claude.json'))) ''

    # damaged config: refused, file untouched, no backup, other clients still processed, exit code 1
    $homeB = New-Profile 'home-b'
    [IO.File]::WriteAllText((Join-Path $homeB '.claude.json'), '{ "mcpServers": { "keep": ', $u8)
    [IO.File]::WriteAllText((Join-Path $homeB '.kimi-code\mcp.json'), "{`"mcpServers`": {`"a`": 1, `"a`": 2}}", $u8)
    $dmgClaude = Hash (Join-Path $homeB '.claude.json'); $dmgKimi = Hash (Join-Path $homeB '.kimi-code\mcp.json')
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeB)
    Check 'script_damaged_configs_refused_without_changes' ($r.code -eq 1 -and (Status $r 'claude-code') -eq 'error' -and (Status $r 'kimi-code') -eq 'error' -and $dmgClaude -eq (Hash (Join-Path $homeB '.claude.json')) -and $dmgKimi -eq (Hash (Join-Path $homeB '.kimi-code\mcp.json')) -and @(Baks $homeB | Where-Object { $_.Name -like '.claude.json*' -or $_.Name -like 'mcp.json*' }).Count -eq 0) "exit $($r.code)"
    Check 'script_failure_does_not_stop_other_clients' ((Status $r 'codex') -eq 'registered' -and (Status $r 'antigravity') -eq 'registered') ''
    Check 'script_error_detail_has_no_file_content' (-not ($r.out -match 'keep')) ''
    # ambiguous TOML
    $homeC = New-Profile 'home-c'
    [IO.File]::WriteAllText((Join-Path $homeC '.codex\config.toml'), "[mcp_servers.widgets-bridge]`ncommand = 'a'`n[mcp_servers.widgets-bridge]`ncommand = 'b'`n", $u8)
    $dmgToml = Hash (Join-Path $homeC '.codex\config.toml')
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeC, '-Clients', 'codex')
    Check 'script_ambiguous_toml_refused' ($r.code -eq 1 -and (Status $r 'codex') -eq 'error' -and $dmgToml -eq (Hash (Join-Path $homeC '.codex\config.toml')) -and @(Baks $homeC).Count -eq 0) ''

    # no clients: nothing found, nothing created
    $homeD = Join-Path $WorkRoot 'home-d'; New-Item -ItemType Directory -Path $homeD | Out-Null
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeD)
    Check 'script_no_clients_found_creates_nothing' ($r.code -eq 0 -and @($r.json.clients | Where-Object { $_.status -eq 'not_found' }).Count -eq 4 -and @(Get-ChildItem -LiteralPath $homeD -Force -Recurse).Count -eq 0) ''
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeD)
    $g = @($r.json.clients | Where-Object { $_.client -eq 'gemini-cli' })[0]
    Check 'script_gemini_cli_is_honest_unsupported' ($g.status -eq 'unsupported' -and $g.detail -match 'not installed') ''
    # claude-code without ~/.claude.json is not created (first-run state of the client must not be forged)
    $homeE = Join-Path $WorkRoot 'home-e'; New-Item -ItemType Directory -Path (Join-Path $homeE '.claude') -Force | Out-Null
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeE, '-Clients', 'claude-code')
    Check 'script_claude_without_config_file_not_found' ((Status $r 'claude-code') -eq 'not_found' -and -not (Test-Path -LiteralPath (Join-Path $homeE '.claude.json'))) ''
    # missing launcher: registration is refused (no entry to a nonexistent file)
    $r = Run-Reg @('-InstallRoot', (Join-Path $WorkRoot 'not-installed'), '-HomeDir', $homeA)
    Check 'script_register_requires_launcher' ($r.code -ne 0 -and $r.out -match 'Launcher not found') "exit $($r.code)"
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', $homeA, '-Clients', 'nope')
    Check 'script_unknown_client_id_rejected' ($r.code -ne 0 -and $r.out -match 'Unknown client id') ''
    # a non-absolute HomeDir is refused
    $r = Run-Reg @('-InstallRoot', $rootA, '-HomeDir', 'relative\dir')
    Check 'script_relative_homedir_refused' ($r.code -ne 0 -and $r.out -match 'HomeDir must be absolute') ''
} finally {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { -not $_.ok })
Write-Output ([ordered]@{ checks = $results.Count; failed = $failed.Count } | ConvertTo-Json -Compress)
exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
