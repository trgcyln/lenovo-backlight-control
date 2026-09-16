@echo off
echo ========================================================
echo   LenovoBacklight - Uninstaller
echo ========================================================
echo.

cd /d "%~dp0"

if exist "bin\LenovoBacklight.exe" (
    bin\LenovoBacklight.exe uninstall
) else if exist "%LOCALAPPDATA%\LenovoBacklight\LenovoBacklight.exe" (
    "%LOCALAPPDATA%\LenovoBacklight\LenovoBacklight.exe" uninstall
) else (
    echo [INFO] LenovoBacklight installation not found.
)

echo.
pause
