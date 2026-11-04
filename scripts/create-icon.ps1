param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $canvas = [Drawing.Graphics]::FromImage($bitmap)
    $canvas.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $canvas.ScaleTransform($size/256.0,$size/256.0)
    $background = [Drawing.Drawing2D.GraphicsPath]::new()
    foreach($arc in @(@(8,8,104,104,180,90),@(144,8,104,104,270,90),@(144,144,104,104,0,90),@(8,144,104,104,90,90))) {
        $background.AddArc([single]$arc[0],[single]$arc[1],[single]$arc[2],[single]$arc[3],[single]$arc[4],[single]$arc[5])
    }
    $background.CloseFigure()
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#101827'))
    $canvas.FillPath($brush,$background)
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#6EE7D0'),20)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    foreach($bar in @(@(53,107,149),@(101,64,192),@(155,43,213),@(203,96,160))) {
        $canvas.DrawLine($pen,[single]$bar[0],[single]$bar[1],[single]$bar[0],[single]$bar[2])
    }
    $memory = [IO.MemoryStream]::new()
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($size,$memory.ToArray())
    if($size -eq 256) { $bitmap.Save((Join-Path $Destination 'sound-manager.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $pen.Dispose(); $brush.Dispose(); $background.Dispose(); $canvas.Dispose(); $bitmap.Dispose()
}
$stream = [IO.File]::Create((Join-Path $Destination 'sound-manager.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6+16*$frames.Count
    foreach($frame in $frames) {
        $dimension = if($frame[0] -eq 256){0}else{$frame[0]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame[1].Length); $writer.Write([uint32]$offset)
        $offset += $frame[1].Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame[1])}
} finally { $writer.Dispose();$stream.Dispose() }
Write-Output 'Created Sound Manager icon at 16, 24, 32, 48, 64, 128 and 256 pixels.'
