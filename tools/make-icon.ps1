# Generates src/LlamaTree/app.ico (and app.png) from docs/brand/LlamaTree-Terminal-Icon.svg, the orange framed
# prompt, which stays clear at every size (the full llama icon, LlamaTree-Icon.svg, blurs when small).
# Microsoft Edge renders the SVG once at 1024 px; each icon size is scaled down from that. The ICO holds PNG entries
# (16-256 px), which Windows has read since Vista.
param(
    [string]$Svg = "$PSScriptRoot\..\docs\brand\LlamaTree-Terminal-Icon.svg",
    [string]$Out = "$PSScriptRoot\..\src\LlamaTree\app.ico",
    [string]$Png = "$PSScriptRoot\..\src\LlamaTree\app.png"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found: it renders the SVG.' }

# Render: a page holding just the image, screenshotted on a transparent background.
$work = Join-Path ([IO.Path]::GetTempPath()) ("llamatree-icon-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
try {
    $data = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $Svg)))
    $page = Join-Path $work 'icon.html'
    Set-Content $page "<html><body style=`"margin:0;background:transparent`"><img src=`"data:image/svg+xml;base64,$data`" style=`"display:block;width:1024px;height:1024px`"></body></html>"
    $big = Join-Path $work 'icon.png'
    & $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 `
        --window-size=1024,1024 "--user-data-dir=$work\profile" "--screenshot=$big" ([Uri]$page).AbsoluteUri 2>$null | Out-Null
    for ($i = 0; $i -lt 50 -and -not (Test-Path $big); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $big)) { throw "Edge did not render $Svg." }
    $source = [System.Drawing.Bitmap]::FromFile($big)

    function Get-Scaled([int]$size) {
        $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode = 'HighQuality'
        $g.CompositingQuality = 'HighQuality'
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()
        $ms = [IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }

    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $images = $sizes | ForEach-Object { , (Get-Scaled $_) }
    [IO.File]::WriteAllBytes($Png, $images[-1])

    # ICONDIR, one ICONDIRENTRY per size, then the PNGs.
    $ico = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ico)
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $images[$i].Length
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 is written as 0
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [IO.File]::WriteAllBytes($Out, $ico.ToArray())
    $source.Dispose()
    Write-Host "Wrote $Out ($($sizes -join ', ') px) and $Png"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
