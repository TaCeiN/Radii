# Draws the app icon, logo, README banner and GitHub social preview into assets\.
# The mark is the ring itself: six sectors on a graphite tile, one lit white: the sector you are about to pick.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1 [-Name Radii] [-Tagline "..."]
param(
    [string]$Name = 'Radii',
    [string]$Tagline = 'Radial context menu for Windows 11',
    [string]$Hint = 'Right-click  ·  pick a sector  ·  Shift converts, Ctrl edits'
)
$ErrorActionPreference = 'Stop'
$env:LIB = ''
Add-Type -AssemblyName System.Drawing
$D = 'System.Drawing'

$White = [System.Drawing.Color]::White
$Unlit = [System.Drawing.Color]::FromArgb(255, 58, 58, 58)

function Polar([double]$cx, [double]$cy, [double]$r, [double]$deg) {
    $a = ($deg - 90) * [Math]::PI / 180
    return [System.Drawing.PointF]::new([single]($cx + $r * [Math]::Cos($a)), [single]($cy + $r * [Math]::Sin($a)))
}

# Annular sector between radii r1..r2, from..to degrees clockwise from the top.
function Sector-Path([double]$cx, [double]$cy, [double]$r1, [double]$r2, [double]$from, [double]$to) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]($cx - $r2), [single]($cy - $r2), [single](2 * $r2), [single](2 * $r2), [single]($from - 90), [single]($to - $from))
    $path.AddArc([single]($cx - $r1), [single]($cy - $r1), [single](2 * $r1), [single](2 * $r1), [single]($to - 90), [single]($from - $to))
    $path.CloseFigure()
    return $path
}

function Fill-Tile($g, [double]$size) {
    $r = $size * 0.234
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($size - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($size - 2 * $r - 1, $size - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $size - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, [single]$size)), ([System.Drawing.Color]::FromArgb(255, 43, 43, 43)), ([System.Drawing.Color]::FromArgb(255, 18, 18, 18))
    $g.FillPath($bg, $path)
    $edge = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, -1)), ([System.Drawing.PointF]::new(0, [single]($size + 1))), ([System.Drawing.Color]::FromArgb(64, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(6, 255, 255, 255))
    $g.DrawPath((New-Object System.Drawing.Pen $edge, 1.5), $path)
}

# The mark in a 128x128 box, at (ox, oy) scaled by s: six ring sectors, the one you are about to pick lit white.
function Draw-Mark($g, [double]$ox, [double]$oy, [double]$s, [bool]$tile) {
    $state = $g.Save()
    $g.TranslateTransform([single]$ox, [single]$oy)
    $g.ScaleTransform([single]$s, [single]$s)
    if ($tile) { Fill-Tile $g 128 }
    # small sizes: lighter unlit sectors and a wider gap so the ring survives at 16 px
    $small = ($s * 128) -le 32
    $gap = $(if ($small) { 12 } else { 9 })
    $unlitColor = $(if ($small) { [System.Drawing.Color]::FromArgb(255, 82, 82, 82) } else { $Unlit })
    $unlit = New-Object System.Drawing.SolidBrush $unlitColor
    $lit = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 16)), ([System.Drawing.PointF]::new(0, 112)), $White, ([System.Drawing.Color]::FromArgb(255, 218, 218, 218))
    for ($i = 0; $i -lt 6; $i++) {
        $mid = $i * 60
        $p = Sector-Path 64 64 19 47 ($mid - 30 + $gap / 2) ($mid + 30 - $gap / 2)
        $brush = $(if ($i -eq 1) { $lit } else { $unlit })
        $g.FillPath($brush, $p)
    }
    $g.FillEllipse($unlit, 52, 52, 24, 24)
    $g.Restore($state)
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    return @($bmp, $g)
}

function Font([string]$family, [double]$px, [string]$style = 'Regular') {
    $fs = [System.Drawing.FontStyle]$style
    foreach ($n in @($family, 'Segoe UI Variable Display', 'Segoe UI Semibold', 'Segoe UI')) {
        $f = [System.Drawing.Font]::new($n, [single]$px, $fs, [System.Drawing.GraphicsUnit]::Pixel)
        if ($f.Name -eq $n) { return $f }
    }
    return [System.Drawing.Font]::new('Segoe UI', [single]$px, $fs, [System.Drawing.GraphicsUnit]::Pixel)
}

# Graphite ground with the mark's ring drawn huge and faint on the right: the logo's motif as a backdrop.
function Draw-Backdrop($g, [int]$w, [int]$h, [double]$cx, [double]$cy, [double]$r) {
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, -1)), ([System.Drawing.PointF]::new(0, [single]($h + 1))), ([System.Drawing.Color]::FromArgb(255, 26, 26, 26)), ([System.Drawing.Color]::FromArgb(255, 13, 13, 13))
    $g.FillRectangle($bg, 0, 0, $w, $h)
    $clip = $g.Clip
    $g.SetClip([System.Drawing.RectangleF]::new(0, 0, $w, $h))
    for ($i = 0; $i -lt 6; $i++) {
        $mid = $i * 60
        $p = Sector-Path $cx $cy ($r * 0.40) $r ($mid - 26) ($mid + 26)
        $alpha = $(if ($i -eq 1) { 34 } else { 10 })
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255))), $p)
    }
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
function Svg-Sector([double]$r1, [double]$r2, [double]$from, [double]$to) {
    $a = Polar 64 64 $r2 $from; $b = Polar 64 64 $r2 $to; $c = Polar 64 64 $r1 $to; $d = Polar 64 64 $r1 $from
    return ('M{0:F2} {1:F2} A{8} {8} 0 0 1 {2:F2} {3:F2} L{4:F2} {5:F2} A{9} {9} 0 0 0 {6:F2} {7:F2} Z' -f $a.X, $a.Y, $b.X, $b.Y, $c.X, $c.Y, $d.X, $d.Y, $r2, $r1)
}
$svg = New-Object System.Text.StringBuilder
$null = $svg.AppendLine('<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">')
$null = $svg.AppendLine('  <defs>')
$null = $svg.AppendLine('    <linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2b2b2b"/><stop offset="1" stop-color="#121212"/></linearGradient>')
$null = $svg.AppendLine('    <linearGradient id="e" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".25"/><stop offset="1" stop-color="#fff" stop-opacity=".02"/></linearGradient>')
$null = $svg.AppendLine('    <linearGradient id="w" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff"/><stop offset="1" stop-color="#dadada"/></linearGradient>')
$null = $svg.AppendLine('  </defs>')
$null = $svg.AppendLine('  <rect width="128" height="128" rx="30" fill="url(#t)"/>')
$null = $svg.AppendLine('  <rect x="0.75" y="0.75" width="126.5" height="126.5" rx="29.25" fill="none" stroke="url(#e)" stroke-width="1.5"/>')
for ($i = 0; $i -lt 6; $i++) {
    $mid = $i * 60
    $fill = $(if ($i -eq 1) { 'url(#w)' } else { '#3a3a3a' })
    $null = $svg.AppendLine(('  <path d="{0}" fill="{1}"/>' -f (Svg-Sector 19 47 ($mid - 25.5) ($mid + 25.5)), $fill))
}
$null = $svg.AppendLine('  <circle cx="64" cy="64" r="12" fill="#3a3a3a"/>')
$null = $svg.AppendLine('</svg>')
[System.IO.File]::WriteAllText((Join-Path $assets 'logo.svg'), ($svg.ToString() -replace '(\d),(\d)', '$1.$2'), (New-Object System.Text.UTF8Encoding $false))

# ---- README banner 1600x400 ----
$bmp, $g = New-Canvas 1600 400
Draw-Backdrop $g 1600 400 1380 200 330
Draw-Mark $g 90 90 1.7 $true
$brWhite = New-Object System.Drawing.SolidBrush $White
$brMuted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 184, 184, 184))
$brAccent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 128, 128, 128))
$g.DrawString($Name, (Font 'Segoe UI Variable Display' 112 'Bold'), $brWhite, 340, 62)
$g.DrawString($Tagline, (Font 'Segoe UI Variable Display' 36 'Regular'), $brMuted, 346, 214)
$g.DrawString($Hint, (Font 'Segoe UI Variable Display' 26 'Regular'), $brAccent, 348, 276)
$g.Dispose(); $bmp.Save((Join-Path $assets 'banner.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# ---- GitHub social preview 1280x640 ----
$bmp, $g = New-Canvas 1280 640
Draw-Backdrop $g 1280 640 1340 320 360
Draw-Mark $g 110 170 2.4 $true
$g.DrawString($Name, (Font 'Segoe UI Variable Display' 150 'Bold'), $brWhite, 450, 150)
$g.DrawString($Tagline, (Font 'Segoe UI Variable Display' 42 'Regular'), $brMuted, 458, 340)
$g.DrawString($Hint, (Font 'Segoe UI Variable Display' 28 'Regular'), $brAccent, 460, 410)
$g.Dispose(); $bmp.Save((Join-Path $assets 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

Write-Host "wrote app.ico, logo.png, logo.svg, banner.png, social-preview.png for '$Name' in $assets"
