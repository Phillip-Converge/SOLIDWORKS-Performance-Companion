# Draws the Companion's icon (a red cube with a small performance-gauge badge) and saves it as
# src\SWCompanion.App\Companion.ico, the file Windows shows in File Explorer, on the Start menu, on the taskbar
# and in the top-left corner of the window.
#
# The picture is defined here, in drawing instructions, so it stays sharp at every size and can be changed in
# one place. To change the icon: edit the shapes or colours below, run this script again, then rebuild.
#
# Run from the project folder (Windows PowerShell):   .\scripts\Make-Icon.ps1
# Optional: -PreviewFolder <folder> also saves a PNG of each size there, to look at.
param([string]$PreviewFolder = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "src\SWCompanion.App\Companion.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 256   # the sizes Windows asks for at different display scalings

# Colours: a red close to (but not the same as) SOLIDWORKS's, lighter on top and darker on the right side,
# so the cube looks lit from above.
$red = "#DA291C"; $redDark = "#9E1B12"; $redLight = "#F0574A"; $white = "#FFFFFF"; $black = "#1A1A1A"

function New-Brush([string]$hex) {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze(); $b
}
function New-RoundPen([string]$hex, [double]$width) {
    $p = New-Object System.Windows.Media.Pen((New-Brush $hex), $width)
    $p.StartLineCap = "Round"; $p.EndLineCap = "Round"; $p.Freeze(); $p
}
function Get-Geometry([string]$data) { [System.Windows.Media.Geometry]::Parse($data) }

# Draws the picture on a 256 x 256 grid, scaled to the requested size, and returns the image.
function New-IconImage([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))

    # The cube: top, left and right faces.
    $dc.DrawGeometry((New-Brush $redLight), $null, (Get-Geometry "M128,24 L224,76 L128,128 L32,76 Z"))
    $dc.DrawGeometry((New-Brush $red),      $null, (Get-Geometry "M32,76 L128,128 L128,236 L32,184 Z"))
    $dc.DrawGeometry((New-Brush $redDark),  $null, (Get-Geometry "M224,76 L128,128 L128,236 L224,184 Z"))

    # The badge: a white ring with a dark dial, bottom right.
    $dc.DrawEllipse((New-Brush $white), $null, (New-Object System.Windows.Point 190, 190), 56, 56)
    $dc.DrawEllipse((New-Brush $black), $null, (New-Object System.Windows.Point 190, 190), 48, 48)

    # The gauge on the dial: an arc, a needle and its hub.
    $dc.DrawGeometry($null, (New-RoundPen $white 8.4), (Get-Geometry "M160,204 A30,30 0 0 1 220,204"))
    $dc.DrawLine((New-RoundPen $white 6.6), (New-Object System.Windows.Point 190, 204), (New-Object System.Windows.Point 208, 181.5))
    $dc.DrawEllipse((New-Brush $white), $null, (New-Object System.Windows.Point 190, 204), 6.6, 6.6)

    $dc.Pop(); $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap
}

function Get-PngBytes($bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms); ,$ms.ToArray()   # the comma keeps the bytes together as one block
}

# Small sizes are stored the classic way (a plain bitmap), which every part of Windows reads; the 256 size is
# stored as PNG, which is the normal way for large icons and keeps the file small.
function Get-BitmapBytes($bitmap, [int]$size) {
    $converted = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $converted.CopyPixels($pixels, $stride, 0)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # Header: 40 bytes; height is doubled because the icon format also expects a see-through mask after the colours.
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) }   # rows bottom to top
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskStride * $size)))   # empty mask: the colours already carry transparency
    $w.Flush(); ,$ms.ToArray()
}

$images = foreach ($s in $sizes) {
    $bmp = New-IconImage $s
    if ($PreviewFolder) { [System.IO.File]::WriteAllBytes((Join-Path $PreviewFolder "icon-$s.png"), (Get-PngBytes $bmp)) }
    [pscustomobject]@{ Size = $s; Bytes = [byte[]]$(if ($s -ge 256) { Get-PngBytes $bmp } else { Get-BitmapBytes $bmp $s }) }
}

# The .ico file: a short header, one 16-byte entry per size, then the images.
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 0 means 256 in the icon format
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$img.Bytes.Length); $w.Write([int]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
Write-Host "Done: $out ($([math]::Round($ms.Length / 1KB)) KB, sizes $($sizes -join ', '))" -ForegroundColor Green
