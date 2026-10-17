@echo off
setlocal

title Disturn Installer Builder

cd /d "%~dp0"

echo =====================================
echo        Disturn Installer Build
echo =====================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Build-Installer.ps1"

echo.
if errorlevel 1 (
    echo [ERROR] Build failed.
) else (
    echo [SUCCESS] Installer build completed.
    echo Output: artifacts\installer\
)

echo.
pause