Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$assetDirectory = Join-Path $PSScriptRoot '..\assets'
$pngPath = Join-Path $assetDirectory 'app-icon.png'
$icoPath = Join-Path $assetDirectory 'app-icon.ico'
$canvasSize = 1024

function New-RoundedPath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($canvasSize, $canvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $tile = New-RoundedPath 52 52 920 920 190
        $tileBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#121820'))
        $graphics.FillPath($tileBrush, $tile)
        $tileBrush.Dispose()

        $inner = New-RoundedPath 70 70 884 884 174
        $innerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(75, 110, 168, 254), 10)
        $graphics.DrawPath($innerPen, $inner)
        $innerPen.Dispose()

        $pawColor = [System.Drawing.Color]::FromArgb(95, 110, 168, 254)
        $pawBrush = [System.Drawing.SolidBrush]::new($pawColor)
        $graphics.FillEllipse($pawBrush, 245, 235, 130, 165)
        $graphics.FillEllipse($pawBrush, 390, 175, 130, 170)
        $graphics.FillEllipse($pawBrush, 535, 175, 130, 170)
        $graphics.FillEllipse($pawBrush, 680, 235, 130, 165)
        $centerPaw = New-RoundedPath 310 355 404 360 145
        $graphics.FillPath($pawBrush, $centerPaw)
        $pawBrush.Dispose()

        $mint = [System.Drawing.ColorTranslator]::FromHtml('#4ED3A1')
        $arrowBrush = [System.Drawing.SolidBrush]::new($mint)
        $graphics.FillRectangle($arrowBrush, 454, 250, 116, 340)
        $arrow = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(330, 520),
            [System.Drawing.PointF]::new(694, 520),
            [System.Drawing.PointF]::new(512, 720)
        )
        $graphics.FillPolygon($arrowBrush, $arrow)
        $arrowBrush.Dispose()

        $trayPath = New-RoundedPath 235 700 554 145 48
        $trayPen = [System.Drawing.Pen]::new($mint, 42)
        $trayPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $trayPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawPath($trayPen, $trayPath)
        $trayPen.Dispose()

        $highlightPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(110, 255, 255, 255), 11)
        $graphics.DrawLine($highlightPen, 477, 278, 477, 470)
        $highlightPen.Dispose()
        $tile.Dispose()
        $inner.Dispose()
        $centerPaw.Dispose()
        $trayPath.Dispose()
    }
    finally {
        $graphics.Dispose()
    }

    if ($size -eq $canvasSize) { return $bitmap }
    $scaled = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $scaledGraphics = [System.Drawing.Graphics]::FromImage($scaled)
    try {
        $scaledGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaledGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaledGraphics.DrawImage($bitmap, 0, 0, $size, $size)
    }
    finally {
        $scaledGraphics.Dispose()
        $bitmap.Dispose()
    }
    return $scaled
}

$master = New-IconBitmap $canvasSize
try { $master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $master.Dispose() }

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add($stream.ToArray())
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$file = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $sizeByte = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Generated $pngPath and $icoPath"
