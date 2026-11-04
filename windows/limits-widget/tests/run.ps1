<#
  Офлайн-проверки AI Limits 1.1: компилирует настоящие исходники виджета (C# 5, .NET Framework 4.8) вместе с тестами,
  поддельные kimi/codex и запускает их. Сеть провайдеров, аккаунты, credentials и установленная копия не используются;
  всё пишется только в -OutDir (абсолютный путь вне каталога исходников).
  Запуск:  powershell -ExecutionPolicy Bypass -File windows\limits-widget\tests\run.ps1 -OutDir <каталог>
#>
param([Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
$widget = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $widget '..\..')).Path
$full = [IO.Path]::GetFullPath($OutDir)
if (-not [IO.Path]::IsPathRooted($OutDir)) { throw 'OutDir must be absolute' }
$src = Join-Path $widget 'src'
if ($full.StartsWith($src, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath (Join-Path $full 'LimitsWidget.exe'))) {
    throw 'OutDir must be an isolated directory, not sources or an installed widget'
}
New-Item -ItemType Directory -Force -Path $full | Out-Null

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$refs = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') | ForEach-Object { "/r:" + (Join-Path $fw $_) }
$refs += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { "/r:" + (Join-Path $fw "WPF\$_") }
$common = @('/nologo', '/langversion:5', '/codepage:65001', '/utf8output')

$fakes = Join-Path $full 'fakes'
New-Item -ItemType Directory -Force -Path $fakes | Out-Null
$kimi = Join-Path $fakes 'kimi.exe'
$codex = Join-Path $fakes 'codex.exe'
$claude = Join-Path $fakes 'claude.exe'
& $csc @common '/target:exe' "/out:$kimi" (Join-Path $PSScriptRoot 'FakeKimi.cs')
if ($LASTEXITCODE -ne 0) { throw 'FakeKimi compilation failed' }
& $csc @common '/target:exe' "/out:$codex" (Join-Path $PSScriptRoot 'FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'FakeCodex compilation failed' }
& $csc @common '/target:exe' "/out:$claude" (Join-Path $PSScriptRoot 'FakeClaude.cs')
if ($LASTEXITCODE -ne 0) { throw 'FakeClaude compilation failed' }

$sources = @(Get-ChildItem -LiteralPath $src -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
$sources += Join-Path $repo 'telemetry\publisher-shared\TelemetrySnapshotWriter.cs'
$sources += Join-Path $PSScriptRoot 'LimitsTests.cs'
$exe = Join-Path $full 'LimitsTests.exe'
& $csc @common '/target:exe' '/main:LimitsWidget.LimitsTests' "/out:$exe" "/win32manifest:$(Join-Path $widget 'app.manifest')" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Test harness compilation failed' }

& $exe $full $kimi $codex $claude
if ($LASTEXITCODE -ne 0) { throw "Limits tests failed (exit $LASTEXITCODE)" }
