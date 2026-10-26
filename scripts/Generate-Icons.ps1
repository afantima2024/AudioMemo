[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root 'src\AudioMemo\Assets'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$source = Join-Path $destination 'BrandLogo.png'
$icon = Join-Path $destination 'AudioMemo.ico'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw 'The supplied BrandLogo.png artwork is required.'
}
$sizes = @{
    'Square44x44Logo.png' = 44
    'Square150x150Logo.png' = 150
    'StoreLogo.png' = 50
    'AppIcon.png' = 256
}
function New-LogoBitmap([System.Drawing.Bitmap]$artwork, [System.Drawing.Rectangle]$bounds, [int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $padding = [Math]::Max(0.5, $size * 0.01)
        $scale = ($size - 2 * $padding) / [Math]::Max($bounds.Width, $bounds.Height)
        $width = [single]($bounds.Width * $scale)
        $height = [single]($bounds.Height * $scale)
        $target = [System.Drawing.RectangleF]::new(($size - $width) / 2, ($size - $height) / 2, $width, $height)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($artwork, $target, $bounds, [System.Drawing.GraphicsUnit]::Pixel)
        return $bitmap
    }
    catch {
        $bitmap.Dispose()
        throw
    }
    finally { $graphics.Dispose() }
}

$artwork = [System.Drawing.Bitmap]::new($source)
try {
    $left = $artwork.Width; $top = $artwork.Height; $right = -1; $bottom = -1
    for ($y = 0; $y -lt $artwork.Height; $y++) {
        for ($x = 0; $x -lt $artwork.Width; $x++) {
            if ($artwork.GetPixel($x, $y).A -ge 8) {
                $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y)
                $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    if ($right -lt $left) { throw 'The supplied artwork has no visible pixels.' }
    $left = [Math]::Max(0, $left - 1); $top = [Math]::Max(0, $top - 1)
    $right = [Math]::Min($artwork.Width - 1, $right + 1); $bottom = [Math]::Min($artwork.Height - 1, $bottom + 1)
    $bounds = [System.Drawing.Rectangle]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
    foreach ($entry in $sizes.GetEnumerator()) {
        $bitmap = New-LogoBitmap $artwork $bounds $entry.Value
        try { $bitmap.Save((Join-Path $destination $entry.Key), [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $bitmap.Dispose() }
    }
    $frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)) {
        $bitmap = New-LogoBitmap $artwork $bounds $size
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        }
        finally {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
        $writer.Flush()
        [IO.File]::WriteAllBytes($icon, $stream.ToArray())
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}
finally { $artwork.Dispose() }
Write-Host "Generated application and MSIX icons in $destination"
