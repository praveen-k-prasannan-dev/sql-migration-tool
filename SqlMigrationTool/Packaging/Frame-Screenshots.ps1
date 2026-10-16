<#
.SYNOPSIS
    Prepares app screenshots for the Microsoft Store listing.

.DESCRIPTION
    Store desktop screenshots must be at least 1366x768. App windows are smaller,
    so each screenshot is centred (with a soft shadow) on a 1920x1080 canvas in the
    logo's navy. Screenshots larger than the canvas are scaled down to fit.

.EXAMPLE
    .\Frame-Screenshots.ps1 -Source ..\..\screenshots -OutDir out\store-screenshots
#>
param(
    [Parameter(Mandatory)] [string]$Source,
    [string]$OutDir = (Join-Path $PSScriptRoot "out\store-screenshots"),
    [int]$Width  = 1920,
    [int]$Height = 1080
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$navy   = [System.Drawing.Color]::FromArgb(255, 0x03, 0x21, 0x4B)
$shadow = [System.Drawing.Color]::FromArgb(90, 0, 0, 0)

Get-ChildItem $Source -Include *.png, *.jpg -File -Recurse | ForEach-Object {
    $shot  = [System.Drawing.Image]::FromFile($_.FullName)
    $scale = [Math]::Min(1.0, [Math]::Min(($Width * 0.85) / $shot.Width, ($Height * 0.85) / $shot.Height))
    $w = [int]($shot.Width * $scale); $h = [int]($shot.Height * $scale)
    $x = [int](($Width - $w) / 2);   $y = [int](($Height - $h) / 2)

    $canvas = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear($navy)
    $brush = New-Object System.Drawing.SolidBrush($shadow)
    $g.FillRectangle($brush, $x + 10, $y + 12, $w, $h)
    $g.DrawImage($shot, $x, $y, $w, $h)

    $target = Join-Path $OutDir ([IO.Path]::GetFileNameWithoutExtension($_.Name) + ".png")
    $canvas.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "  $($_.Name) -> $target"

    $brush.Dispose(); $g.Dispose(); $canvas.Dispose(); $shot.Dispose()
}
