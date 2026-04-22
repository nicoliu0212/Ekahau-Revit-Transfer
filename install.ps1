# ─────────────────────────────────────────────────────────────────────
#  Ekahau WiFi Tools — multi-version installer
#
#  Usage:
#    .\install.ps1                     # default build dir, current user
#    .\install.ps1 -BuildDir "C:\..."  # custom output root
#    .\install.ps1 -Scope CurrentUser  # %APPDATA%       (default, no admin)
#    .\install.ps1 -Scope AllUsers     # all-users path  (needs admin)
#
#  All-users path differs by Revit version:
#    2023-2026 → C:\ProgramData\Autodesk\Revit\Addins\YYYY
#    2027      → C:\Program Files\Autodesk\Revit\Addins\2027
#                (NOT C:\Program Files\Autodesk\Revit 2027\AddIns —
#                 that's reserved for signed Autodesk add-ins)
#
#  Layout for all versions: layered
#    Addins\YYYY\EkahauRevitPlugin.addin
#    Addins\YYYY\EkahauWiFiTools\EkahauRevitPlugin.dll
# ─────────────────────────────────────────────────────────────────────
param(
    [string]$BuildDir = ".\EkahauRevitPlugin\bin\Release",
    [ValidateSet("CurrentUser", "AllUsers")]
    [string]$Scope = "CurrentUser"
)

$ErrorActionPreference = "Stop"

# Resolve the add-in root for a given Revit version.
function Get-AddinRoot([string]$ver, [string]$scope) {
    if ($scope -eq "CurrentUser") {
        return Join-Path $env:APPDATA "Autodesk\Revit\Addins"
    }
    if ([int]$ver -ge 2027) {
        # Revit 2027+ moved the all-users root to Program Files
        return Join-Path $env:ProgramFiles "Autodesk\Revit\Addins"
    }
    return Join-Path $env:ProgramData "Autodesk\Revit\Addins"
}

# Map each Revit version to the build framework folder.
$revitVersions = [ordered]@{
    "2023" = "net48"
    "2024" = "net48"
    "2025" = "net8.0-windows"
    "2026" = "net8.0-windows"
    "2027" = "net10.0-windows"
}

$installed = 0
$skipped   = 0

foreach ($ver in $revitVersions.Keys) {
    $framework = $revitVersions[$ver]
    $addinRoot = Get-AddinRoot $ver $Scope
    $addinDir  = Join-Path $addinRoot $ver

    # For AllUsers + 2027, the parent path may not exist by default.
    # Create it so Revit 2027 can find our manifest.
    if (-not (Test-Path $addinDir)) {
        if ($Scope -eq "AllUsers") {
            try {
                New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
            } catch {
                Write-Host "Revit $ver  cannot create $addinDir (admin required) — skipping" -ForegroundColor Yellow
                $skipped++
                continue
            }
        } else {
            Write-Host "Revit $ver  not detected ($addinDir) — skipping" -ForegroundColor Yellow
            $skipped++
            continue
        }
    }

    $sourceDir = Join-Path $BuildDir $framework
    $dllPath   = Join-Path $sourceDir "EkahauRevitPlugin.dll"
    $addinSrc  = Join-Path $sourceDir "EkahauRevitPlugin.addin"
    if (-not (Test-Path $dllPath)) {
        Write-Host "Revit $ver  build missing ($dllPath) — skipping" -ForegroundColor Yellow
        $skipped++
        continue
    }

    $targetDir = Join-Path $addinDir "EkahauWiFiTools"
    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    # Layered layout: manifest at the version root, DLL + deps in subfolder
    Copy-Item $addinSrc $addinDir -Force
    Copy-Item $dllPath  $targetDir -Force

    $deps = Join-Path $sourceDir "EkahauRevitPlugin.deps.json"
    if (Test-Path $deps) { Copy-Item $deps $targetDir -Force }

    # Net48 brings supporting BCL DLLs (System.Text.Json etc) — copy them too
    Get-ChildItem -Path $sourceDir -Filter "*.dll" -File |
        Where-Object { $_.Name -ne "EkahauRevitPlugin.dll" } |
        ForEach-Object { Copy-Item $_.FullName $targetDir -Force }

    Write-Host "Revit $ver  installed   ($framework) → $addinDir" -ForegroundColor Green
    $installed++
}

Write-Host ""
Write-Host "Done.  Installed: $installed   Skipped: $skipped" -ForegroundColor Cyan
