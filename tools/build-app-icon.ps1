# Reproduce the Grove G caption badge as Windows/desktop assets. Windows only;
# uses the installed Segoe UI font and System.Drawing, without downloaded artwork.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/GitX.Desktop/Assets'))
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = [single]($size * 0.5); $edge = [single]($size - 1)
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($edge-$d, 0, $d, $d, 270, 90)
    $path.AddArc($edge-$d, $edge-$d, $d, $d, 0, 90)
    $path.AddArc(0, $edge-$d, $d, $d, 90, 90); $path.CloseFigure()
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.Rectangle]::new(0, 0, $size, $size),
        [Drawing.ColorTranslator]::FromHtml('#3574F0'),
        [Drawing.ColorTranslator]::FromHtml('#8A5CF5'), [single]45)
    $font = [Drawing.Font]::new('Segoe UI', [single]($size * 0.5), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $format = [Drawing.StringFormat]::new()
    $format.Alignment = [Drawing.StringAlignment]::Center
    $format.LineAlignment = [Drawing.StringAlignment]::Center
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.FillPath($gradient, $path)
        $graphics.DrawString('G', $font, [Drawing.Brushes]::White,
            [Drawing.RectangleF]::new(0, 0, $size, $size), $format)
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames += ,$stream.ToArray()
        if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetRoot 'gitx.png'), $frames[-1]) }
    } finally {
        $stream.Dispose(); $format.Dispose(); $font.Dispose(); $gradient.Dispose()
        $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}
$file = [IO.File]::Create((Join-Path $assetRoot 'gitx.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $encodedSize = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$encodedSize); $writer.Write([byte]$encodedSize)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Output "Generated Grove icon assets in $assetRoot"
