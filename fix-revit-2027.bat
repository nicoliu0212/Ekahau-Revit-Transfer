@echo off
REM ─────────────────────────────────────────────────────────────────
REM  Manual install for Revit 2027 — per-user (no admin required)
REM  Just double-click this file.  No UAC prompt.
REM
REM  Installs to:
REM    %APPDATA%\Autodesk\Revit\Addins\2027\
REM
REM  Removes any prior installs from system locations (safe to re-run).
REM ─────────────────────────────────────────────────────────────────

setlocal
set SRC=%~dp0EkahauRevitPlugin\bin\Release\net10.0-windows
set DST=%APPDATA%\Autodesk\Revit\Addins\2027

echo.
echo === Removing any prior installs from other locations ===
for %%P in (
    "C:\ProgramData\Autodesk\Revit\Addins\2027\EkahauRevitPlugin.addin"
    "C:\ProgramData\Autodesk\Revit\Addins\2027\EkahauWiFiTools"
    "C:\Program Files\Autodesk\Revit\Addins\2027\EkahauRevitPlugin.addin"
    "C:\Program Files\Autodesk\Revit\Addins\2027\EkahauWiFiTools"
    "C:\Program Files\Autodesk\Revit 2027\AddIns\EkahauWiFiTools"
) do (
    if exist %%P (
        echo Removing %%P
        rmdir /S /Q %%P 2>nul
        del /F /Q %%P 2>nul
    )
)

echo.
echo === Installing to %DST% ===
if not exist "%DST%" mkdir "%DST%"
if not exist "%DST%\EkahauWiFiTools" mkdir "%DST%\EkahauWiFiTools"

REM Layered layout — manifest in version dir, DLL in EkahauWiFiTools subfolder
copy /Y "%SRC%\EkahauRevitPlugin.addin"     "%DST%\"                   >nul
copy /Y "%SRC%\EkahauRevitPlugin.dll"       "%DST%\EkahauWiFiTools\"   >nul
copy /Y "%SRC%\EkahauRevitPlugin.deps.json" "%DST%\EkahauWiFiTools\"   >nul

echo.
echo === Installed files ===
dir /S /B "%DST%"

echo.
echo === Done.  Restart Revit 2027 and check the WiFi Tools tab. ===
pause
