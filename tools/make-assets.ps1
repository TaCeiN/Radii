# Draws the app icon, logo, README banner and GitHub social preview into assets\.
# The mark is a hub with six spokes ("radii"); one tip is lit: the sector you are about to pick.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1 [-Name Radii] [-Tagline "..."]
param(
    [string]$Name = 'Radii',
    [string]$Tagline = 'Radial context menu for Windows 11',
    [string]$Hint = 'Hold the right button  ·  flick to a sector  ·  release'
)
$ErrorActionPreference = 'Stop'
$env:LIB = ''
Add-Type -AssemblyName System.Drawing
$D = 'System.Drawing'

$Accent = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)
$White = [System.Drawing.Color]::White

function Polar([double]$cx, [double]$cy, [double]$r, [double]$deg) {
    $a = ($deg - 90) * [Math]::PI / 180
    return [System.Drawing.PointF]::new([single]($cx + $r * [Math]::Cos($a)), [single]($cy + $r * [Math]::Sin($a)))
}

function Fill-Tile($g, [double]$size) {
    $r = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($size - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($size - 2 * $r - 1, $size - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $size - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new([single]$size, [single]$size)), ([System.Drawing.Color]::FromArgb(255, 40, 43, 58)), ([System.Drawing.Color]::FromArgb(255, 17, 18, 25))
    $g.FillPath($bg, $path)
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), 1.2
    $g.DrawPath($edge, $path)
}

# The mark in a 128x128 box, at (ox, oy) scaled by s. $tile draws the rounded tile behind it.
function Draw-Mark($g, [double]$ox, [double]$oy, [double]$s, [bool]$tile) {
    $state = $g.Save()
    $g.TranslateTransform([single]$ox, [single]$oy)
    $g.ScaleTransform([single]$s, [single]$s)
    if ($tile) { Fill-Tile $g 128 }
    # small sizes get a heavier mark so the spokes survive at 16 px
    $bold = ($s * 128) -le 32
    $penW = if ($bold) { 15 } else { 10 }
    $pen = New-Object System.Drawing.Pen $Accent, $penW
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $accentBrush = New-Object System.Drawing.SolidBrush $Accent
    $whiteBrush = New-Object System.Drawing.SolidBrush $White
    for ($i = 0; $i -lt 6; $i++) {
        $a = Polar 64 64 22 ($i * 60)
        $b = Polar 64 64 40 ($i * 60)
        $g.DrawLine($pen, $a, $b)
        $t = Polar 64 64 46 ($i * 60)
        if ($i -eq 1) { $rad = $(if ($bold) { 16 } else { 12 }); $br = $whiteBrush } else { $rad = $(if ($bold) { 12 } else { 9 }); $br = $accentBrush }
        $g.FillEllipse($br, $t.X - $rad, $t.Y - $rad, 2 * $rad, 2 * $rad)
    }
    $hub = $(if ($bold) { 18 } else { 14 })
    $g.FillEllipse($accentBrush, 64 - $hub, 64 - $hub, 2 * $hub, 2 * $hub)
    $g.Restore($state)
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.InterpolationMode = 'HighQualityBicubic'
    return @($bmp, $g)
}

function Draw-Rays($g, [double]$cx, [double]$cy, [double]$maxR, [int]$count) {
    # faint spokes radiating from a hub: the same motif as the mark, as a backdrop
    for ($i = 0; $i -lt $count; $i++) {
        $deg = $i * 360.0 / $count
        $len = $maxR * (0.45 + 0.55 * [Math]::Abs([Math]::Sin($i * 1.7)))
        $inner = $maxR * 0.12
        $alpha = [int](18 + 42 * [Math]::Abs([Math]::Cos($i * 0.9)))
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($alpha, 76, 194, 255)), 3
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, (Polar $cx $cy $inner $deg), (Polar $cx $cy $len $deg))
        $tip = Polar $cx $cy $len $deg
        $br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([int]($alpha * 1.4), 76, 194, 255))
        $g.FillEllipse($br, $tip.X - 6, $tip.Y - 6, 12, 12)
    }
}

function Font([string]$family, [double]$px, [string]$style = 'Regular') {
    $fs = [System.Drawing.FontStyle]$style
    foreach ($n in @($family, 'Segoe UI Variable Display', 'Segoe UI Semibold', 'Segoe UI')) {
        $f = [System.Drawing.Font]::new($n, [single]$px, $fs, [System.Drawing.GraphicsUnit]::Pixel)
        if ($f.Name -eq $n) { return $f }
    }
    return [System.Drawing.Font]::new('Segoe UI', [single]$px, $fs, [System.Drawing.GraphicsUnit]::Pixel)
}
function Draw-Backdrop($g, [int]$w, [int]$h, [double]$hubX, [double]$hubY, [double]$maxR) {
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new([single]$w, [single]$h)), ([System.Drawing.Color]::FromArgb(255, 24, 26, 38)), ([System.Drawing.Color]::FromArgb(255, 12, 13, 19))
    $g.FillRectangle($bg, 0, 0, $w, $h)
    $clip = $g.Clip
    $g.SetClip([System.Drawing.RectangleF]::new(0, 0, $w, $h))
    Draw-Rays $g $hubX $hubY $maxR 28
    $hub = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 76, 194, 255))
    $g.FillEllipse($hub, $hubX - 18, $hubY - 18, 36, 36)
    $g.Clip = $clip
}

$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# ---- icon (.ico with PNG frames) and logo ----
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp, $g = New-Canvas $size $size
    Draw-Mark $g 0 0 ($size / 128.0) $true
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $b = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())

$bmp, $g = New-Canvas 512 512
Draw-Mark $g 0 0 4 $true
$g.Dispose(); $bmp.Save((Join-Path $assets 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# vector logo, same geometry
$svg = New-Object System.Text.StringBuilder
$null = $svg.AppendLine('<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">')
$null = $svg.AppendLine('  <defs><linearGradient id="t" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#282b3a"/><stop offset="1" stop-color="#111219"/></linearGradient></defs>')
$null = $svg.AppendLine('  <rect width="128" height="128" rx="28" fill="url(#t)"/>')
for ($i = 0; $i -lt 6; $i++) {
    $a = Polar 64 64 22 ($i * 60); $b = Polar 64 64 40 ($i * 60); $t = Polar 64 64 46 ($i * 60)
    $null = $svg.AppendLine(('  <line x1="{0:F2}" y1="{1:F2}" x2="{2:F2}" y2="{3:F2}" stroke="#4cc2ff" stroke-width="10" stroke-linecap="round"/>' -f $a.X, $a.Y, $b.X, $b.Y))
    if ($i -eq 1) { $r = 12; $c = '#ffffff' } else { $r = 9; $c = '#4cc2ff' }
    $null = $svg.AppendLine(('  <circle cx="{0:F2}" cy="{1:F2}" r="{2}" fill="{3}"/>' -f $t.X, $t.Y, $r, $c))
}
$null = $svg.AppendLine('  <circle cx="64" cy="64" r="14" fill="#4cc2ff"/>')
$null = $svg.AppendLine('</svg>')
[System.IO.File]::WriteAllText((Join-Path $assets 'logo.svg'), ($svg.ToString() -replace ',', '.'), (New-Object System.Text.UTF8Encoding $false))

# ---- README banner 1600x400 ----
$bmp, $g = New-Canvas 1600 400
Draw-Backdrop $g 1600 400 1380 200 520
Draw-Mark $g 90 90 1.7 $true
$brWhite = New-Object System.Drawing.SolidBrush $White
$brMuted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 176, 186, 205))
$brAccent = New-Object System.Drawing.SolidBrush $Accent
$g.DrawString($Name, (Font 'Segoe UI Variable Display' 112 'Bold'), $brWhite, 340, 62)
$g.DrawString($Tagline, (Font 'Segoe UI Variable Display' 36 'Regular'), $brMuted, 346, 214)
$g.DrawString($Hint, (Font 'Segoe UI Variable Display' 26 'Regular'), $brAccent, 348, 276)
$g.Dispose(); $bmp.Save((Join-Path $assets 'banner.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# ---- GitHub social preview 1280x640 ----
$bmp, $g = New-Canvas 1280 640
Draw-Backdrop $g 1280 640 1160 320 600
Draw-Mark $g 110 170 2.4 $true
$g.DrawString($Name, (Font 'Segoe UI Variable Display' 150 'Bold'), $brWhite, 450, 150)
$g.DrawString($Tagline, (Font 'Segoe UI Variable Display' 42 'Regular'), $brMuted, 458, 340)
$g.DrawString($Hint, (Font 'Segoe UI Variable Display' 28 'Regular'), $brAccent, 460, 410)
$g.Dispose(); $bmp.Save((Join-Path $assets 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

Write-Host "wrote app.ico, logo.png, logo.svg, banner.png, social-preview.png for '$Name' in $assets"
