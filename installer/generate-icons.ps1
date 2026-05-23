# Generate Reson.ico (multi-size, PNG-encoded for modern Windows) and PWA PNGs
# from the master Reson.png. Run once after the master image changes; the
# generated files are committed to the repo.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$srcPng = Join-Path $repoRoot 'Reson.png'
if (-not (Test-Path $srcPng)) { throw "Master image not found: $srcPng" }

Write-Host "Loading master: $srcPng"
$master = [System.Drawing.Image]::FromFile($srcPng)

function Save-Resized {
    param([int]$size, [string]$outPath)
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($master, 0, 0, $size, $size)
    $g.Dispose()
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# --- PWA PNGs ---
$wwwroot = Join-Path $repoRoot 'src\Soundpad\wwwroot'
Save-Resized 192 (Join-Path $wwwroot 'icon-192.png')
Save-Resized 512 (Join-Path $wwwroot 'icon-512.png')
Save-Resized 32  (Join-Path $wwwroot 'favicon-32.png')
Write-Host "Wrote PWA PNGs (192, 512, 32)"

# --- Multi-size ICO with PNG-encoded entries (Vista+ supports this) ---
$sizes  = @(16, 32, 48, 64, 128, 256)
$assets = Join-Path $repoRoot 'assets'
if (-not (Test-Path $assets)) { New-Item -ItemType Directory $assets | Out-Null }
$icoPath = Join-Path $assets 'Reson.ico'

# Encode each size as PNG bytes
$pngBytes = @{}
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBytes[$s] = $ms.ToArray()
    $ms.Dispose()
}

# Build the ICO file: ICONDIR (6 bytes) + ICONDIRENTRY[count] (16 bytes each) + image data
$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $out

# ICONDIR
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type 1 = icon
$bw.Write([UInt16]$sizes.Count) # count

# ICONDIRENTRY offsets: 6 (ICONDIR) + 16*count (entries)
$dataOffset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { [byte]$s }  # 0 means 256 per ICO spec
    $bw.Write([byte]$dim)        # width
    $bw.Write([byte]$dim)        # height
    $bw.Write([byte]0)           # color count (0 = >=256 colors)
    $bw.Write([byte]0)           # reserved
    $bw.Write([UInt16]1)         # color planes
    $bw.Write([UInt16]32)        # bits per pixel
    $bw.Write([UInt32]$pngBytes[$s].Length)  # size of image data
    $bw.Write([UInt32]$dataOffset)           # offset to image data
    $dataOffset += $pngBytes[$s].Length
}

# Image data (concatenated PNG bytes)
foreach ($s in $sizes) {
    $bw.Write($pngBytes[$s])
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose()
$out.Dispose()
$master.Dispose()

Write-Host "Wrote ICO: $icoPath ($([math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB, $($sizes.Count) sizes)"
