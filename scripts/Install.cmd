@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-PerUser.ps1"
if errorlevel 1 (
    echo.
    echo Installation fehlgeschlagen. Details siehe oben.
    pause
    exit /b 1
)
