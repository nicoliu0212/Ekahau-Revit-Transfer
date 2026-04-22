@echo off
REM ─────────────────────────────────────────────────────────────────
REM  Manual install for Revit 2027 — UNSIGNED third-party add-in
REM  Right-click → "Run as administrator"
REM
REM  Revit 2027 add-in path rules:
REM
REM    OK   C:\Program Files\Autodesk\Revit\Addins\2027\
REM         (third-party add-ins, signed or unsigned, all-users)
REM
REM    OK   %APPDATA%\Autodesk\Revit\Addins\2027\
REM         (third-party add-ins, current-user)
REM
REM    NO   C:\Program Files\Autodesk\Revit 2027\AddIns\
REM         (RESERVED for code-signed Autodesk-internal add-ins.
REM          Unsigned manifests there are rejected with:
REM          "is not signed as internal addin")
REM
REM    NO   C:\ProgramData\Autodesk\Revit\Addins\2027\
REM         (Pre-2027 path; explicitly rejected by Revit 2027:
REM          "All-users Add-in manifest files must be installed to:
REM           C:\Program Files\Autodesk\Revit\Addins\2027")
REM ─────────────────────────────────────────────────────────────────

setlocal
set SRC=%~dp0EkahauRevitPlugin\bin\Release\net10.0-windows
set DST=C:\Program Files\Autodesk\Revit\Addins\2027

echo.
echo === Cleaning up wrong / stale install locations ===
for %%P in (
    "C:\ProgramData\Autodesk\Revit\Addins\2027\EkahauRevitPlugin.addin"
    "C:\ProgramData\Autodesk\Revit\Addins\2027\EkahauWiFiTools"
    "C:\Program Files\Autodesk\Revit 2027\AddIns\EkahauWiFiTools"
) do (
    if exist %%P (
        echo Removing %%P
        rmdir /S /Q %%P 2>nul
        del /F /Q %%P 2>nul
    )
)

echo.
echo === Installing to correct path: %DST% ===
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
