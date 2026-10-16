<#
.SYNOPSIS
    Generates the MSIX / Microsoft Store logo assets from the app logo.

.DESCRIPTION
    The source logo is a rounded-square icon on a white square canvas. Each asset
    is cut to a rounded square with transparent corners (so no white frame shows on
    dark tiles or the taskbar); wide tiles centre the icon on the logo's own navy.
    Re-run whenever the logo changes:  .\Generate-Assets.ps1
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\SqlMigrationTool\Resources\image1.png"),
    [string]$OutDir = (Join-Path $PSScriptRoot "Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$navy   = [System.Drawing.Color]::FromArgb(255, 0x03, 0x21, 0x4B)   # logo background
$logo   = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$size) {
    $r    = $size * 0.11          # corner radius matching the logo's own rounding
    $d    = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x,              $y,              $d, $d, 180, 90)
    $path.AddArc($x + $size - $d, $y,              $d, $d, 270, 90)
    $path.AddArc($x + $size - $d, $y + $size - $d, $d, $d,   0, 90)
    $path.AddArc($x,              $y + $size - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function Save-Asset([string]$name, [int]$width, [int]$height, [bool]$fillNavy) {
    $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($fillNavy) { $g.Clear($navy) }

    # Square icon, centred; trim the white canvas margin (~0.5%) before scaling.
    $size = [Math]::Min($width, $height)
    $x    = ($width  - $size) / 2
    $y    = ($height - $size) / 2
    $path = New-RoundedPath $x $y $size
    $g.SetClip($path)
    $inset = $logo.Width * 0.005
    $src   = New-Object System.Drawing.RectangleF($inset, $inset, ($logo.Width - 2 * $inset), ($logo.Height - 2 * $inset))
    $dst   = New-Object System.Drawing.RectangleF($x, $y, $size, $size)
    $g.DrawImage($logo, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)

    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $name  ${width}x$height"
}

Write-Host "Generating assets in $OutDir"

# App list / taskbar icon
foreach ($s in @(@{n="scale-100";p=44}, @{n="scale-125";p=55}, @{n="scale-150";p=66}, @{n="scale-200";p=88}, @{n="scale-400";p=176})) {
    Save-Asset "Square44x44Logo.$($s.n).png" $s.p $s.p $false
}
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Asset "Square44x44Logo.targetsize-$t.png"                  $t $t $false
    Save-Asset "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $false
}

# Start tiles
foreach ($s in @(@{n="scale-100";p=150}, @{n="scale-200";p=300}, @{n="scale-400";p=600})) {
    Save-Asset "Square150x150Logo.$($s.n).png" $s.p $s.p $false
}
foreach ($s in @(@{n="scale-100";f=1}, @{n="scale-200";f=2}, @{n="scale-400";f=4})) {
    Save-Asset "Wide310x150Logo.$($s.n).png" (310 * $s.f) (150 * $s.f) $true
}

# Store / package logo
foreach ($s in @(@{n="scale-100";p=50}, @{n="scale-200";p=100}, @{n="scale-400";p=200})) {
    Save-Asset "StoreLogo.$($s.n).png" $s.p $s.p $false
}

$logo.Dispose()
Write-Host "Done."
