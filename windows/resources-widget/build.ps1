<#
  Сборка ResourcesWidget.exe встроенным компилятором .NET Framework 4.x (csc.exe, C# 5).
  .NET SDK не нужен. Запуск:  powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "Не найден $csc — нужен .NET Framework 4.8" }

$refs = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll',
    'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll') | ForEach-Object { Join-Path $fw $_ }
$refs += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') |
    ForEach-Object { Join-Path $fw "WPF\$_" }

$src = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName }
$src += Join-Path $root 'installer\shared\SensorSecurity.cs'
$ico = Join-Path $root 'assets\ResourcesWidget.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'tools\make-icon.ps1') -OutFile $ico }

$out = Join-Path $root 'ResourcesWidget.exe'
# Building never closes an existing widget. Use an unused output checkout.
$running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($out)) -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out }
if ($running) { throw 'Output executable is running. Exit it yourself before rebuilding.' }
$config = Join-Path $root 'config.json'
if (-not (Test-Path -LiteralPath $config)) { Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $config }
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output',
    "/out:$out", "/win32manifest:$(Join-Path $root 'app.manifest')", "/win32icon:$ico") +
    ($refs | ForEach-Object { "/r:$_" }) + $src
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc завершился с кодом $LASTEXITCODE" }
Write-Host "OK: $out"

