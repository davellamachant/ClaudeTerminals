# Generates src/ClaudeTerminals/app.ico: a purple rounded tile with a terminal prompt.
# Multi-resolution ICO with PNG-compressed entries (16-256 px).
param([string]$Out = "$PSScriptRoot\..\src\ClaudeTerminals\app.ico")

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconPng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $m = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundedRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.2)
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($s, $s),
        [System.Drawing.Color]::FromArgb(255, 0x8b, 0x7f, 0xf0), [System.Drawing.Color]::FromArgb(255, 0x4b, 0x3f, 0xb0))
    $g.FillPath($grad, $tile)

    # Terminal "screen" inset, skipped at tiny sizes where it would just be noise.
    if ($size -ge 32) {
        $inset = $s * 0.16
        $screen = New-RoundedRect $inset ($inset * 1.05) ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.08)
        $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 0x18, 0x18, 0x1c)), $screen)
    }

    # Prompt: chevron ">" and cursor "_".
    $stroke = [Math]::Max(1.6, $s * 0.085)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $stroke)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $cx = $s * 0.30; $cy = $s * 0.50; $ch = $s * 0.15
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, $cy - $ch),
        [System.Drawing.PointF]::new($cx + $ch * 1.05, $cy),
        [System.Drawing.PointF]::new($cx, $cy + $ch)))
    $cursor = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 0xc9, 0xc2, 0xff), $stroke)
    $cursor.StartCap = 'Round'; $cursor.EndCap = 'Round'
    $g.DrawLine($cursor, $s * 0.53, $cy + $ch, $s * 0.72, $cy + $ch)

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (New-IconPng $sz) }

$Out = [System.IO.Path]::GetFullPath($Out)
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Out, $ico.ToArray())

# A 256 px PNG preview alongside, handy for checking the design.
[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Out, '.png'), $images[-1])
"Wrote $Out"
