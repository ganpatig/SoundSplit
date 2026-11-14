# Draws the SoundSplit icon (one source splitting into two speakers) and writes Assets\app.ico.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded-square tile in the Windows 11 accent blues.
    $r = 56 * $s
    $rect = New-Object System.Drawing.RectangleF (8 * $s), (8 * $s), (240 * $s), (240 * $s)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0, 95, 184)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 60
    $g.FillPath($brush, $path)

    # One dot on the left, two on the right, joined by smooth curves.
    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.6, 20 * $s))
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $x0 = 74 * $s; $y0 = 128 * $s
    $x1 = 182 * $s; $ya = 78 * $s; $yb = 178 * $s
    $mid = 128 * $s
    $g.DrawBezier($pen, $x0, $y0, $mid, $y0, $mid, $ya, $x1, $ya)
    $g.DrawBezier($pen, $x0, $y0, $mid, $y0, $mid, $yb, $x1, $yb)
    $fill = New-Object System.Drawing.SolidBrush $white
    $big = 26 * $s; $small = 22 * $s
    $g.FillEllipse($fill, $x0 - $big, $y0 - $big, $big * 2, $big * 2)
    $g.FillEllipse($fill, $x1 - $small, $ya - $small, $small * 2, $small * 2)
    $g.FillEllipse($fill, $x1 - $small, $yb - $small, $small * 2, $small * 2)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large entries are stored as PNG.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small entries are stored as classic 32-bit bitmaps, which every icon reader understands.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        # Transparency mask (unused with 32-bit colour, but required): one bit per pixel, rows padded to 32 bits.
        $maskRow = New-Object byte[] ([int]([Math]::Ceiling($size / 32.0) * 4))
        for ($y = 0; $y -lt $size; $y++) { $bw.Write($maskRow) }
        $bw.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = $sizes | ForEach-Object { ,(New-IconPng $_) }

$out = Join-Path $PSScriptRoot '..\src\SoundSplit\Assets\app.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
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
$w.Close()
"Wrote $out"
