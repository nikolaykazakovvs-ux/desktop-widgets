<#
  Рисует значок виджета (три столбика нагрузки на тёмной плашке) и собирает .ico из PNG-кадров.
#>
param([Parameter(Mandatory = $true)][string]$OutFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2.0, $size * 0.22); $d = $r * 2; $w = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 32, 42))), $path)

    # столбики: процессор (синий), память (фиолетовый), видеокарта (зелёный)
    $m = $size * 0.2; $gap = $size * 0.08; $bw = ($size - 2 * $m - 2 * $gap) / 3; $full = $size - 2 * $m
    $bars = @(@(76, 154, 255, 0.55), @(179, 136, 255, 0.8), @(118, 192, 67, 0.35))
    $track = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
    for ($i = 0; $i -lt 3; $i++) {
        $x = $m + $i * ($bw + $gap)
        $g.FillRectangle($track, [single]$x, [single]$m, [single]$bw, [single]$full)
        $c = $bars[$i]; $h = $full * $c[3]
        $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, $c[0], $c[1], $c[2]))
        $g.FillRectangle($fill, [single]$x, [single]($m + $full - $h), [single]$bw, [single]$h)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) { $images.Add((New-IconPng $s)) }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "icon: $OutFile"
