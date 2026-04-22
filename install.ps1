# ─────────────────────────────────────────────────────────────────────
#  Ekahau WiFi Tools — per-user installer
#
#  Installs everything under %APPDATA%\Autodesk\Revit\Addins\YYYY\
#  No admin / UAC required.  Files are only visible to the installing
#  user — does not write to Program Files or ProgramData.
#
#  Usage:
#    .\install.ps1                     # default build dir
#    .\install.ps1 -BuildDir "C:\..."  # custom build dir
#
#  Layout per Revit version:
#    Addins\YYYY\EkahauRevitPlugin.addin
#    Addins\YYYY\EkahauWiFiTools\EkahauRevitPlugin.dll
#    Addins\YYYY\EkahauWiFiTools\<supporting BCL DLLs for net48>
# ─────────────────────────────────────────────────────────────────────
param(
    [string]$BuildDir = ".\EkahauRevitPlugin\bin\Release"
)

$ErrorActionPreference = "Stop"

# Map each Revit version to the build framework folder.
$revitVersions = [ordered]@{
    "2023" = "net48"
    "2024" = "net48"
    "2025" = "net8.0-windows"
    "2026" = "net8.0-windows"
    "2027" = "net10.0-windows"
}

$addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$installed = 0
$skipped   = 0

foreach ($ver in $revitVersions.Keys) {
    $framework = $revitVersions[$ver]
    $addinDir  = Join-Path $addinRoot $ver

    # Per-user path always allowed — create it if missing
    if (-not (Test-Path $addinDir)) {
        try {
            New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
        } catch {
            Write-Host "Revit $ver  cannot create $addinDir — skipping" -ForegroundColor Yellow
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

    # Net48 brings supporting BCL DLLs (System.Text.Json etc.) — copy them too
    Get-ChildItem -Path $sourceDir -Filter "*.dll" -File |
        Where-Object { $_.Name -ne "EkahauRevitPlugin.dll" } |
        ForEach-Object { Copy-Item $_.FullName $targetDir -Force }

    Write-Host "Revit $ver  installed   ($framework) -> $addinDir" -ForegroundColor Green
    $installed++
}

Write-Host ""
Write-Host "Done.  Installed: $installed   Skipped: $skipped" -ForegroundColor Cyan
Write-Host "Open Revit and look for the 'WiFi Tools' ribbon tab." -ForegroundColor Cyan
