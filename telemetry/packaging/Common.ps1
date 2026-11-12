# Общие функции пакетирования Bridge. Dot-source: . "$PSScriptRoot\Common.ps1"
Set-StrictMode -Version Latest

function Assert-SafeInstallRoot([string]$Root) {
    if (-not [IO.Path]::IsPathRooted($Root)) { throw "InstallRoot must be absolute: $Root" }
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw "InstallRoot must not be a drive root: $Root" }
    $p = $full
    while ($p) {
        if (Test-Path -LiteralPath $p) {
            $it = Get-Item -LiteralPath $p -Force
            if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "InstallRoot contains a reparse point: $p" }
        }
        $parent = Split-Path -Parent $p
        if (-not $parent -or $parent -eq $p) { break }
        $p = $parent
    }
    if (Test-Path -LiteralPath $full) {
        $rp = Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($rp) { throw "InstallRoot contains a reparse point: $($rp.FullName)" }
    }
    return $full
}

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# Идентификатор версии: <semver>-<12 hex>. Только такие имена считаются версиями Bridge.
function Test-VersionId($Id) { return ($Id -is [string]) -and ($Id -cmatch '^\d+\.\d+\.\d+-[0-9a-f]{12}$') }
function Assert-VersionId($Id, [string]$What) { if (-not (Test-VersionId $Id)) { throw "$What is not a valid version id" } }

# Файлы версии (включая bridge\node_modules; кроме manifest.json в корне версии): относительные пути с '/'
function Get-ManifestFiles([string]$Dir) {
    $base = (Resolve-Path -LiteralPath $Dir).Path.TrimEnd('\')
    $list = Get-ChildItem -LiteralPath $base -Recurse -File -Force |
        Where-Object { -not ($_.Name -eq 'manifest.json' -and $_.DirectoryName -eq $base) } |
        ForEach-Object { $_.FullName.Substring($base.Length + 1).Replace('\', '/') }
    return @($list | Sort-Object -CaseSensitive)
}

# Доверенные владельцы и получатели прав на каталог установки: текущий пользователь, SYSTEM, Administrators.
function Get-TrustedSids { return @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544') }

# Новый каталог установки: защищённая DACL без наследования (код Bridge исполняется от имени клиента оператора).
function Set-PrivateDirectoryAcl([string]$Dir) {
    $acl = Get-Acl -LiteralPath $Dir
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
    foreach ($sid in (Get-TrustedSids)) {
        $id = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Dir -AclObject $acl
}

# Существующий каталог установки: владелец и все разрешающие ACE принадлежат только доверенным субъектам.
function Test-PrivateDirectory([string]$Dir) {
    $trusted = Get-TrustedSids
    $acl = Get-Acl -LiteralPath $Dir
    $problems = @()
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($trusted -notcontains $owner) { $problems += "owner $owner" }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $trusted -notcontains $rule.IdentityReference.Value) { $problems += "allow $($rule.IdentityReference.Value)" }
    }
    return @{ ok = ($problems.Count -eq 0); problems = $problems }
}
function Assert-PrivateInstallRoot([string]$Root) {
    $chk = Test-PrivateDirectory $Root
    if (-not $chk.ok) { throw "InstallRoot is accessible to other principals ($($chk.problems -join ', ')); use a private directory (default: %LOCALAPPDATA%\DesktopWidgets\bridge)." }
}

function New-ManifestObject([string]$Dir) {
    $files = foreach ($rel in (Get-ManifestFiles $Dir)) {
        [ordered]@{ path = $rel; sha256 = (Get-Sha256 (Join-Path $Dir ($rel.Replace('/', '\')))) }
    }
    return @($files)
}

function Test-Manifest([string]$VersionDir) {
    $mf = Join-Path $VersionDir 'manifest.json'
    if (-not (Test-Path -LiteralPath $mf)) { return @{ ok = $false; problems = @('manifest.json missing') } }
    $m = Get-Content -LiteralPath $mf -Raw | ConvertFrom-Json
    $problems = @()
    foreach ($f in @($m.files)) {
        $p = Join-Path $VersionDir ($f.path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $p)) { $problems += "missing: $($f.path)"; continue }
        if ((Get-Sha256 $p) -ne $f.sha256) { $problems += "hash mismatch: $($f.path)" }
    }
    $listed = @($m.files | ForEach-Object { $_.path })
    foreach ($a in (Get-ManifestFiles $VersionDir)) { if ($listed -cnotcontains $a) { $problems += "unlisted: $a" } }
    if (-not (Test-Path -LiteralPath (Join-Path $VersionDir 'bridge\node_modules'))) { $problems += 'bridge\node_modules missing' }
    if ($m.id -ne (Split-Path -Leaf $VersionDir)) { $problems += 'manifest id differs from directory name' }
    return @{ ok = ($problems.Count -eq 0); problems = $problems }
}

function Write-AtomicText([string]$Path, [string]$Text) {
    $tmp = "$Path.tmp-$([guid]::NewGuid().ToString('N'))"
    [IO.File]::WriteAllText($tmp, $Text, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($tmp, $Path, [NullString]::Value) } else { [IO.File]::Move($tmp, $Path) }
}

function Write-Launchers([string]$Root, [string]$VersionId) {
    # %~dp0 вместо абсолютного пути: ASCII-файл, безопасно для не-ASCII путей установки
    Write-AtomicText (Join-Path $Root 'bridge-server.cmd') "@echo off`r`nnode `"%~dp0versions\$VersionId\bridge\server.mjs`" %*`r`n"
    Write-AtomicText (Join-Path $Root 'bridge-cli.cmd') "@echo off`r`nnode `"%~dp0versions\$VersionId\bridge\cli.mjs`" %*`r`n"
}

function Read-Current([string]$Root) {
    $p = Join-Path $Root 'current.json'
    if (Test-Path -LiteralPath $p) { return Get-Content -LiteralPath $p -Raw | ConvertFrom-Json }
    return $null
}
