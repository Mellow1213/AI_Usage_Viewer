# Rasterize the existing vector triangle logo, rather than enlarging a bitmap.
# Run with Windows PowerShell -STA. This renders offscreen and opens no windows.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$taskProject = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskProject 'Assets'
[xml]$taskSvg = Get-Content -LiteralPath (Join-Path $taskAssets 'usage-widget.svg') -Raw
$taskBackground = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($taskSvg.svg.rect.fill))
$taskBorder = [Windows.Media.Pen]::new([Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($taskSvg.svg.rect.stroke)), 1)
$taskFrames = [Collections.Generic.List[object]]::new()
foreach ($taskSize in @(256, 128, 96, 64, 48, 40, 32, 24, 20, 16)) {
    $taskVisual = [Windows.Media.DrawingVisual]::new()
    $taskDrawing = $taskVisual.RenderOpen()
    $taskDrawing.PushTransform([Windows.Media.ScaleTransform]::new($taskSize * 4 / 48, $taskSize * 4 / 48))
    $taskDrawing.DrawRoundedRectangle($taskBackground, $taskBorder, [Windows.Rect]::new(.5, .5, 47, 47), 22, 22)
    $taskDrawing.PushTransform([Windows.Media.TranslateTransform]::new(4, 4))
    foreach ($taskPath in $taskSvg.svg.g.path) {
        $taskBrush = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($taskSvg.svg.g.fill))
        $taskBrush.Opacity = [double]::Parse($taskPath.opacity, [Globalization.CultureInfo]::InvariantCulture)
        $taskDrawing.DrawGeometry($taskBrush, $null, [Windows.Media.Geometry]::Parse($taskPath.d))
    }
    $taskDrawing.Pop()
    $taskDrawing.Pop()
    $taskDrawing.Close()
    $taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($taskSize * 4, $taskSize * 4, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskScaled = [Windows.Media.Imaging.TransformedBitmap]::new($taskBitmap, [Windows.Media.ScaleTransform]::new(.25, .25))
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskScaled))
    $taskStream = [IO.MemoryStream]::new()
    $taskEncoder.Save($taskStream)
    $taskFrames.Add([pscustomobject]@{ Size = $taskSize; Data = $taskStream.ToArray() })
    $taskStream.Dispose()
}
# PNG-backed ICO frames retain alpha and cover Explorer, taskbar and high DPI.
[IO.File]::WriteAllBytes((Join-Path $taskAssets 'usage-widget.png'), [byte[]]$taskFrames[0].Data)
$taskFile = [IO.File]::Create((Join-Path $taskAssets 'usage-widget.ico'))
$taskWriter = [IO.BinaryWriter]::new($taskFile)
try {
    $taskWriter.Write([uint16]0)
    $taskWriter.Write([uint16]1)
    $taskWriter.Write([uint16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    foreach ($taskFrame in $taskFrames) {
        $taskDimension = if ($taskFrame.Size -eq 256) { 0 } else { $taskFrame.Size }
        $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrame.Data.Length)
        $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrame.Data.Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Data) }
} finally { $taskWriter.Dispose() }
Write-Output ('Generated triangle icon: ' + (Join-Path $taskAssets 'usage-widget.ico'))
