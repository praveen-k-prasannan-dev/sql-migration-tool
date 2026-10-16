<#
.SYNOPSIS
    Builds the MSIX package for the Microsoft Store (and for local testing).

.DESCRIPTION
    1. dotnet publish (self-contained, win-x64) into Packaging\out\layout
    2. copies the logo assets and fills in Package.appxmanifest
       (identity from StoreIdentity.json, version from the project file)
    3. builds resources.pri (makepri) so scale-specific logos resolve
    4. packs Packaging\out\SqlMigrationTool_<version>_x64.msix (makeappx)

    The Store signs packages itself, so the .msix is uploaded unsigned.

.PARAMETER Register
    After building, registers the unpacked layout for the current user so the
    packaged app can be tested locally (needs Windows Developer Mode; no
    certificate needed). Remove it again with:
        Get-AppxPackage *SQLMigrationTool* | Remove-AppxPackage

.EXAMPLE
    .\Build-Msix.ps1
    .\Build-Msix.ps1 -Register
#>
param(
    [switch]$Register,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root     = Split-Path $PSScriptRoot -Parent                     # ...\SqlMigrationTool (solution folder)
$project  = Join-Path $root "SqlMigrationTool\SqlMigrationTool.csproj"
$out      = Join-Path $PSScriptRoot "out"
$layout   = Join-Path $out "layout"

# ── Tools from the newest Windows 10/11 SDK ──────────────────────────────────
$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
          Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } |
          Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $sdkBin) { throw "Windows SDK (makeappx.exe) not found. Install the Windows 10/11 SDK." }
$makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"
$makepri  = Join-Path $sdkBin.FullName "x64\makepri.exe"
Write-Host "Windows SDK $($sdkBin.Name)"

# ── Version and identity ─────────────────────────────────────────────────────
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> in $project" }
$msixVersion = ([version]$version).ToString(3) + ".0"            # Store requires the 4th part to be 0

$identity = Get-Content (Join-Path $PSScriptRoot "StoreIdentity.json") -Raw | ConvertFrom-Json
if ($identity.Publisher -like "*-Dev") {
    Write-Warning "StoreIdentity.json still holds placeholder values: fine for local testing, not for the Store."
}

# ── Publish ──────────────────────────────────────────────────────────────────
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host "Publishing $version ..."
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false `
    --configfile (Join-Path $root "NuGet.Config") -o $layout -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── Manifest and assets ──────────────────────────────────────────────────────
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $layout "Assets") -Recurse
$manifest = (Get-Content (Join-Path $PSScriptRoot "Package.appxmanifest") -Raw).
    Replace('$IdentityName$',         [Security.SecurityElement]::Escape($identity.IdentityName)).
    Replace('$Publisher$',            [Security.SecurityElement]::Escape($identity.Publisher)).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
    Replace('$Version$',              $msixVersion)
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding UTF8

# ── Resource index (scale-qualified logos) ───────────────────────────────────
$priConfig = Join-Path $out "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

# ── Pack ─────────────────────────────────────────────────────────────────────
$msix = Join-Path $out "SqlMigrationTool_${msixVersion}_x64.msix"
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }
Write-Host "Package: $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB)"

if ($Register) {
    Get-AppxPackage -Name $identity.IdentityName | Remove-AppxPackage
    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml")
    Write-Host "Registered for local testing: start 'SQL Migration Tool' from the Start menu."
}
