# Builds PcTidy.ico (16, 20, 24, 32, 40, 48, 64, 128, 256 px, PNG-compressed frames) from the XAML logo in
# src\PcTidy.UI\Themes\Logo.xaml. Run it again after changing the logo, then commit the .ico:
#   powershell -ExecutionPolicy Bypass -File "src\PcTidy.App\Assets\make-icon.ps1"
# Only writes the one .ico next to this script.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$logoXaml = Join-Path $here '..\..\PcTidy.UI\Themes\Logo.xaml'
$out = Join-Path $here 'PcTidy.ico'

$dict = [System.Windows.Markup.XamlReader]::Parse((Get-Content -Raw -LiteralPath $logoXaml))
$image = $dict['TidyLogo']
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$frames = foreach ($s in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($image, (New-Object System.Windows.Rect 0, 0, $s, $s))
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    , @{ Size = $s; Bytes = $ms.ToArray() }
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)   # ICONDIR
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {                                                     # ICONDIRENTRY
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$f.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Close()
Write-Output "Wrote $out ($($frames.Count) sizes)"
