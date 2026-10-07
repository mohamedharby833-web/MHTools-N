@echo off
REM ---------------------------------------------------------------------------
REM  Runs build-installer.ps1 without touching the machine's PowerShell
REM  execution policy.
REM
REM  Windows blocks downloaded .ps1 files by default, and a script that came out
REM  of a zip is marked as downloaded. -ExecutionPolicy Bypass applies to this
REM  one process only, so nothing about the system changes.
REM
REM  Double-click this file, or run it from a terminal.
REM ---------------------------------------------------------------------------

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*

if errorlevel 1 (
    echo.
    echo Build failed - the messages above say why.
    pause
    exit /b 1
)

echo.
pause
