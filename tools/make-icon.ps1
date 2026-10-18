# Draws the Thunderstore icon (256 x 256 PNG) in the style of Void Crew's terminals: a light bezel, a dark screen with
# gold meander bands, two METEM ships side by side (one BLUE, one RED), and the title.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 [-Out thunderstore\icon.png]
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\thunderstore\icon.png")
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

$scale = 4
$size = 256
$big = New-Object System.Drawing.Bitmap ($size * $scale), ($size * $scale)
$g = [System.Drawing.Graphics]::FromImage($big)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.ScaleTransform($scale, $scale)

function C([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Poly([float[]]$xy) {
    $pts = New-Object 'System.Drawing.PointF[]' ($xy.Length / 2)
    for ($i = 0; $i -lt $pts.Length; $i++) { $pts[$i] = New-Object System.Drawing.PointF $xy[2 * $i], $xy[2 * $i + 1] }
    return $pts
}

# ---------------------------------------------------------------- bezel and screen
$bezel = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 0, 256, 256), (C "#F1EEE6"), (C "#C9C3B4"), 90
$g.FillRectangle($bezel, 0, 0, 256, 256)
$bracket = New-Object System.Drawing.SolidBrush (C "#8E8A82")
foreach ($by in 34, 186) {
    $g.FillPath($bracket, (RoundRect 2 $by 9 36 3))
    $g.FillPath($bracket, (RoundRect 245 $by 9 36 3))
}
$screenPath = RoundRect 14 14 228 228 9
$screen = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 14, 14, 228, 228), (C "#20252A"), (C "#0B0D0F"), 90
$g.FillPath($screen, $screenPath)
$g.DrawPath((New-Object System.Drawing.Pen (C "#000000" 120), 2), $screenPath)

# ---------------------------------------------------------------- meander bands
$goldPen = New-Object System.Drawing.Pen (C "#C99A36"), 1.15
function Meander([float]$y) {
    $unit = 10.0
    $h = 7.0
    for ($x = 24.0; $x + $unit -le 233; $x += $unit) {
        $pts = Poly @($x, ($y + $h), $x, $y, ($x + 7), $y, ($x + 7), ($y + 5), ($x + 3), ($y + 5), ($x + 3), ($y + 2.5), ($x + 5), ($y + 2.5))
        $g.DrawLines($goldPen, $pts)
        $g.DrawLine($goldPen, $x, ($y + $h), ($x + $unit), ($y + $h))
    }
}
Meander 21
Meander 226

# ---------------------------------------------------------------- two ships, nose up
function Ship([float]$cx, [float]$cy, [string]$hull, [string]$glow) {
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(($cx - 46), ($cy - 58), 92, 116)
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glowBrush.CenterColor = C $glow 120
    $glowBrush.SurroundColors = @(C $glow 0)
    $g.FillPath($glowBrush, $glowPath)
    # Hull: a long hexagonal body with swept wings and an engine block.
    $body = Poly @($cx, ($cy - 46), ($cx + 11), ($cy - 30), ($cx + 13), ($cy + 20), ($cx + 8), ($cy + 38), ($cx - 8), ($cy + 38), ($cx - 13), ($cy + 20), ($cx - 11), ($cy - 30))
    $wingL = Poly @(($cx - 12), ($cy - 6), ($cx - 34), ($cy + 18), ($cx - 32), ($cy + 28), ($cx - 12), ($cy + 22))
    $wingR = Poly @(($cx + 12), ($cy - 6), ($cx + 34), ($cy + 18), ($cx + 32), ($cy + 28), ($cx + 12), ($cy + 22))
    $hullBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF ($cx - 34), ($cy - 46), 68, 84), (C "#E9E4D8"), (C "#9C978C"), 0
    $edge = New-Object System.Drawing.Pen (C "#2A2E33"), 1.4
    foreach ($part in $wingL, $wingR, $body) {
        $g.FillPolygon($hullBrush, $part)
        $g.DrawPolygon($edge, $part)
    }
    # Team stripe and cockpit.
    $g.FillRectangle((New-Object System.Drawing.SolidBrush (C $hull)), ($cx - 4), ($cy - 20), 8, 40)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C "#7FD8FF")), ($cx - 4), ($cy - 36), 8, 9)
    # Engines.
    $flame = New-Object System.Drawing.SolidBrush (C $glow 220)
    $g.FillEllipse($flame, ($cx - 9), ($cy + 37), 7, 9)
    $g.FillEllipse($flame, ($cx + 2), ($cy + 37), 7, 9)
}
Ship 84 112 "#3C8DFF" "#3C8DFF"
Ship 172 112 "#FF4A3C" "#FF6A3C"

# ---------------------------------------------------------------- title
$font = New-Object System.Drawing.Font "Segoe UI", 30, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$shadow = New-Object System.Drawing.SolidBrush (C "#000000" 160)
$g.DrawString("FLEET", $font, $shadow, (New-Object System.Drawing.RectangleF 15, 180, 228, 40), $format)
$g.DrawString("FLEET", $font, (New-Object System.Drawing.SolidBrush (C "#E3AE3C")), (New-Object System.Drawing.RectangleF 14, 178, 228, 40), $format)

# ---------------------------------------------------------------- downscale and save
$icon = New-Object System.Drawing.Bitmap $size, $size
$small = [System.Drawing.Graphics]::FromImage($icon)
$small.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$small.DrawImage($big, 0, 0, $size, $size)
$dir = Split-Path $Out -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory $dir | Out-Null }
$icon.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose(); $g.Dispose(); $big.Dispose(); $icon.Dispose()
Write-Output "Wrote $Out"
