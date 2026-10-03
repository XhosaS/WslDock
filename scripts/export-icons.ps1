# Rebuild the vector sources, PNGs and multi-resolution ICOs from one geometry spec.
# Run with Windows PowerShell -STA; no image processing packages are required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$design = Join-Path $root 'artifacts\branding'
$assets = Join-Path $root 'src\WslDock\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
New-Item -ItemType Directory -Path $design -Force | Out-Null
$spec = Get-Content -LiteralPath (Join-Path $root 'assets\branding\icon-spec.json') -Raw | ConvertFrom-Json
function Brush($name) {
    if (-not $name) { return $null }
    if ($name.StartsWith('#')) { return [Windows.Media.BrushConverter]::new().ConvertFromString($name) }
    $colors = $spec.gradients.$name
    return [Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($colors[0]), [Windows.Media.ColorConverter]::ConvertFromString($colors[1]), 90)
}
function Render-Icon($variant, [int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    foreach ($shape in $spec.variants.$variant) {
        $pen = $null
        if ($shape.stroke) {
            $pen = [Windows.Media.Pen]::new((Brush $shape.stroke), $shape.strokeWidth)
            $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
        }
        $fill = Brush $shape.fill
        if ($shape.type -eq 'rect') {
            $drawing.DrawRoundedRectangle($fill, $pen, [Windows.Rect]::new($shape.x,$shape.y,$shape.width,$shape.height),$shape.radius,$shape.radius)
        } else { $drawing.DrawGeometry($fill,$pen,[Windows.Media.Geometry]::Parse($shape.data)) }
    }
    $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new(); $encoder.Save($stream)
    $bytes = $stream.ToArray(); $stream.Dispose()
    return ,$bytes
}
function Write-Ico($variant, $file) {
    $sizes = @(16,20,24,32,40,48,64,128,256)
    $frames = @($sizes | ForEach-Object { ,(Render-Icon $variant $_) })
    $stream = [IO.File]::Create($file); $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dim = if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
            $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([UInt16]1); $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frames[$i].Length); $writer.Write([UInt32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $stream.Dispose() }
}
foreach ($variant in @('app','tray-white','tray-light')) {
    $svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" fill="none"><defs>'
    foreach ($gradient in $spec.gradients.PSObject.Properties) {
        $svg += '<linearGradient id="'+$gradient.Name+'" x1="0" y1="0" x2="0" y2="1"><stop stop-color="'+$gradient.Value[0]+'"/><stop offset="1" stop-color="'+$gradient.Value[1]+'"/></linearGradient>'
    }
    $svg += '</defs>'
    foreach ($shape in $spec.variants.$variant) {
        $fill = if (-not $shape.fill) {'none'} elseif ($shape.fill.StartsWith('#')) {$shape.fill} else {'url(#'+$shape.fill+')'}
        $svg += if ($shape.type -eq 'rect') {'<rect x="'+$shape.x+'" y="'+$shape.y+'" width="'+$shape.width+'" height="'+$shape.height+'" rx="'+$shape.radius+'"'} else {'<path d="'+$shape.data+'"'}
        $svg += ' fill="'+$fill+'"'
        if ($shape.stroke) { $svg += ' stroke="'+$shape.stroke+'" stroke-width="'+$shape.strokeWidth+'" stroke-linecap="round" stroke-linejoin="round"' }
        $svg += '/>'
    }
    $svg += '</svg>'
    [IO.File]::WriteAllText((Join-Path $design ($variant+'.svg')),$svg,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes((Join-Path $design ($variant+'.png')),(Render-Icon $variant 512))
    foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        [IO.File]::WriteAllBytes((Join-Path $design ($variant+'-'+$size+'.png')),(Render-Icon $variant $size))
    }
}
Write-Ico 'app' (Join-Path $assets 'WslDock.ico')
Write-Ico 'tray-white' (Join-Path $assets 'TrayWhite.ico')
Write-Ico 'tray-light' (Join-Path $assets 'TrayLight.ico')
Copy-Item -LiteralPath (Join-Path $design 'app.png') -Destination (Join-Path $assets 'WslDock.png') -Force
foreach ($file in @('WslDock.ico','TrayWhite.ico','TrayLight.ico')) {
    Copy-Item -LiteralPath (Join-Path $assets $file) -Destination (Join-Path $design $file) -Force
}
Write-Output 'Exported SVG, PNG, and nine-resolution ICO assets.'
