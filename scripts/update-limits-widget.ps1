<#
  Reversible package-manifest updater for AI Limits (LimitsWidget).
  - Requires a concrete local target installation path (or explicit -ExtractTo directory).
  - Validates archive authenticity (-ExpectedPackageSha256 or matching entry in adjacent SHA256SUMS.txt),
    package-manifest.json, and all file SHA-256 hashes before any extraction or target mutation.
  - Requests graceful-only application shutdown for processes whose executable path strictly matches
    <TargetPath>\LimitsWidget.exe (verified by PID, ExecutablePath, and CreationDate). Never touches
    foreign LimitsWidget instances or external/child user applications (browsers, editors, CLIs), and
    never falls back to forced process termination. Aborts before target mutation if graceful exit
    cannot be confirmed within -GracefulStopTimeoutSeconds.
  - Stops the target application before hashing/backing up config.json and state.json so shutdown
    state saves complete cleanly without races.
  - Preserves existing config.json and state.json (including all unknown fields and post-update edits
    during rollback) and unknown user files.
  - Safely archives known legacy source/build items (src, assets, tools, fixtures, build.ps1,
    app.manifest, README.md) under .limits-widget-legacy\ rather than destroying them.
  - Verifies strict absolute path containment and absence of reparse points before any recursive move/delete.
  - Supports -DryRun, -ExtractTo, -InjectFailure (automatic rollback verification), and -RollbackFromBackup.
#>
[CmdletBinding()]
param(
    [string]$TargetPath,
    [string]$PackagePath,
    [string]$ExpectedPackageSha256,
    [string]$BackupRoot,
    [string]$RollbackFromBackup,
    [string]$ExtractTo,
    [switch]$DryRun,
    [switch]$RestartWidget,
    [ValidateRange(1, 120)]
    [int]$GracefulStopTimeoutSeconds = 10,
    [ValidateSet('None', 'AfterStopBeforeMutate', 'AfterArchiveLegacy', 'MidFileUpdate', 'AfterFileUpdateValidation')]
    [string]$InjectFailure = 'None',
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

if (-not ('LimitsWidgetUpdaterNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LimitsWidgetUpdaterNative
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    public static int RequestGracefulWindowClose(int targetPid, bool allowGlobalExitEvent)
    {
        int posted = 0;
        var targetWindows = new List<IntPtr>();
        var fallbackWindows = new List<IntPtr>();

        EnumWindows((hWnd, lParam) =>
        {
            uint winPid;
            GetWindowThreadProcessId(hWnd, out winPid);
            if ((int)winPid != targetPid) return true;

            var clsSb = new StringBuilder(256);
            GetClassNameW(hWnd, clsSb, clsSb.Capacity);
            string cls = clsSb.ToString();

            var titleSb = new StringBuilder(256);
            GetWindowTextW(hWnd, titleSb, titleSb.Capacity);
            string title = titleSb.ToString();

            // Primary WPF WidgetWindow / SubscriptionsDialog / graceful test fixture window handles
            if (cls.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase) ||
                cls.StartsWith("LimitsWidget", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(title, "\u041B\u0438\u043C\u0438\u0442\u044B \u043D\u0435\u0439\u0440\u043E\u0441\u0435\u0442\u0435\u0439", StringComparison.Ordinal) ||
                string.Equals(title, "LimitsWidget", StringComparison.OrdinalIgnoreCase))
            {
                targetWindows.Add(hWnd);
            }
            else if (!cls.StartsWith("GDI+ Hook", StringComparison.OrdinalIgnoreCase) &&
                     !cls.StartsWith(".NET-BroadcastEventWindow", StringComparison.OrdinalIgnoreCase) &&
                     !cls.StartsWith("CicMarshalWndClass", StringComparison.OrdinalIgnoreCase) &&
                     !cls.StartsWith("IME", StringComparison.OrdinalIgnoreCase) &&
                     !cls.StartsWith("MSCTFIME", StringComparison.OrdinalIgnoreCase))
            {
                fallbackWindows.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in targetWindows)
        {
            if (PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) posted++;
        }
        if (posted == 0)
        {
            foreach (var hWnd in fallbackWindows)
            {
                if (PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) posted++;
            }
        }

        // Also signal per-PID graceful exit event if opened by the target process
        try
        {
            using (var pidEvt = EventWaitHandle.OpenExisting("Local\\LimitsWidget.GracefulExit." + targetPid))
            {
                pidEvt.Set();
                posted++;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch
        {
        }

        // Only signal global single-instance exit event when verified no foreign LimitsWidget.exe is running
        if (allowGlobalExitEvent)
        {
            try
            {
                using (var globalEvt = EventWaitHandle.OpenExisting("Local\\LimitsWidget.Exit"))
                {
                    globalEvt.Set();
                    posted++;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch
            {
            }
        }

        return posted;
    }
}
'@
}

$OwnershipMarkerName = '.limits-widget-install'
$OwnershipMarkerContent = 'LimitsWidget owned installation v1'
$AllowedPackageFiles = @(
    'LimitsWidget.exe',
    'config.json',
    'config.example.json',
    'LICENSE',
    'START-HERE.txt',
    'update-limits-widget.ps1'
)
$ReplacePackageFiles = @(
    'LimitsWidget.exe',
    'config.example.json',
    'LICENSE',
    'START-HERE.txt',
    'update-limits-widget.ps1'
)
$KnownLegacyItems = @(
    'src',
    'assets',
    'tools',
    'fixtures',
    'build.ps1',
    'app.manifest',
    'README.md'
)
$BackupCandidateFiles = @(
    'LimitsWidget.exe',
    'config.json',
    'config.example.json',
    'state.json',
    'LICENSE',
    'START-HERE.txt',
    'update-limits-widget.ps1',
    'package-manifest.json',
    '.limits-widget-install',
    'widget.log',
    'widget.log.1'
)
$UserRuntimePreserveFiles = @(
    'config.json',
    'state.json',
    'widget.log',
    'widget.log.1'
)

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-LowerSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $sha.Dispose()
    }
}

function Test-ValidJsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $raw = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($raw)) { return $false }
        $parsed = $raw | ConvertFrom-Json
        return ($null -ne $parsed)
    } catch {
        return $false
    }
}

function Write-UpdaterReceipt($Data) {
    if ([string]::IsNullOrWhiteSpace($ReceiptPath)) { return }
    $fullReceipt = [IO.Path]::GetFullPath($ReceiptPath)
    $parent = Split-Path -Parent $fullReceipt
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json = $Data | ConvertTo-Json -Depth 10
    Write-Utf8NoBom -Path $fullReceipt -Content $json
}

function Assert-NoReparsePoint([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try {
        if (Test-Path -LiteralPath $Path) {
            $item = Get-Item -LiteralPath $Path -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point (symlink/junction) rejected at path: $Path"
            }
        }
    } catch [IO.FileNotFoundException] {
    } catch [IO.DirectoryNotFoundException] {
    }
}

function Assert-NoReparsePointsInAncestors([string]$FullPath) {
    $current = [IO.DirectoryInfo]::new($FullPath)
    while ($null -ne $current) {
        if ($current.Exists -and (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Reparse point rejected in ancestor path: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Assert-NoReparsePointsInTree([string]$RootPath) {
    Assert-NoReparsePoint -Path $RootPath
    if (-not (Test-Path -LiteralPath $RootPath)) { return }
    $rootItem = Get-Item -LiteralPath $RootPath -Force
    if (-not $rootItem.PSIsContainer) { return }
    foreach ($entry in Get-ChildItem -LiteralPath $RootPath -Force -Recurse) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse point rejected inside directory tree: $($entry.FullName)"
        }
    }
}

function Assert-StrictChildPath([string]$ParentDir, [string]$ChildPath) {
    if ([string]::IsNullOrWhiteSpace($ParentDir) -or [string]::IsNullOrWhiteSpace($ChildPath)) {
        throw 'Containment check requires non-empty parent and child paths.'
    }
    $fullParent = [IO.Path]::GetFullPath($ParentDir).TrimEnd('\', '/')
    $fullChild = [IO.Path]::GetFullPath($ChildPath).TrimEnd('\', '/')
    if ($fullParent -notmatch '^[A-Za-z]:\\' -or $fullChild -notmatch '^[A-Za-z]:\\') {
        throw "Containment check requires rooted local drive paths: parent='$fullParent' child='$fullChild'"
    }
    $prefix = $fullParent + '\'
    if ($fullChild.Length -le $prefix.Length -or -not $fullChild.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Containment check failed: '$fullChild' is not strictly inside '$fullParent'."
    }
    Assert-NoReparsePointsInAncestors -FullPath $fullParent
    $cursor = $fullChild
    while ($cursor.Length -gt $fullParent.Length) {
        Assert-NoReparsePoint -Path $cursor
        $cursor = Split-Path -Parent $cursor
        if ([string]::IsNullOrEmpty($cursor)) { break }
    }
}

function Remove-ContainedPath([string]$ParentDir, [string]$ChildPath) {
    Assert-StrictChildPath -ParentDir $ParentDir -ChildPath $ChildPath
    Assert-NoReparsePointsInTree -RootPath $ChildPath
    if (Test-Path -LiteralPath $ChildPath) {
        Remove-Item -LiteralPath $ChildPath -Recurse -Force
    }
}

function Move-ContainedPath([string]$SourceParentDir, [string]$SourcePath, [string]$DestParentDir, [string]$DestPath) {
    Assert-StrictChildPath -ParentDir $SourceParentDir -ChildPath $SourcePath
    Assert-StrictChildPath -ParentDir $DestParentDir -ChildPath $DestPath
    Assert-NoReparsePointsInTree -RootPath $SourcePath
    Assert-NoReparsePoint -Path $DestPath
    $destParent = Split-Path -Parent $DestPath
    if (-not (Test-Path -LiteralPath $destParent)) {
        New-Item -ItemType Directory -Path $destParent -Force | Out-Null
    }
    Move-Item -LiteralPath $SourcePath -Destination $DestPath -Force
}

function Assert-SafeDirectoryPath([string]$RawPath, [string]$ParameterName, [switch]$AllowExtractDir) {
    if ([string]::IsNullOrWhiteSpace($RawPath)) {
        throw "Parameter -$ParameterName requires a concrete local directory path."
    }
    $trimmed = $RawPath.Trim()
    if ($trimmed.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "UNC/network paths are not allowed for -$ParameterName."
    }
    if ($trimmed -match '^[A-Za-z]:(?![\\/])') {
        throw "Drive-relative paths are not allowed for -$ParameterName."
    }
    if (-not [IO.Path]::IsPathRooted($trimmed)) {
        throw "Relative paths are not allowed for -$ParameterName; provide an absolute local path."
    }
    $root = [IO.Path]::GetPathRoot($trimmed)
    if ([string]::IsNullOrEmpty($root) -or $root.Length -lt 3 -or $root -notmatch '^[A-Za-z]:\\$') {
        throw "Path for -$ParameterName must be on a fixed local drive."
    }
    $full = [IO.Path]::GetFullPath($trimmed).TrimEnd('\', '/')
    if ([string]::Equals($full, $root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Volume root '$full' is not allowed for -$ParameterName."
    }
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        throw "Path '$full' points to an existing file, not a directory."
    }
    Assert-NoReparsePointsInAncestors -FullPath $full
    Assert-NoReparsePoint -Path $full

    $reservedFolders = @(
        [Environment+SpecialFolder]::UserProfile,
        [Environment+SpecialFolder]::DesktopDirectory,
        [Environment+SpecialFolder]::MyDocuments,
        [Environment+SpecialFolder]::LocalApplicationData,
        [Environment+SpecialFolder]::ApplicationData,
        [Environment+SpecialFolder]::ProgramFiles,
        [Environment+SpecialFolder]::ProgramFilesX86,
        [Environment+SpecialFolder]::CommonApplicationData,
        [Environment+SpecialFolder]::Windows,
        [Environment+SpecialFolder]::System,
        [Environment+SpecialFolder]::Programs,
        [Environment+SpecialFolder]::Startup,
        [Environment+SpecialFolder]::MyMusic,
        [Environment+SpecialFolder]::MyPictures,
        [Environment+SpecialFolder]::MyVideos
    )
    foreach ($sf in $reservedFolders) {
        $reservedPath = [Environment]::GetFolderPath($sf)
        if (-not [string]::IsNullOrEmpty($reservedPath)) {
            $normReserved = [IO.Path]::GetFullPath($reservedPath).TrimEnd('\', '/')
            if ([string]::Equals($full, $normReserved, [StringComparison]::OrdinalIgnoreCase)) {
                throw "System or user root folder '$full' cannot be used as -$ParameterName; choose a dedicated subfolder."
            }
        }
    }
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if (-not [string]::IsNullOrEmpty($userProfile)) {
        $downloads = [IO.Path]::GetFullPath((Join-Path $userProfile 'Downloads')).TrimEnd('\', '/')
        if ([string]::Equals($full, $downloads, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Downloads folder '$full' cannot be used as -$ParameterName; choose a dedicated subfolder."
        }
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ([string]::Equals($full, $tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Temp root folder '$full' cannot be used as -$ParameterName; choose a dedicated subfolder."
    }

    if (-not $AllowExtractDir) {
        if (Test-Path -LiteralPath (Join-Path $full '.git')) {
            throw "Git repository checkout '$full' cannot be used as an installation target."
        }
        $checkParent = Split-Path -Parent $full
        while (-not [string]::IsNullOrEmpty($checkParent)) {
            if ((Test-Path -LiteralPath (Join-Path $checkParent '.git')) -and (Test-Path -LiteralPath (Join-Path $checkParent 'windows\limits-widget\build.ps1'))) {
                $sourceWidgetDir = [IO.Path]::GetFullPath((Join-Path $checkParent 'windows\limits-widget')).TrimEnd('\', '/')
                if ([string]::Equals($full, $sourceWidgetDir, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Source checkout directory '$full' cannot be used as an installation target."
                }
            }
            $nextParent = Split-Path -Parent $checkParent
            if ($nextParent -eq $checkParent) { break }
            $checkParent = $nextParent
        }
    }

    return $full
}

function Get-TargetRollbackProvenance([string]$FullTargetDir, [string]$CustomBackupRoot = $null) {
    if (-not (Test-Path -LiteralPath $FullTargetDir -PathType Container)) { return $null }
    Assert-NoReparsePoint -Path $FullTargetDir

    $allowedPostRollbackNames = @('.limits-widget-backups', 'config.json', 'state.json', 'widget.log', 'widget.log.1')
    $entries = @(Get-ChildItem -LiteralPath $FullTargetDir -Force)
    if ($entries.Count -eq 0) { return $null }

    foreach ($entry in $entries) {
        if ($allowedPostRollbackNames -notcontains $entry.Name) {
            return $null
        }
        Assert-NoReparsePointsInTree -RootPath $entry.FullName
    }

    $cfgCandidate = Join-Path $FullTargetDir 'config.json'
    if ((Test-Path -LiteralPath $cfgCandidate) -and -not (Test-ValidJsonFile -Path $cfgCandidate)) {
        return $null
    }
    $stateCandidate = Join-Path $FullTargetDir 'state.json'
    if ((Test-Path -LiteralPath $stateCandidate) -and -not (Test-ValidJsonFile -Path $stateCandidate)) {
        return $null
    }

    $candidateBackupRoots = @()
    if (-not [string]::IsNullOrWhiteSpace($CustomBackupRoot)) {
        try {
            $safeCustomRoot = Assert-SafeDirectoryPath -RawPath $CustomBackupRoot -ParameterName 'BackupRoot' -AllowExtractDir
            if (Test-Path -LiteralPath $safeCustomRoot -PathType Container) {
                $candidateBackupRoots += $safeCustomRoot
            }
        } catch {
        }
    }
    $inTargetBackupRoot = Join-Path $FullTargetDir '.limits-widget-backups'
    if (Test-Path -LiteralPath $inTargetBackupRoot -PathType Container) {
        $candidateBackupRoots += $inTargetBackupRoot
    }

    foreach ($bRoot in $candidateBackupRoots) {
        Assert-NoReparsePointsInTree -RootPath $bRoot
        $backupDirs = @(Get-ChildItem -LiteralPath $bRoot -Directory -Filter 'backup-*' -Force -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        foreach ($bDir in $backupDirs) {
            $bmPath = Join-Path $bDir.FullName 'backup-manifest.json'
            if (-not (Test-Path -LiteralPath $bmPath -PathType Leaf)) { continue }
            try {
                $bm = [IO.File]::ReadAllText($bmPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
                if ($null -eq $bm -or $bm.schemaVersion -ne '1.0' -or [string]::IsNullOrWhiteSpace([string]$bm.targetPath)) {
                    continue
                }
                $bmTarget = [IO.Path]::GetFullPath([string]$bm.targetPath).TrimEnd('\', '/')
                if (-not [string]::Equals($bmTarget, $FullTargetDir, [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                $missingList = @($bm.missingBeforeUpdate | ForEach-Object { [string]$_ })
                if (($missingList -contains 'LimitsWidget.exe') -and ($missingList -contains $OwnershipMarkerName)) {
                    return [pscustomobject]@{
                        BackupDir          = $bDir.FullName
                        Manifest           = $bm
                        SeededConfigSha256 = if ($bm.PSObject.Properties['seededConfigSha256'] -and -not [string]::IsNullOrWhiteSpace([string]$bm.seededConfigSha256)) { ([string]$bm.seededConfigSha256).ToLowerInvariant() } else { $null }
                    }
                }
            } catch {
                continue
            }
        }
    }

    return $null
}

function Test-OwnedOrVerifiedLimitsInstall([string]$FullTargetDir, [string]$CustomBackupRoot = $null) {
    $markerPath = Join-Path $FullTargetDir $OwnershipMarkerName
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        Assert-NoReparsePoint -Path $markerPath
        $content = [IO.File]::ReadAllText($markerPath, [Text.Encoding]::UTF8).Trim()
        if ($content -eq $OwnershipMarkerContent) { return $true }
    }
    $exePath = Join-Path $FullTargetDir 'LimitsWidget.exe'
    if (Test-Path -LiteralPath $exePath -PathType Leaf) {
        Assert-NoReparsePoint -Path $exePath
        try {
            $vi = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
            if ($vi.ProductName -eq 'LimitsWidget') { return $true }
        } catch {
            return $false
        }
    }
    if ($null -ne (Get-TargetRollbackProvenance -FullTargetDir $FullTargetDir -CustomBackupRoot $CustomBackupRoot)) {
        return $true
    }
    return $false
}

function Assert-TargetInstallationDirectory([string]$FullTargetDir, [string]$CustomBackupRoot = $null) {
    if (-not (Test-Path -LiteralPath $FullTargetDir)) { return }
    $entries = @(Get-ChildItem -LiteralPath $FullTargetDir -Force)
    if ($entries.Count -eq 0) { return }
    if (-not (Test-OwnedOrVerifiedLimitsInstall -FullTargetDir $FullTargetDir -CustomBackupRoot $CustomBackupRoot)) {
        throw "Target directory '$FullTargetDir' is non-empty and is not a verified LimitsWidget installation."
    }
    foreach ($name in ($AllowedPackageFiles + $KnownLegacyItems + @('package-manifest.json', $OwnershipMarkerName, 'state.json', 'widget.log', 'widget.log.1', '.limits-widget-backups', '.limits-widget-legacy'))) {
        $candidate = Join-Path $FullTargetDir $name
        if (Test-Path -LiteralPath $candidate) {
            Assert-NoReparsePointsInTree -RootPath $candidate
        }
    }
}

function Assert-SanitizedPublicConfig([string]$ConfigFilePath) {
    $raw = [IO.File]::ReadAllText($ConfigFilePath, [Text.Encoding]::UTF8)
    $cfg = $raw | ConvertFrom-Json
    if ($null -eq $cfg) { throw "Package config '$ConfigFilePath' is not valid JSON." }
    if ($cfg.allowLocalTokens -ne $false) {
        throw "Package config '$ConfigFilePath' must have allowLocalTokens = false."
    }
    if ($null -eq $cfg.services) {
        throw "Package config '$ConfigFilePath' is missing 'services' object."
    }
    foreach ($reqSvc in @('claude', 'kimi', 'antigravity', 'codex')) {
        $prop = $cfg.services.PSObject.Properties[$reqSvc]
        if ($null -eq $prop -or $null -eq $prop.Value -or $prop.Value.enabled -ne $false) {
            throw "Package config '$ConfigFilePath' must have services.$reqSvc.enabled = false."
        }
        if ($prop.Value.PSObject.Properties['command'] -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value.command)) {
            throw "Package config '$ConfigFilePath' must not ship a personal CLI command path for $reqSvc."
        }
    }
}

function Expand-AndValidatePackage([string]$ResolvedPackageInput, [string]$StagingBaseDir) {
    $stageExtract = Join-Path $StagingBaseDir 'extracted'
    New-Item -ItemType Directory -Path $stageExtract -Force | Out-Null

    if (Test-Path -LiteralPath $ResolvedPackageInput -PathType Leaf) {
        Assert-NoReparsePoint -Path $ResolvedPackageInput
        $actualZipHash = Get-LowerSha256 -Path $ResolvedPackageInput
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageSha256)) {
            $trimmedExpected = $ExpectedPackageSha256.Trim().ToLowerInvariant()
            if ($trimmedExpected -notmatch '^[a-f0-9]{64}$') {
                throw "Invalid -ExpectedPackageSha256 value '$ExpectedPackageSha256'; expected 64 hexadecimal characters."
            }
            if ($actualZipHash -ne $trimmedExpected) {
                throw "Package archive SHA-256 mismatch: expected $trimmedExpected, got $actualZipHash"
            }
        } else {
            $adjacentSums = Join-Path (Split-Path -Parent $ResolvedPackageInput) 'SHA256SUMS.txt'
            if (-not (Test-Path -LiteralPath $adjacentSums -PathType Leaf)) {
                throw "Package archive authenticity cannot be verified: provide -ExpectedPackageSha256 or place SHA256SUMS.txt next to '$ResolvedPackageInput'."
            }
            Assert-NoReparsePoint -Path $adjacentSums
            $zipName = [IO.Path]::GetFileName($ResolvedPackageInput)
            $matchedSumEntry = $false
            foreach ($line in Get-Content -LiteralPath $adjacentSums) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -match '^([a-fA-F0-9]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $zipName) {
                    $matchedSumEntry = $true
                    $expectedFromSums = $Matches[1].ToLowerInvariant()
                    if ($actualZipHash -ne $expectedFromSums) {
                        throw "Package archive SHA-256 mismatch against adjacent SHA256SUMS.txt: expected $expectedFromSums, got $actualZipHash"
                    }
                }
            }
            if (-not $matchedSumEntry) {
                throw "Package archive '$zipName' is not listed in adjacent SHA256SUMS.txt ('$adjacentSums')."
            }
        }

        $pkgRoot = Join-Path $stageExtract 'limits-widget'
        New-Item -ItemType Directory -Path $pkgRoot -Force | Out-Null
        Assert-StrictChildPath -ParentDir $stageExtract -ChildPath $pkgRoot

        $zip = [IO.Compression.ZipFile]::OpenRead($ResolvedPackageInput)
        try {
            $expectedZipEntries = ($AllowedPackageFiles + @('package-manifest.json')) | ForEach-Object { "limits-widget/$_" }
            $seenFileEntries = @{}
            foreach ($entry in $zip.Entries) {
                $rawEntryName = [string]$entry.FullName
                if ([string]::IsNullOrWhiteSpace($rawEntryName) -or $rawEntryName -match '[\x00-\x1F]') {
                    throw "Invalid control characters in package archive entry: '$rawEntryName'"
                }
                $normEntry = $rawEntryName.Replace('\', '/')
                if ($normEntry.StartsWith('/', [StringComparison]::Ordinal) -or $normEntry.Contains('..') -or $normEntry.Contains(':')) {
                    throw "Unsafe path traversal or rooted entry in package archive: '$rawEntryName'"
                }
                if ($normEntry.EndsWith('/', [StringComparison]::Ordinal)) {
                    if ($normEntry -ne 'limits-widget/') {
                        throw "Unallowlisted directory entry in package archive: '$rawEntryName'"
                    }
                    continue
                }
                if ($expectedZipEntries -notcontains $normEntry) {
                    throw "Unallowlisted file entry in package archive: '$rawEntryName'"
                }
                if ($seenFileEntries.ContainsKey($normEntry)) {
                    throw "Duplicate file entry in package archive: '$rawEntryName'"
                }
                $seenFileEntries[$normEntry] = $entry
            }

            if ($seenFileEntries.Count -ne $expectedZipEntries.Count) {
                throw "Package archive file entry count mismatch: expected $($expectedZipEntries.Count), got $($seenFileEntries.Count)."
            }

            foreach ($normEntry in $expectedZipEntries) {
                $zipEntry = $seenFileEntries[$normEntry]
                $leafName = $normEntry.Substring('limits-widget/'.Length)
                $destPath = Join-Path $pkgRoot $leafName
                Assert-StrictChildPath -ParentDir $pkgRoot -ChildPath $destPath
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($zipEntry, $destPath, $true)
            }
        } finally {
            $zip.Dispose()
        }
    } elseif (Test-Path -LiteralPath $ResolvedPackageInput -PathType Container) {
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageSha256)) {
            throw '-ExpectedPackageSha256 requires -PackagePath to point to a .zip archive file.'
        }
        $candidateSub = Join-Path $ResolvedPackageInput 'limits-widget'
        $pkgRoot = if (Test-Path -LiteralPath (Join-Path $candidateSub 'package-manifest.json')) { $candidateSub } else { $ResolvedPackageInput }
    } else {
        throw "Package path '$ResolvedPackageInput' does not exist."
    }

    Assert-NoReparsePointsInTree -RootPath $pkgRoot
    $manifestPath = Join-Path $pkgRoot 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Missing package-manifest.json in '$pkgRoot'."
    }

    $actualFilesInRoot = @(Get-ChildItem -LiteralPath $pkgRoot -Force | ForEach-Object { $_.Name })
    $expectedRootNames = $AllowedPackageFiles + @('package-manifest.json')
    if ($actualFilesInRoot.Count -ne $expectedRootNames.Count) {
        throw "Package directory '$pkgRoot' has $($actualFilesInRoot.Count) entries; expected exact allowlist of $($expectedRootNames.Count)."
    }
    foreach ($name in $actualFilesInRoot) {
        if ($expectedRootNames -notcontains $name) {
            throw "Unallowlisted file in package directory: $name"
        }
    }

    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($null -eq $manifest -or $manifest.schemaVersion -ne '1.0' -or $manifest.product -ne 'LimitsWidget') {
        throw 'Invalid package-manifest.json schemaVersion or product.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.version) -or $manifest.executable -ne 'LimitsWidget.exe') {
        throw 'Invalid package-manifest.json version or executable field.'
    }
    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -ne $AllowedPackageFiles.Count) {
        throw "package-manifest.json files count ($($manifestFiles.Count)) does not match allowlist count ($($AllowedPackageFiles.Count))."
    }

    $seenPaths = @{}
    foreach ($entry in $manifestFiles) {
        $rel = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($rel) -or $rel.Contains('..') -or $rel.Contains(':') -or $rel.Contains('\') -or $rel.Contains('/')) {
            throw "Invalid file path in package-manifest.json: '$rel'"
        }
        if ($AllowedPackageFiles -notcontains $rel) {
            throw "File '$rel' in package-manifest.json is not in the allowlist."
        }
        if ($seenPaths.ContainsKey($rel)) {
            throw "Duplicate file '$rel' in package-manifest.json."
        }
        $seenPaths[$rel] = $true

        $filePath = Join-Path $pkgRoot $rel
        Assert-StrictChildPath -ParentDir $pkgRoot -ChildPath $filePath
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Missing allowlisted file '$rel' in package."
        }
        $item = Get-Item -LiteralPath $filePath -Force
        if ([int64]$entry.size -ne [int64]$item.Length) {
            throw "Size mismatch for '$rel': manifest says $($entry.size), actual is $($item.Length)."
        }
        $hash = Get-LowerSha256 -Path $filePath
        if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "SHA-256 mismatch for '$rel': manifest says $($entry.sha256), actual is $hash."
        }
        $expectedMode = if ($rel -eq 'config.json') { 'seed-if-missing' } else { 'replace' }
        if ([string]$entry.installMode -ne $expectedMode) {
            throw "Unexpected installMode '$($entry.installMode)' for '$rel'; expected '$expectedMode'."
        }
    }

    $exePath = Join-Path $pkgRoot 'LimitsWidget.exe'
    $vi = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    if ($vi.ProductName -ne 'LimitsWidget') {
        throw "Packaged LimitsWidget.exe ProductName is '$($vi.ProductName)', expected 'LimitsWidget'."
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$manifest.executableVersion) -and $vi.FileVersion -ne [string]$manifest.executableVersion) {
        throw "Packaged LimitsWidget.exe FileVersion ($($vi.FileVersion)) does not match manifest executableVersion ($($manifest.executableVersion))."
    }

    Assert-SanitizedPublicConfig -ConfigFilePath (Join-Path $pkgRoot 'config.json')
    Assert-SanitizedPublicConfig -ConfigFilePath (Join-Path $pkgRoot 'config.example.json')
    if ((Get-LowerSha256 -Path (Join-Path $pkgRoot 'config.json')) -ne (Get-LowerSha256 -Path (Join-Path $pkgRoot 'config.example.json'))) {
        throw 'Packaged config.json and config.example.json must have identical hashes.'
    }

    return [pscustomobject]@{
        PackageRoot = $pkgRoot
        Manifest    = $manifest
        ManifestRaw = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8)
    }
}

function Get-TargetProcessState([string]$FullTargetDir) {
    $targetExe = [IO.Path]::GetFullPath((Join-Path $FullTargetDir 'LimitsWidget.exe'))
    $targetPrefix = $FullTargetDir.TrimEnd('\', '/') + '\'
    $allCim = @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue)
    $ownedRoots = @()
    $ownedTargetExes = @()
    $foreignLimits = @()

    foreach ($p in $allCim) {
        $procPid = [int]$p.ProcessId
        if ($procPid -eq $PID) { continue }
        $exe = [string]$p.ExecutablePath
        $name = [string]$p.Name
        if (-not [string]::IsNullOrEmpty($exe)) {
            $fullExe = $null
            try { $fullExe = [IO.Path]::GetFullPath($exe) } catch { $fullExe = $exe }
            if ([string]::Equals($fullExe, $targetExe, [StringComparison]::OrdinalIgnoreCase)) {
                $ownedRoots += $p
                $ownedTargetExes += $p
            } elseif ($fullExe.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $ownedTargetExes += $p
            } elseif ($name -match '^LimitsWidget(\.exe)?$') {
                $foreignLimits += $p
            }
        } elseif ($name -match '^LimitsWidget(\.exe)?$') {
            $foreignLimits += $p
        }
    }

    return [pscustomobject]@{
        TargetExe         = $targetExe
        OwnedRootPids     = @($ownedRoots | ForEach-Object { [int]$_.ProcessId })
        OwnedRootRecords  = $ownedRoots
        OwnedTreePids     = @($ownedTargetExes | ForEach-Object { [int]$_.ProcessId })
        OwnedTreeRecords  = $ownedTargetExes
        ForeignLimitsPids = @($foreignLimits | ForEach-Object { [int]$_.ProcessId })
    }
}

function Stop-OwnedTargetProcessesGracefully([string]$FullTargetDir, [int]$TimeoutSeconds = 10) {
    $initialState = Get-TargetProcessState -FullTargetDir $FullTargetDir
    if ($initialState.OwnedTreePids.Count -eq 0) {
        return $initialState
    }

    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
    while ([DateTime]::UtcNow -lt $deadline) {
        $currentState = Get-TargetProcessState -FullTargetDir $FullTargetDir
        if ($currentState.OwnedTreePids.Count -eq 0) {
            Start-Sleep -Milliseconds 100
            return $initialState
        }

        $allowGlobalExit = ($currentState.ForeignLimitsPids.Count -eq 0 -and $currentState.OwnedRootPids.Count -gt 0)
        foreach ($rec in $currentState.OwnedTreeRecords) {
            $pidToStop = [int]$rec.ProcessId
            if ($pidToStop -eq $PID) { continue }
            try {
                $liveCim = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $pidToStop" -ErrorAction SilentlyContinue
                if ($null -eq $liveCim) { continue }
                if ($null -ne $rec.CreationDate -and $null -ne $liveCim.CreationDate -and $liveCim.CreationDate -ne $rec.CreationDate) {
                    continue
                }
                $liveExe = [string]$liveCim.ExecutablePath
                if ([string]::IsNullOrEmpty($liveExe)) { continue }
                $fullLiveExe = [IO.Path]::GetFullPath($liveExe)
                $targetPrefix = $FullTargetDir.TrimEnd('\', '/') + '\'
                if (-not $fullLiveExe.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $proc = Get-Process -Id $pidToStop -ErrorAction SilentlyContinue
                if ($null -ne $proc -and -not $proc.HasExited) {
                    try { [void]$proc.CloseMainWindow() } catch { }
                }
                [void][LimitsWidgetUpdaterNative]::RequestGracefulWindowClose($pidToStop, $allowGlobalExit)
            } catch {
            }
        }

        Start-Sleep -Milliseconds 150
    }

    $finalCheck = Get-TargetProcessState -FullTargetDir $FullTargetDir
    if ($finalCheck.OwnedTreePids.Count -gt 0) {
        $stillRunningList = $finalCheck.OwnedTreePids -join ', '
        throw "Graceful shutdown could not be confirmed within ${TimeoutSeconds}s for target process PID(s): $stillRunningList. Force termination is disabled by policy; close the target LimitsWidget instance and retry."
    }

    return $initialState
}

function New-TargetBackup([string]$FullTargetDir, [string]$CustomBackupRoot, [string]$SeededConfigSha256 = $null) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $baseBackupRoot = if ([string]::IsNullOrWhiteSpace($CustomBackupRoot)) {
        Join-Path $FullTargetDir '.limits-widget-backups'
    } else {
        Assert-SafeDirectoryPath -RawPath $CustomBackupRoot -ParameterName 'BackupRoot' -AllowExtractDir
    }
    if ([string]::IsNullOrWhiteSpace($CustomBackupRoot)) {
        Assert-StrictChildPath -ParentDir $FullTargetDir -ChildPath $baseBackupRoot
    }
    if (-not (Test-Path -LiteralPath $baseBackupRoot)) {
        New-Item -ItemType Directory -Path $baseBackupRoot -Force | Out-Null
    }
    $backupDir = Join-Path $baseBackupRoot ("backup-$stamp")
    Assert-StrictChildPath -ParentDir $baseBackupRoot -ChildPath $backupDir
    $snapshotDir = Join-Path $backupDir 'snapshot'
    $legacySnapDir = Join-Path $backupDir 'legacy'
    New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null
    New-Item -ItemType Directory -Path $legacySnapDir -Force | Out-Null

    $backedUpFiles = @()
    $missingBeforeUpdate = @()
    foreach ($rel in $BackupCandidateFiles) {
        $srcFile = Join-Path $FullTargetDir $rel
        if (Test-Path -LiteralPath $srcFile -PathType Leaf) {
            Assert-StrictChildPath -ParentDir $FullTargetDir -ChildPath $srcFile
            $dstFile = Join-Path $snapshotDir $rel
            Assert-StrictChildPath -ParentDir $snapshotDir -ChildPath $dstFile
            Copy-Item -LiteralPath $srcFile -Destination $dstFile -Force
            $backedUpFiles += [ordered]@{
                path   = $rel
                sha256 = Get-LowerSha256 -Path $srcFile
                size   = (Get-Item -LiteralPath $srcFile -Force).Length
            }
        } else {
            $missingBeforeUpdate += $rel
        }
    }

    $backedUpLegacy = @()
    foreach ($item in $KnownLegacyItems) {
        $srcLegacy = Join-Path $FullTargetDir $item
        if (Test-Path -LiteralPath $srcLegacy) {
            Assert-StrictChildPath -ParentDir $FullTargetDir -ChildPath $srcLegacy
            Assert-NoReparsePointsInTree -RootPath $srcLegacy
            $dstLegacy = Join-Path $legacySnapDir $item
            Assert-StrictChildPath -ParentDir $legacySnapDir -ChildPath $dstLegacy
            Copy-Item -LiteralPath $srcLegacy -Destination $dstLegacy -Recurse -Force
            $entryObj = Get-Item -LiteralPath $srcLegacy -Force
            $fileHashes = @()
            if ($entryObj.PSIsContainer) {
                foreach ($f in Get-ChildItem -LiteralPath $srcLegacy -File -Force -Recurse) {
                    $relSub = $f.FullName.Substring($FullTargetDir.TrimEnd('\').Length + 1).Replace('\', '/')
                    $fileHashes += [ordered]@{
                        path   = $relSub
                        sha256 = Get-LowerSha256 -Path $f.FullName
                    }
                }
            } else {
                $fileHashes += [ordered]@{
                    path   = $item
                    sha256 = Get-LowerSha256 -Path $srcLegacy
                }
            }
            $backedUpLegacy += [ordered]@{
                name        = $item
                isDirectory = [bool]$entryObj.PSIsContainer
                files       = $fileHashes
            }
        }
    }

    $backupManifest = [ordered]@{
        schemaVersion       = '1.0'
        createdAtUtc        = [DateTime]::UtcNow.ToString('o')
        targetPath          = $FullTargetDir
        legacyArchiveFolder = $null
        seededConfigSha256  = $SeededConfigSha256
        backedUpFiles       = $backedUpFiles
        missingBeforeUpdate = $missingBeforeUpdate
        backedUpLegacy      = $backedUpLegacy
    }
    Write-Utf8NoBom -Path (Join-Path $backupDir 'backup-manifest.json') -Content ($backupManifest | ConvertTo-Json -Depth 10)
    return [pscustomobject]@{
        BackupDir = $backupDir
        Manifest  = $backupManifest
    }
}

function Restore-FromBackup([string]$FullTargetDir, [string]$BackupDir, [int]$TimeoutSeconds = 10) {
    $safeBackupDir = Assert-SafeDirectoryPath -RawPath $BackupDir -ParameterName 'RollbackFromBackup' -AllowExtractDir
    Assert-NoReparsePointsInTree -RootPath $safeBackupDir
    $bmPath = Join-Path $safeBackupDir 'backup-manifest.json'
    if (-not (Test-Path -LiteralPath $bmPath -PathType Leaf)) {
        throw "Backup manifest not found at '$bmPath'."
    }
    $bm = [IO.File]::ReadAllText($bmPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($null -eq $bm -or $bm.schemaVersion -ne '1.0') {
        throw "Invalid backup-manifest.json in '$safeBackupDir'."
    }
    if ([string]::IsNullOrWhiteSpace([string]$bm.targetPath)) {
        throw "Backup manifest in '$safeBackupDir' is missing targetPath."
    }
    $manifestTarget = [IO.Path]::GetFullPath([string]$bm.targetPath).TrimEnd('\', '/')
    if (-not [string]::Equals($manifestTarget, $FullTargetDir, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cross-target rollback rejected: backup '$safeBackupDir' belongs to '$manifestTarget', not '$FullTargetDir'."
    }

    # Verify backup snapshot hashes before stopping processes or mutating target
    $snapshotDir = Join-Path $safeBackupDir 'snapshot'
    $legacySnapDir = Join-Path $safeBackupDir 'legacy'
    foreach ($bf in @($bm.backedUpFiles)) {
        $snapFile = Join-Path $snapshotDir ([string]$bf.path)
        Assert-StrictChildPath -ParentDir $snapshotDir -ChildPath $snapFile
        if (-not (Test-Path -LiteralPath $snapFile -PathType Leaf)) {
            throw "Corrupt backup: missing snapshot file '$($bf.path)'."
        }
        if ((Get-LowerSha256 -Path $snapFile) -ne ([string]$bf.sha256).ToLowerInvariant()) {
            throw "Corrupt backup: SHA-256 mismatch in snapshot file '$($bf.path)'."
        }
    }
    foreach ($leg in @($bm.backedUpLegacy)) {
        foreach ($lf in @($leg.files)) {
            $relOs = ([string]$lf.path).Replace('/', '\')
            $snapLegFile = Join-Path $legacySnapDir $relOs
            Assert-StrictChildPath -ParentDir $legacySnapDir -ChildPath $snapLegFile
            if (-not (Test-Path -LiteralPath $snapLegFile -PathType Leaf)) {
                throw "Corrupt backup: missing legacy file '$($lf.path)'."
            }
            if ((Get-LowerSha256 -Path $snapLegFile) -ne ([string]$lf.sha256).ToLowerInvariant()) {
                throw "Corrupt backup: SHA-256 mismatch in legacy file '$($lf.path)'."
            }
        }
    }

    # Gracefully stop any running target process before mutating target files
    [void](Stop-OwnedTargetProcessesGracefully -FullTargetDir $FullTargetDir -TimeoutSeconds $TimeoutSeconds)

    # Clean up any leftover .new staging files
    foreach ($rel in ($AllowedPackageFiles + @('package-manifest.json'))) {
        $newTmp = Join-Path $FullTargetDir ("$rel.new")
        if (Test-Path -LiteralPath $newTmp) {
            Remove-ContainedPath -ParentDir $FullTargetDir -ChildPath $newTmp
        }
    }

    $seededCfgHash = if ($bm.PSObject.Properties['seededConfigSha256'] -and -not [string]::IsNullOrWhiteSpace([string]$bm.seededConfigSha256)) {
        ([string]$bm.seededConfigSha256).ToLowerInvariant()
    } else {
        $null
    }

    # Remove application files that did not exist before the update, but never delete user-authored config/state/logs.
    # If config.json was newly seeded by the updater during a first install and remains byte-for-byte unchanged,
    # remove the unchanged seed so a failed pristine first install rolls back cleanly without losing user edits.
    foreach ($added in @($bm.missingBeforeUpdate)) {
        $addedRel = [string]$added
        if ($UserRuntimePreserveFiles -contains $addedRel) {
            if ($addedRel -eq 'config.json' -and $null -ne $seededCfgHash) {
                $seededCfgPath = Join-Path $FullTargetDir 'config.json'
                if (Test-Path -LiteralPath $seededCfgPath -PathType Leaf) {
                    Assert-NoReparsePoint -Path $seededCfgPath
                    if ((Get-LowerSha256 -Path $seededCfgPath) -eq $seededCfgHash) {
                        Remove-ContainedPath -ParentDir $FullTargetDir -ChildPath $seededCfgPath
                    }
                }
            }
            continue
        }
        $addedPath = Join-Path $FullTargetDir $addedRel
        if (Test-Path -LiteralPath $addedPath) {
            Remove-ContainedPath -ParentDir $FullTargetDir -ChildPath $addedPath
        }
    }

    # Restore backed up application files; preserve valid on-disk config.json/state.json/logs so post-update user edits are never lost
    foreach ($bf in @($bm.backedUpFiles)) {
        $rel = [string]$bf.path
        $snapFile = Join-Path $snapshotDir $rel
        $destFile = Join-Path $FullTargetDir $rel
        Assert-StrictChildPath -ParentDir $FullTargetDir -ChildPath $destFile
        Assert-NoReparsePoint -Path $destFile

        if ($rel -eq 'config.json' -or $rel -eq 'state.json') {
            if (Test-ValidJsonFile -Path $destFile) {
                continue
            }
        } elseif ($rel -eq 'widget.log' -or $rel -eq 'widget.log.1') {
            if (Test-Path -LiteralPath $destFile -PathType Leaf) {
                continue
            }
        }

        Copy-Item -LiteralPath $snapFile -Destination $destFile -Force
        if ((Get-LowerSha256 -Path $destFile) -ne ([string]$bf.sha256).ToLowerInvariant()) {
            throw "Rollback verification failed for '$rel'."
        }
    }

    # Restore backed up legacy items to TargetPath
    foreach ($leg in @($bm.backedUpLegacy)) {
        $name = [string]$leg.name
        $srcLeg = Join-Path $legacySnapDir $name
        $destLeg = Join-Path $FullTargetDir $name
        if (Test-Path -LiteralPath $destLeg) {
            Remove-ContainedPath -ParentDir $FullTargetDir -ChildPath $destLeg
        }
        Copy-Item -LiteralPath $srcLeg -Destination $destLeg -Recurse -Force
        foreach ($lf in @($leg.files)) {
            $relOs = ([string]$lf.path).Replace('/', '\')
            $restoredFile = Join-Path $FullTargetDir $relOs
            Assert-StrictChildPath -ParentDir $FullTargetDir -ChildPath $restoredFile
            if ((Get-LowerSha256 -Path $restoredFile) -ne ([string]$lf.sha256).ToLowerInvariant()) {
                throw "Rollback verification failed for legacy item '$($lf.path)'."
            }
        }
    }

    # If this update run created a legacy archive folder, remove that run's archive folder now that legacy items are restored
    if (-not [string]::IsNullOrWhiteSpace([string]$bm.legacyArchiveFolder)) {
        $archiveDir = [string]$bm.legacyArchiveFolder
        if (Test-Path -LiteralPath $archiveDir) {
            $legacyRoot = Join-Path $FullTargetDir '.limits-widget-legacy'
            Remove-ContainedPath -ParentDir $legacyRoot -ChildPath $archiveDir
            if ((Test-Path -LiteralPath $legacyRoot) -and @(Get-ChildItem -LiteralPath $legacyRoot -Force).Count -eq 0) {
                Remove-ContainedPath -ParentDir $FullTargetDir -ChildPath $legacyRoot
            }
        }
    }
}

$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('limits-updater-stage-' + [guid]::NewGuid().ToString('N'))
try {
    # Mode 1: Isolated Extraction (-ExtractTo)
    if (-not [string]::IsNullOrWhiteSpace($ExtractTo)) {
        if (-not [string]::IsNullOrWhiteSpace($TargetPath) -or -not [string]::IsNullOrWhiteSpace($RollbackFromBackup) -or $DryRun) {
            throw '-ExtractTo cannot be combined with -TargetPath, -RollbackFromBackup, or -DryRun.'
        }
        $fullExtract = Assert-SafeDirectoryPath -RawPath $ExtractTo -ParameterName 'ExtractTo' -AllowExtractDir
        if (Test-Path -LiteralPath $fullExtract) {
            if (@(Get-ChildItem -LiteralPath $fullExtract -Force).Count -gt 0) {
                throw "Extraction directory '$fullExtract' already exists and is not empty."
            }
        } else {
            New-Item -ItemType Directory -Path $fullExtract -Force | Out-Null
        }

        $pkgSource = if ([string]::IsNullOrWhiteSpace($PackagePath)) { $PSScriptRoot } else { [IO.Path]::GetFullPath($PackagePath) }
        $validated = Expand-AndValidatePackage -ResolvedPackageInput $pkgSource -StagingBaseDir $stagingRoot
        foreach ($name in ($AllowedPackageFiles + @('package-manifest.json'))) {
            $src = Join-Path $validated.PackageRoot $name
            $dst = Join-Path $fullExtract $name
            Assert-StrictChildPath -ParentDir $fullExtract -ChildPath $dst
            Copy-Item -LiteralPath $src -Destination $dst -Force
        }

        $receipt = [ordered]@{
            mode          = 'extract-only'
            status        = 'EXTRACTED_ONLY'
            acceptance    = $false
            mutatedTarget = $false
            extractPath   = $fullExtract
            version       = [string]$validated.Manifest.version
            timestampUtc  = [DateTime]::UtcNow.ToString('o')
            note          = 'Package manifest and SHA-256 hashes verified and extracted to isolated directory. No installation or live acceptance was performed.'
        }
        Write-UpdaterReceipt -Data $receipt
        Write-Host "EXTRACTED_ONLY: $fullExtract (version $($validated.Manifest.version))"
        exit 0
    }

    # Mode 2 & 3 require a concrete TargetPath
    $fullTarget = Assert-SafeDirectoryPath -RawPath $TargetPath -ParameterName 'TargetPath'
    Assert-TargetInstallationDirectory -FullTargetDir $fullTarget -CustomBackupRoot $BackupRoot

    # Mode 2: Explicit Rollback (-RollbackFromBackup)
    if (-not [string]::IsNullOrWhiteSpace($RollbackFromBackup)) {
        if ($DryRun) { throw '-RollbackFromBackup cannot be combined with -DryRun.' }
        $resolvedBackup = $null
        if ([string]::Equals($RollbackFromBackup.Trim(), 'latest', [StringComparison]::OrdinalIgnoreCase)) {
            $searchBase = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
                Join-Path $fullTarget '.limits-widget-backups'
            } else {
                Assert-SafeDirectoryPath -RawPath $BackupRoot -ParameterName 'BackupRoot' -AllowExtractDir
            }
            if (-not (Test-Path -LiteralPath $searchBase)) {
                throw "No backups directory found at '$searchBase'."
            }
            $latestDir = Get-ChildItem -LiteralPath $searchBase -Directory -Filter 'backup-*' | Sort-Object Name -Descending | Select-Object -First 1
            if ($null -eq $latestDir) {
                throw "No backup-* snapshot found inside '$searchBase'."
            }
            $resolvedBackup = $latestDir.FullName
        } else {
            $resolvedBackup = Assert-SafeDirectoryPath -RawPath $RollbackFromBackup -ParameterName 'RollbackFromBackup' -AllowExtractDir
        }

        try {
            Restore-FromBackup -FullTargetDir $fullTarget -BackupDir $resolvedBackup -TimeoutSeconds $GracefulStopTimeoutSeconds
        } catch {
            $rbErr = $_
            $blockedReceipt = [ordered]@{
                mode          = 'rollback-blocked'
                status        = 'ROLLBACK_ABORTED_BEFORE_MUTATION'
                acceptance    = $false
                mutatedTarget = $false
                targetPath    = $fullTarget
                backupPath    = $resolvedBackup
                error         = $rbErr.Exception.Message
                timestampUtc  = [DateTime]::UtcNow.ToString('o')
            }
            Write-UpdaterReceipt -Data $blockedReceipt
            throw
        }

        if ($RestartWidget -and (Test-Path -LiteralPath (Join-Path $fullTarget 'LimitsWidget.exe') -PathType Leaf)) {
            Start-Process -FilePath (Join-Path $fullTarget 'LimitsWidget.exe') -WorkingDirectory $fullTarget
        }

        $receipt = [ordered]@{
            mode          = 'rollback'
            status        = 'ROLLED_BACK'
            acceptance    = $false
            mutatedTarget = $true
            targetPath    = $fullTarget
            backupPath    = $resolvedBackup
            timestampUtc  = [DateTime]::UtcNow.ToString('o')
        }
        Write-UpdaterReceipt -Data $receipt
        Write-Host "ROLLED_BACK: $fullTarget from $resolvedBackup"
        exit 0
    }

    # Mode 3: Validate Package for Dry-Run or Live Update
    $pkgSource = if ([string]::IsNullOrWhiteSpace($PackagePath)) { $PSScriptRoot } else { [IO.Path]::GetFullPath($PackagePath) }
    $validated = Expand-AndValidatePackage -ResolvedPackageInput $pkgSource -StagingBaseDir $stagingRoot
    $procBefore = Get-TargetProcessState -FullTargetDir $fullTarget

    $existingLegacy = @()
    foreach ($item in $KnownLegacyItems) {
        if (Test-Path -LiteralPath (Join-Path $fullTarget $item)) {
            $existingLegacy += $item
        }
    }
    $configExists = Test-Path -LiteralPath (Join-Path $fullTarget 'config.json') -PathType Leaf
    $stateExists = Test-Path -LiteralPath (Join-Path $fullTarget 'state.json') -PathType Leaf

    if ($DryRun) {
        $receipt = [ordered]@{
            mode                 = 'dry-run'
            status               = 'DRY_RUN_OK'
            acceptance           = $false
            mutatedTarget        = $false
            targetPath           = $fullTarget
            packageVersion       = [string]$validated.Manifest.version
            executableVersion    = [string]$validated.Manifest.executableVersion
            preserveConfig       = [bool]$configExists
            preserveState        = [bool]$stateExists
            legacyItemsToArchive = $existingLegacy
            replaceFiles         = $ReplacePackageFiles
            ownedTargetRootPids  = $procBefore.OwnedRootPids
            ownedTargetTreePids  = $procBefore.OwnedTreePids
            foreignLimitsPids    = $procBefore.ForeignLimitsPids
            timestampUtc         = [DateTime]::UtcNow.ToString('o')
            note                 = 'Dry-run validated package manifest/hashes and target path containment without modifying files or stopping processes.'
        }
        Write-UpdaterReceipt -Data $receipt
        Write-Host "DRY_RUN_OK: $fullTarget (version $($validated.Manifest.version))"
        exit 0
    }

    # Live Update Step 1: Gracefully stop owned target process(es) BEFORE creating directories, hashing config/state, or creating backups.
    $stoppedState = $null
    try {
        $stoppedState = Stop-OwnedTargetProcessesGracefully -FullTargetDir $fullTarget -TimeoutSeconds $GracefulStopTimeoutSeconds
    } catch {
        $stopErr = $_
        $abortReceipt = [ordered]@{
            mode          = 'update-aborted'
            status        = 'ABORTED_PROCESS_STILL_RUNNING'
            acceptance    = $false
            mutatedTarget = $false
            targetPath    = $fullTarget
            error         = $stopErr.Exception.Message
            timestampUtc  = [DateTime]::UtcNow.ToString('o')
        }
        Write-UpdaterReceipt -Data $abortReceipt
        throw
    }

    # Live Update Step 2: Ensure TargetPath exists and capture quiescent config/state hashes and backup
    if (-not (Test-Path -LiteralPath $fullTarget)) {
        New-Item -ItemType Directory -Path $fullTarget -Force | Out-Null
    }

    $packagedConfigSha256 = Get-LowerSha256 -Path (Join-Path $validated.PackageRoot 'config.json')
    $rollbackProvenance = Get-TargetRollbackProvenance -FullTargetDir $fullTarget -CustomBackupRoot $BackupRoot
    $targetCfgCandidate = Join-Path $fullTarget 'config.json'
    if ($null -ne $rollbackProvenance -and (Test-Path -LiteralPath $targetCfgCandidate -PathType Leaf)) {
        $existingCfgSha = Get-LowerSha256 -Path $targetCfgCandidate
        if ($existingCfgSha -eq $packagedConfigSha256 -or ($null -ne $rollbackProvenance.SeededConfigSha256 -and $existingCfgSha -eq $rollbackProvenance.SeededConfigSha256)) {
            Remove-ContainedPath -ParentDir $fullTarget -ChildPath $targetCfgCandidate
        }
    }

    $configExists = Test-Path -LiteralPath $targetCfgCandidate -PathType Leaf
    $stateExists = Test-Path -LiteralPath (Join-Path $fullTarget 'state.json') -PathType Leaf
    $preConfigHash = if ($configExists) { Get-LowerSha256 -Path $targetCfgCandidate } else { $null }
    $preStateHash = if ($stateExists) { Get-LowerSha256 -Path (Join-Path $fullTarget 'state.json') } else { $null }
    $seededConfigHashForBackup = if (-not $configExists) { $packagedConfigSha256 } else { $null }

    $backupInfo = New-TargetBackup -FullTargetDir $fullTarget -CustomBackupRoot $BackupRoot -SeededConfigSha256 $seededConfigHashForBackup

    $archivedLegacyDir = $null
    try {
        if ($InjectFailure -eq 'AfterStopBeforeMutate') {
            throw 'Simulated failure injection: AfterStopBeforeMutate'
        }

        if ($existingLegacy.Count -gt 0) {
            $legacyBase = Join-Path $fullTarget '.limits-widget-legacy'
            Assert-StrictChildPath -ParentDir $fullTarget -ChildPath $legacyBase
            if (-not (Test-Path -LiteralPath $legacyBase)) {
                New-Item -ItemType Directory -Path $legacyBase -Force | Out-Null
            }
            $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
            $archivedLegacyDir = Join-Path $legacyBase ("legacy-$stamp")
            Assert-StrictChildPath -ParentDir $legacyBase -ChildPath $archivedLegacyDir
            New-Item -ItemType Directory -Path $archivedLegacyDir -Force | Out-Null

            $backupInfo.Manifest.legacyArchiveFolder = $archivedLegacyDir
            Write-Utf8NoBom -Path (Join-Path $backupInfo.BackupDir 'backup-manifest.json') -Content ($backupInfo.Manifest | ConvertTo-Json -Depth 10)

            foreach ($item in $existingLegacy) {
                $srcPath = Join-Path $fullTarget $item
                $dstPath = Join-Path $archivedLegacyDir $item
                Move-ContainedPath -SourceParentDir $fullTarget -SourcePath $srcPath -DestParentDir $archivedLegacyDir -DestPath $dstPath
            }
        }

        if ($InjectFailure -eq 'AfterArchiveLegacy') {
            throw 'Simulated failure injection: AfterArchiveLegacy'
        }

        # Replace allowlisted application files via verified .new staging files
        $replacedCount = 0
        foreach ($rel in $ReplacePackageFiles) {
            $srcFile = Join-Path $validated.PackageRoot $rel
            $dstFile = Join-Path $fullTarget $rel
            $tmpFile = Join-Path $fullTarget ("$rel.new")
            Assert-StrictChildPath -ParentDir $fullTarget -ChildPath $dstFile
            Assert-StrictChildPath -ParentDir $fullTarget -ChildPath $tmpFile
            Assert-NoReparsePoint -Path $dstFile
            Assert-NoReparsePoint -Path $tmpFile

            Copy-Item -LiteralPath $srcFile -Destination $tmpFile -Force
            $manifestEntry = @($validated.Manifest.files | Where-Object { $_.path -eq $rel })[0]
            if ((Get-LowerSha256 -Path $tmpFile) -ne ([string]$manifestEntry.sha256).ToLowerInvariant()) {
                throw "Staged hash mismatch for '$rel.new'."
            }
            Move-Item -LiteralPath $tmpFile -Destination $dstFile -Force
            $replacedCount++
            if ($replacedCount -eq 1 -and $InjectFailure -eq 'MidFileUpdate') {
                throw 'Simulated failure injection: MidFileUpdate'
            }
        }

        # Seed config.json only if missing; otherwise preserve existing config.json byte-for-byte (including all unknown fields)
        $targetConfigPath = Join-Path $fullTarget 'config.json'
        if (-not $configExists) {
            $srcCfg = Join-Path $validated.PackageRoot 'config.json'
            Assert-StrictChildPath -ParentDir $fullTarget -ChildPath $targetConfigPath
            Copy-Item -LiteralPath $srcCfg -Destination $targetConfigPath -Force
        }

        # Write package-manifest.json and ownership marker
        $targetManifestPath = Join-Path $fullTarget 'package-manifest.json'
        Assert-StrictChildPath -ParentDir $fullTarget -ChildPath $targetManifestPath
        Assert-NoReparsePoint -Path $targetManifestPath
        Write-Utf8NoBom -Path $targetManifestPath -Content $validated.ManifestRaw

        $markerPath = Join-Path $fullTarget $OwnershipMarkerName
        Assert-StrictChildPath -ParentDir $fullTarget -ChildPath $markerPath
        Assert-NoReparsePoint -Path $markerPath
        Write-Utf8NoBom -Path $markerPath -Content $OwnershipMarkerContent

        if ($InjectFailure -eq 'AfterFileUpdateValidation') {
            throw 'Simulated failure injection: AfterFileUpdateValidation'
        }

        # Post-update integrity verification
        foreach ($rel in $ReplacePackageFiles) {
            $dstFile = Join-Path $fullTarget $rel
            $manifestEntry = @($validated.Manifest.files | Where-Object { $_.path -eq $rel })[0]
            if ((Get-LowerSha256 -Path $dstFile) -ne ([string]$manifestEntry.sha256).ToLowerInvariant()) {
                throw "Post-update hash verification failed for '$rel'."
            }
        }
        if ($configExists -and (Get-LowerSha256 -Path $targetConfigPath) -ne $preConfigHash) {
            throw 'Post-update check failed: existing config.json was unexpectedly modified.'
        }
        $targetStatePath = Join-Path $fullTarget 'state.json'
        if ($stateExists -and (Get-LowerSha256 -Path $targetStatePath) -ne $preStateHash) {
            throw 'Post-update check failed: existing state.json was unexpectedly modified.'
        }
    } catch {
        $updateErr = $_
        try {
            Restore-FromBackup -FullTargetDir $fullTarget -BackupDir $backupInfo.BackupDir -TimeoutSeconds $GracefulStopTimeoutSeconds
            $rollbackReceipt = [ordered]@{
                mode          = 'update-rolled-back'
                status        = 'ROLLED_BACK_ON_FAILURE'
                acceptance    = $false
                mutatedTarget = $false
                targetPath    = $fullTarget
                backupPath    = $backupInfo.BackupDir
                error         = $updateErr.Exception.Message
                timestampUtc  = [DateTime]::UtcNow.ToString('o')
            }
            Write-UpdaterReceipt -Data $rollbackReceipt
        } catch {
            throw "Update failed ('$($updateErr.Exception.Message)') AND rollback encountered an error: $($_.Exception.Message)"
        }
        throw "Update failed and was cleanly rolled back from '$($backupInfo.BackupDir)': $($updateErr.Exception.Message)"
    }

    if ($RestartWidget) {
        Start-Process -FilePath (Join-Path $fullTarget 'LimitsWidget.exe') -WorkingDirectory $fullTarget
    }

    $receipt = [ordered]@{
        mode                = 'update'
        status              = 'UPDATED'
        acceptance          = $false
        mutatedTarget       = $true
        targetPath          = $fullTarget
        backupPath          = $backupInfo.BackupDir
        archivedLegacyPath  = $archivedLegacyDir
        archivedLegacyItems = $existingLegacy
        packageVersion      = [string]$validated.Manifest.version
        executableVersion   = [string]$validated.Manifest.executableVersion
        executableSha256    = Get-LowerSha256 -Path (Join-Path $fullTarget 'LimitsWidget.exe')
        configPreserved     = [bool]$configExists
        statePreserved      = [bool]$stateExists
        stoppedRootPids     = $stoppedState.OwnedRootPids
        stoppedTreePids     = $stoppedState.OwnedTreePids
        foreignLimitsPids   = $stoppedState.ForeignLimitsPids
        timestampUtc        = [DateTime]::UtcNow.ToString('o')
        note                = 'Update completed with graceful target stop, verified manifest hashes, and backup; live runtime acceptance requires separate Root verification.'
    }
    Write-UpdaterReceipt -Data $receipt
    Write-Host "UPDATED: $fullTarget (version $($validated.Manifest.version), backup: $($backupInfo.BackupDir))"
    exit 0
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        Remove-ContainedPath -ParentDir $tempBase -ChildPath $stagingRoot
    }
}
