@echo off
echo ========================================================
echo   Building LenovoBacklight
echo ========================================================

set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found.
    exit /b 1
)

if not exist bin mkdir bin

echo Compiling using: %CSC%
"%CSC%" /nologo /target:exe /out:bin\LenovoBacklight.exe /r:System.dll,System.Windows.Forms.dll,System.Drawing.dll src\LenovoBacklight.cs src\Properties\AssemblyInfo.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo.
echo [SUCCESS] Binary built successfully: bin\LenovoBacklight.exe
