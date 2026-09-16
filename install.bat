@echo off
echo ========================================================
echo   LenovoBacklight - Auto-Wake Installer
echo ========================================================
echo.

cd /d "%~dp0"

if not exist "bin\LenovoBacklight.exe" (
    echo Building LenovoBacklight.exe...
    call build.bat
    if errorlevel 1 (
        echo [ERROR] Build failed. Aborting installation.
        pause
        exit /b 1
    )
)

echo Installing LenovoBacklight service (Wake Level 2 - High)...
bin\LenovoBacklight.exe install 2

echo.
pause
